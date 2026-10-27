using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace VIBN_Tools.Quality;

/// <summary>
/// Vendor-neutral contract for a WinCC/HMI runtime adapter. The desktop tool
/// deliberately does not pretend that TIA Openness can operate a running HMI.
/// </summary>
public interface IHmiRuntimeAdapter
{
    Task<(bool Success, string Message)> ProbeAsync(CancellationToken cancellationToken);
    Task<string?> ReadTagAsync(string tag, CancellationToken cancellationToken);
    Task WriteTagAsync(string tag, string? value, CancellationToken cancellationToken);
}

/// <summary>Read-only FEE signal monitor used by the HMI closed-loop test.</summary>
public interface IFeeSignalMonitor
{
    Task<string?> ReadAsync(Guid signalGuid, CancellationToken cancellationToken);
}

public interface IFeeSignalCatalog
{
    Task<IReadOnlyList<FeeRuntimeSignal>> LoadAsync(CancellationToken cancellationToken);
}

public sealed record FeeRuntimeSignal(
    Guid Guid,
    string InterfaceName,
    string Tag,
    string Location,
    string IoType);

public sealed class UnavailableFeeSignalRuntime : IFeeSignalCatalog, IFeeSignalMonitor
{
    public Task<IReadOnlyList<FeeRuntimeSignal>> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<FeeRuntimeSignal>>(
            new InvalidOperationException("In diesem Host ist kein FEE-Signaladapter registriert."));

    public Task<string?> ReadAsync(Guid signalGuid, CancellationToken cancellationToken) =>
        Task.FromException<string?>(
            new InvalidOperationException("In diesem Host ist kein FEE-Signaladapter registriert."));
}

/// <summary>
/// Client for the customer/runtime-specific WinCC adapter. One JSON request
/// and response are exchanged per pipe connection.
/// </summary>
public sealed class NamedPipeHmiRuntimeAdapter : IHmiRuntimeAdapter
{
    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;

    public NamedPipeHmiRuntimeAdapter(string pipeName, TimeSpan? connectTimeout = null)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? throw new ArgumentException("Pipe-Name fehlt.", nameof(pipeName))
            : pipeName.Trim();
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(3);
    }

    public async Task<(bool Success, string Message)> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await SendAsync(new HmiRuntimeRequest("probe", null, null), cancellationToken)
                .ConfigureAwait(false);
            return (response.Success, response.Message ?? (response.Success ? "Adapter bereit." : "Adapter nicht bereit."));
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return (false, $"WinCC-Runtime-Adapter nicht erreichbar: {exception.Message}");
        }
    }

    public async Task<string?> ReadTagAsync(string tag, CancellationToken cancellationToken)
    {
        var response = await SendAsync(new HmiRuntimeRequest("read", tag, null), cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response);
        return response.Value;
    }

    public async Task WriteTagAsync(string tag, string? value, CancellationToken cancellationToken)
    {
        var response = await SendAsync(new HmiRuntimeRequest("write", tag, value), cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response);
    }

    private async Task<HmiRuntimeResponse> SendAsync(
        HmiRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(_connectTimeout);
        try
        {
            await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Named Pipe '{_pipeName}' antwortet nicht.");
        }

        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, true, 1024, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request)).ConfigureAwait(false);
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(line)
            ? throw new IOException("Der WinCC-Runtime-Adapter lieferte keine Antwort.")
            : JsonSerializer.Deserialize<HmiRuntimeResponse>(line) ??
              throw new IOException("Antwort des WinCC-Runtime-Adapters ist ungültig.");
    }

    private static void EnsureSuccess(HmiRuntimeResponse response)
    {
        if (!response.Success)
            throw new InvalidOperationException(response.Message ?? "WinCC-Runtime-Operation fehlgeschlagen.");
    }

    private sealed record HmiRuntimeRequest(string Operation, string? Tag, string? Value);
    private sealed record HmiRuntimeResponse(bool Success, string? Value, string? Message);
}

public sealed record HmiClosedLoopTestDefinition(
    string HmiTag,
    string TriggerValue,
    Guid? OutputSignalGuid,
    string? ExpectedOutputValue,
    Guid? FeedbackSignalGuid,
    string? ExpectedFeedbackValue,
    TimeSpan Timeout,
    TimeSpan PollInterval);

public sealed record HmiClosedLoopObservation(
    DateTimeOffset TimestampUtc,
    string Step,
    string Value,
    bool Successful);

public sealed record HmiClosedLoopTestResult(
    bool Success,
    bool HmiValueRestored,
    string Summary,
    IReadOnlyList<HmiClosedLoopObservation> Observations);

/// <summary>
/// Executes HMI tag -> PLC/FEE output -> FEE feedback as a deterministic,
/// bounded workflow. A blank expected value means that a transition from the
/// initial value is required. The original HMI value is restored in all paths.
/// </summary>
public sealed class HmiClosedLoopTestService
{
    private readonly IHmiRuntimeAdapter _hmi;
    private readonly IFeeSignalMonitor _fee;

    public HmiClosedLoopTestService(IHmiRuntimeAdapter hmi, IFeeSignalMonitor fee)
    {
        _hmi = hmi ?? throw new ArgumentNullException(nameof(hmi));
        _fee = fee ?? throw new ArgumentNullException(nameof(fee));
    }

