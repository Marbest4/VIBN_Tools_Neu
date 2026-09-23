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

        public event Action Connected;

        public bool LoadFeeDataOnConnect { get; set; }


        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            private set
            {
                bool changed = SetPropertyChange(ref _isConnected, value);

                if (changed)
                {
                    OnPropertyChanged(nameof(CanUseFeeFeatures));
                    OnPropertyChanged(nameof(UnavailableReason));
                }

                if (changed && value)
                {
                    Connected?.Invoke();
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

        public FeeConnectionService()
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += (sender, eventargs) => CheckConnection();
            _timer.Start();
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
            if (Services.ApiInstance is null)
            {
                IsConnected = false;
                IsConnecting = false;
                return;
            }

            // API Call for Connection State
            var state = Services.ApiInstance.ApiState;

            IsConnected = state == NetworkState.Connected;
            IsConnecting = state == NetworkState.Connecting;

        }
    }
}
