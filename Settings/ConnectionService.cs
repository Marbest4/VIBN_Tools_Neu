using FS.SDK.Network.API;
using System.Windows.Threading;
using VIBN_Tools.GlobalClasses;

namespace VIBN_Tools.Settings
{
    /// <summary>Polls the FEE SDK state and exposes confirmed connection transitions to the UI.</summary>
    public class FeeConnectionService : NotifyBase
    {
        public const string MissingConnectionMessage = "Keine Verbindung zu FEE vorhanden.";

        private readonly DispatcherTimer _timer;
        private readonly Func<(bool Connected, bool Connecting)> _readConnectionState;

        public event Action Connected;

        /// <summary>The argument is true only for a disconnect requested by this application.</summary>
        public event Action<bool>? Disconnected;

        private long _connectionRevision;
        private bool _disconnectRequested;
        private bool _areModelValidationObjectsCurrent;

        public long ConnectionRevision => _connectionRevision;

        public bool AreModelValidationObjectsCurrent
        {
            get => _areModelValidationObjectsCurrent;
            private set
            {
                if (SetPropertyChange(ref _areModelValidationObjectsCurrent, value))
                    OnPropertyChanged(nameof(ModelValidationUnavailableReason));
            }
        }

        public string? ModelValidationUnavailableReason => !IsConnected
            ? MissingConnectionMessage
            : AreModelValidationObjectsCurrent ? null
                : "Zuerst ModelValidation → Update Objects für diese FEE-Verbindung ausführen.";

        /// <summary>Starts an explicit ModelValidation update in the current connection session.</summary>
        public long BeginModelValidationUpdate()
        {
            CheckConnection();
            AreModelValidationObjectsCurrent = false;
            return _connectionRevision;
        }

        public bool CompleteModelValidationUpdate(long connectionRevision)
        {
            CheckConnection();
            if (!IsConnected || connectionRevision != _connectionRevision || _disconnectRequested)
                return false;
            AreModelValidationObjectsCurrent = true;
            return true;
        }

        public void RequestIntentionalDisconnect()
        {
            _disconnectRequested = true;
            AreModelValidationObjectsCurrent = false;
        }

        public void DisconnectIntentionally()
        {
            RequestIntentionalDisconnect();
            try
            {
                Services.ApiInstance?.Disconnect();
                CheckConnection();
            }
            catch
            {
                _disconnectRequested = false;
                throw;
            }
        }

        public bool LoadFeeDataOnConnect { get; set; }

        private string? _connectedServer;
        private string? _connectedStation;

        public string? ConnectedServer
        {
            get => _connectedServer;
            private set
            {
                if (SetPropertyChange(ref _connectedServer, value))
                    OnPropertyChanged(nameof(ConnectedServerDisplay));
            }
        }

        public string? ConnectedStation
        {
            get => _connectedStation;
            private set
            {
                if (SetPropertyChange(ref _connectedStation, value))
                    OnPropertyChanged(nameof(ConnectedStationDisplay));
            }
        }

        public string ConnectedServerDisplay => IsConnected && !string.IsNullOrWhiteSpace(ConnectedServer)
            ? ConnectedServer
            : "nicht verbunden";

        public string ConnectedStationDisplay => IsConnected && !string.IsNullOrWhiteSpace(ConnectedStation)
            ? ConnectedStation
            : "nicht eingelesen";


        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            private set
            {
                bool changed = SetPropertyChange(ref _isConnected, value);

                if (changed)
                {
                    _connectionRevision++;
                    AreModelValidationObjectsCurrent = false;
                    OnPropertyChanged(nameof(CanUseFeeFeatures));
                    OnPropertyChanged(nameof(UnavailableReason));
                    OnPropertyChanged(nameof(ModelValidationUnavailableReason));
                    OnPropertyChanged(nameof(ConnectedServerDisplay));
                    OnPropertyChanged(nameof(ConnectedStationDisplay));
                }

                if (changed && value)
                {
                    _disconnectRequested = false;
                    Connected?.Invoke();
                }
                else if (changed)
                {
                    var intentional = _disconnectRequested;
                    _disconnectRequested = false;
                    Disconnected?.Invoke(intentional);
                }
            }
        }

        /// <summary>
        /// Central capability used by all UI actions that require a confirmed
        /// Project-Settings connection. It deliberately follows the SDK state,
        /// not merely a completed Connect call.
        /// </summary>
        public bool CanUseFeeFeatures => IsConnected;

        /// <summary>Reason shown by disabled FEE-dependent controls.</summary>
        public string? UnavailableReason =>
            IsConnected ? null : MissingConnectionMessage;

        private bool _isConnecting;
        public bool IsConnecting
        {
            get => _isConnecting;
            private set => SetPropertyChange(ref _isConnecting, value);
        }

        public FeeConnectionService() : this(ReadSdkConnectionState) { }

        internal FeeConnectionService(Func<(bool Connected, bool Connecting)> readConnectionState)
        {
            _readConnectionState = readConnectionState;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += (sender, eventargs) => CheckConnection();
            _timer.Start();
        }

        public void SetConnectionContext(string? server)
        {
            ConnectedServer = string.IsNullOrWhiteSpace(server) ? null : server.Trim();
            ConnectedStation = null;
        }

        public void SetConnectedStation(string? station)
        {
            ConnectedStation = string.IsNullOrWhiteSpace(station) ? null : station.Trim();
        }

        public void ClearConnectionContext()
        {
            ConnectedServer = null;
            ConnectedStation = null;
        }

        /// <summary>
        /// Waits for the state transition reported by the shared FEE client.
        /// This mirrors the confirmed-connect behavior from fdc85b1.
        /// </summary>
        public async Task<bool> WaitForConnectedAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            do
            {
                CheckConnection();
                if (IsConnected)
                    return true;
                await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
            }
            while (DateTimeOffset.UtcNow < deadline);

            CheckConnection();
            return IsConnected;
        }

        /// <summary>Waits until the SDK no longer reports a live remote session.</summary>
        public async Task<bool> WaitForDisconnectedAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            do
            {
                CheckConnection();
                if (!IsConnected && !IsConnecting)
                    return true;
                await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
            }
            while (DateTimeOffset.UtcNow < deadline);

            CheckConnection();
            return !IsConnected && !IsConnecting;
        }

        private void CheckConnection()
        {
            (bool Connected, bool Connecting) state;
            try
            {
                state = _readConnectionState();
            }
            catch (Exception exception)
            {
                if (IsConnected)
                    VIBN_Tools.Application.ApplicationLogService.Instance.Warning(
                        "Project Settings", "FEE-Verbindungsstatus konnte nicht gelesen werden; die Verbindung wird als unterbrochen behandelt.", exception.Message);
                state = (false, false);
            }
            IsConnected = state.Connected;
            IsConnecting = state.Connecting;
        }

        private static (bool Connected, bool Connecting) ReadSdkConnectionState()
        {
            if (Services.ApiInstance is null)
                return (false, false);
            var state = Services.ApiInstance.ApiState;
            return (state == NetworkState.Connected, state == NetworkState.Connecting);
        }
    }
}