    public async Task<HmiClosedLoopTestResult> RunAsync(
        HmiClosedLoopTestDefinition definition,
        IProgress<HmiClosedLoopObservation>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(definition);
        var observations = new List<HmiClosedLoopObservation>();
        string? originalHmiValue = null;
        bool originalHmiValueRead = false;
        bool restored = false;
        bool success = false;
        string summary = "Test wurde nicht ausgeführt.";

        void Add(string step, string? value, bool successful)
        {
            var item = new HmiClosedLoopObservation(
                DateTimeOffset.UtcNow,
                step,
                value ?? "<null>",
                successful);
            observations.Add(item);
            progress?.Report(item);
        }

        try
        {
            var probe = await _hmi.ProbeAsync(cancellationToken).ConfigureAwait(false);
            Add("HMI-Adapter", probe.Message, probe.Success);
            if (!probe.Success)
                throw new InvalidOperationException(probe.Message);

            originalHmiValue = await _hmi.ReadTagAsync(definition.HmiTag, cancellationToken)
                .ConfigureAwait(false);
            originalHmiValueRead = true;
            Add("HMI-Ausgangswert", originalHmiValue, true);

            var initialOutput = definition.OutputSignalGuid is Guid outputGuid
                ? await _fee.ReadAsync(outputGuid, cancellationToken).ConfigureAwait(false)
                : null;
            var initialFeedback = definition.FeedbackSignalGuid is Guid feedbackGuid
                ? await _fee.ReadAsync(feedbackGuid, cancellationToken).ConfigureAwait(false)
                : null;
            if (definition.OutputSignalGuid is not null)
                Add("FEE-Ausgang vorher", initialOutput, true);
            if (definition.FeedbackSignalGuid is not null)
                Add("FEE-Rückmeldung vorher", initialFeedback, true);

            await _hmi.WriteTagAsync(definition.HmiTag, definition.TriggerValue, cancellationToken)
                .ConfigureAwait(false);
            Add("HMI-Trigger", definition.TriggerValue, true);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(definition.Timeout);
            string? output = initialOutput;
            string? feedback = initialFeedback;
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (definition.OutputSignalGuid is Guid outputId)
                    output = await _fee.ReadAsync(outputId, timeout.Token).ConfigureAwait(false);
                if (definition.FeedbackSignalGuid is Guid feedbackId)
                    feedback = await _fee.ReadAsync(feedbackId, timeout.Token).ConfigureAwait(false);

                var outputReached = definition.OutputSignalGuid is null ||
                    IsReached(output, initialOutput, definition.ExpectedOutputValue);
                var feedbackReached = definition.FeedbackSignalGuid is null ||
                    IsReached(feedback, initialFeedback, definition.ExpectedFeedbackValue);
                if (outputReached && feedbackReached)
                {
                    if (definition.OutputSignalGuid is not null)
                        Add("FEE-Ausgang nach Trigger", output, true);
                    if (definition.FeedbackSignalGuid is not null)
                        Add("FEE-Rückmeldung nach Trigger", feedback, true);
                    success = true;
                    summary = "HMI-Trigger und alle konfigurierten FEE-Signalreaktionen wurden bestätigt.";
                    break;
                }

                await Task.Delay(definition.PollInterval, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Add("Zeitüberschreitung", "Erwartete FEE-Signalreaktion blieb aus.", false);
            summary = "Zeitüberschreitung: Mindestens eine erwartete FEE-Signalreaktion blieb aus.";
        }
        catch (Exception exception)
        {
            Add("Fehler", exception.Message, false);
            summary = exception.Message;
        }
        finally
        {
            if (originalHmiValueRead)
            {
                try
                {
                    using var restoreTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _hmi.WriteTagAsync(definition.HmiTag, originalHmiValue, restoreTimeout.Token)
                        .ConfigureAwait(false);
                    var restoredValue = await _hmi.ReadTagAsync(definition.HmiTag, restoreTimeout.Token)
                        .ConfigureAwait(false);
                    restored = Same(restoredValue, originalHmiValue);
                    Add("HMI-Rücksetzung", restoredValue, restored);
                }
                catch (Exception exception)
                {
                    Add("HMI-Rücksetzung", exception.Message, false);
                }
            }

        }

        return BuildResult();

        HmiClosedLoopTestResult BuildResult()
        {
            if (originalHmiValueRead && !restored)
            {
                success = false;
                summary += " Der ursprüngliche HMI-Wert konnte nicht verifiziert wiederhergestellt werden.";
            }
            return new(success, restored, summary, observations.ToArray());
        }
    }

    public static bool WasRestored(IEnumerable<HmiClosedLoopObservation> observations) =>
        observations.LastOrDefault(item => item.Step == "HMI-Rücksetzung")?.Successful == true;

    private static void Validate(HmiClosedLoopTestDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.HmiTag))
            throw new ArgumentException("Ein HMI-Runtime-Tag ist erforderlich.", nameof(definition));
        if (definition.OutputSignalGuid is null && definition.FeedbackSignalGuid is null)
            throw new ArgumentException("Mindestens ein FEE-Ausgang oder eine FEE-Rückmeldung ist erforderlich.", nameof(definition));
        if (definition.Timeout <= TimeSpan.Zero || definition.PollInterval <= TimeSpan.Zero)
            throw new ArgumentException("Timeout und Abfrageintervall müssen größer als null sein.", nameof(definition));
    }

    private static bool IsReached(string? current, string? initial, string? expected) =>
        string.IsNullOrWhiteSpace(expected)
            ? !Same(current, initial)
            : Same(current, expected);

    private static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}
