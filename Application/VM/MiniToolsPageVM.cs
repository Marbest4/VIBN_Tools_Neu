using System.Collections.ObjectModel;
using System.Windows.Input;
using VIBN_Tools.GlobalClasses;
using VIBN_Tools.MiniTools;
using VIBN_Tools.Settings;

namespace VIBN_Tools.Application.VM;

public sealed class MiniToolsPageVM : MvvmBase
{
    private const string LogArea = "Mini-Tools";
    private readonly FeeSelectionPositioningService _service;
    private readonly FeeConnectionService _connection;
    private readonly ApplicationLogService _log;
    private bool _isBusy;
    private string _statusText =
        "Objekte in FEE auswählen, Bandhöhe festlegen und anschließend positionieren.";
    private int _progressValue;
    private int _progressMaximum = 1;

    public MiniToolsPageVM()
        : this(
            new FeeSelectionPositioningService(new FeeSdkSelectionTransformGateway()),
            Services.Connection ?? new FeeConnectionService(),
            ApplicationLogService.Instance)
    {
    }

    internal MiniToolsPageVM(
        FeeSelectionPositioningService service,
        FeeConnectionService connection,
        ApplicationLogService log)
    {
        _service = service;
        _connection = connection;
        _log = log;
        PositionSelectionCommand = GetCommandBindingAsync(PositionSelectionAsync);
        SelectedBandHeight = BandHeights[0];
        _connection.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(FeeConnectionService.CanUseFeeFeatures))
                OnPropertyChanged(nameof(CanPosition));
        };
    }

    public ICommand PositionSelectionCommand { get; }
    public FeeConnectionService Connection => _connection;
    public ObservableCollection<FeeSelectionPositioningItemResult> Results { get; } = [];

    public IReadOnlyList<BandHeightOption> BandHeights { get; } =
    [
        new(MiniToolsBandHeight.BandHeight1, "Bandhöhe 1"),
        new(MiniToolsBandHeight.BandHeight2, "Bandhöhe 2"),
        new(MiniToolsBandHeight.BandHeight3, "Bandhöhe 3"),
    ];

    public BandHeightOption SelectedBandHeight { get; set; }

    public string XText { get; set; } = "0";
    public string YText { get; set; } = "0";
    public string BandHeight1Text { get; set; } = "0";
    public string BandHeight2Text { get; set; } = "0";
    public string BandHeight3Text { get; set; } = "0";
    public string LengthText { get; set; } = "1";
    public string WidthText { get; set; } = "1";
    public string HeightText { get; set; } = "1";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanPosition));
        }
    }

    public bool CanPosition => !IsBusy && _connection.CanUseFeeFeatures;

    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public int ProgressValue
    {
        get => _progressValue;
        private set
        {
            _progressValue = value;
            OnPropertyChanged();
        }
    }

    public int ProgressMaximum
    {
        get => _progressMaximum;
        private set
        {
            _progressMaximum = Math.Max(1, value);
            OnPropertyChanged();
        }
    }

    private async Task PositionSelectionAsync()
    {
        if (!CanPosition)
        {
            StatusText = IsBusy
                ? "Die vorherige Positionierung läuft noch."
                : FeeConnectionService.MissingConnectionMessage;
            _log.Warning(LogArea, StatusText);
            return;
        }

        if (!TryCreateRequest(out var request, out var validationMessage))
        {
            StatusText = validationMessage;
            _log.Warning(LogArea, StatusText);
            return;
        }

        IsBusy = true;
        ProgressValue = 0;
        ProgressMaximum = 1;
        Results.Clear();
        StatusText = "FEE-Auswahl wird gelesen …";
        try
        {
            var progress = new InlineProgress<FeeSelectionPositioningProgress>(update =>
            {
                ProgressMaximum = update.Total;
                ProgressValue = update.Completed;
                StatusText = update.Message;
            });
            var result = await _service.PositionSelectionAsync(request, progress);
            foreach (var item in result.Items)
                Results.Add(item);

            ProgressMaximum = Math.Max(1, result.SelectedCount);
            ProgressValue = result.SelectedCount;
            StatusText = result.SelectedCount == 0
                ? "In FEE ist kein Objekt ausgewählt. Es wurden keine Änderungen vorgenommen."
                : $"{result.SuccessfulPositionCount} von {result.SelectedCount} Objekten positioniert; " +
                  $"{result.SurfaceCount} Surface-Skalierungen geändert" +
                  (result.FailedCount > 0 ? $"; {result.FailedCount} Fehler." : ".");

            if (result.FailedCount > 0)
                _log.Warning(LogArea, StatusText, string.Join(Environment.NewLine,
                    result.Items
                        .Where(item => !item.PositionChanged || (item.IsSurface && !item.ScaleChanged))
                        .Select(item => item.Message)));
            else
                _log.Information(LogArea, StatusText);
        }
        catch (Exception exception)
        {
            StatusText = $"Positionierung fehlgeschlagen: {exception.Message}";
            _log.Error(LogArea, StatusText, exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal bool TryCreateRequest(
        out FeeSelectionPositioningRequest request,
        out string validationMessage)
    {
        return new FeeSelectionPositioningInput(
                XText,
                YText,
                BandHeight1Text,
                BandHeight2Text,
                BandHeight3Text,
                LengthText,
                WidthText,
                HeightText,
                SelectedBandHeight?.Value ?? MiniToolsBandHeight.BandHeight1)
            .TryCreateRequest(out request, out validationMessage);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

public sealed record BandHeightOption(
    MiniToolsBandHeight Value,
    string DisplayName);
