using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using VIBN_Tools.Tia.Contracts;

namespace VIBN_Tools.Tia.Client;

public sealed class NamedPipeTiaBridgeClient : ITiaBridgeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly TiaBridgeClientOptions _options;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private CancellationTokenSource _connectionCancellation = new();
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Process? _bridgeProcess;
    private bool _disposed;

    public NamedPipeTiaBridgeClient(TiaBridgeClientOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(options.PipeName))
            throw new ArgumentException("Ein Pipe-Name ist erforderlich.", nameof(options));
    }

    public bool IsConnected => _pipe?.IsConnected == true;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsConnected)
            return;

        try
        {
            await OpenPipeAsync(cancellationToken);
        }
        catch (TimeoutException) when (TryStartBridge())
        {
            await OpenPipeAsync(cancellationToken);
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return;

        var lockTaken = await _requestLock.WaitAsync(0, cancellationToken);
        try
        {
            if (lockTaken && IsConnected)
            {
                try
                {
                    using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    closeTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                    await SendCoreAsync(
                        TiaCommands.Close,
                        EmptyPayload.Instance,
                        closeTimeout.Token,
                        _connectionCancellation.Token);
                }
                catch
                {
                    // A blocked Openness call or an already closed bridge is
                    // handled by the transport/process cleanup below.
                }
            }
            else if (!lockTaken)
            {
                // Cancel the pending pipe read immediately. The bridge owns a
                // unique process, so terminating it cannot affect another page.
                _connectionCancellation.Cancel();
            }
        }
        finally
        {
            if (lockTaken)
                _requestLock.Release();
        }

        if (!lockTaken)
        {
            try
            {
                lockTaken = await _requestLock.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Cleanup below is still required when the caller cancels.
            }
            finally
            {
                if (lockTaken)
                    _requestLock.Release();
            }
        }

        CloseTransport();
        StopOwnedBridge();
        ResetConnectionCancellation();
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<EmptyPayload, string>(
            TiaCommands.Ping,
            EmptyPayload.Instance,
            cancellationToken);

        return string.Equals(response, "pong", StringComparison.OrdinalIgnoreCase);
    }

    public Task SelectVersionAsync(string version, CancellationToken cancellationToken = default) =>
        SendWithoutResultAsync(
            TiaCommands.SelectVersion,
            new TiaVersionPayload { Version = version },
            cancellationToken);

    public Task AttachAsync(CancellationToken cancellationToken = default) =>
        SendWithoutResultAsync(TiaCommands.Attach, EmptyPayload.Instance, cancellationToken);

    public async Task<IReadOnlyList<TiaPlcInfo>> ListPlcsAsync(
        CancellationToken cancellationToken = default) =>
        await SendAsync<EmptyPayload, List<TiaPlcInfo>>(
            TiaCommands.ListPlcs,
            EmptyPayload.Instance,
            cancellationToken);

    public Task SelectPlcAsync(int plcIndex, CancellationToken cancellationToken = default) =>
        SendWithoutResultAsync(
            TiaCommands.SelectPlc,
            new TiaPlcSelectionPayload { PlcIndex = plcIndex },
            cancellationToken);

    public async Task<IReadOnlyList<TiaHardwareModuleInfo>> ListHardwareAsync(
        CancellationToken cancellationToken = default) =>
        await SendAsync<EmptyPayload, List<TiaHardwareModuleInfo>>(
            TiaCommands.ListHardware,
            EmptyPayload.Instance,
            cancellationToken);

    public Task<TiaProjectTree> ListProgramBlocksAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync<EmptyPayload, TiaProjectTree>(
            TiaCommands.ListProgramBlocks,
            EmptyPayload.Instance,
            cancellationToken);

    public Task<TiaProjectTree> ListDataTypesAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync<EmptyPayload, TiaProjectTree>(
            TiaCommands.ListDataTypes,
            EmptyPayload.Instance,
            cancellationToken);

    public Task ImportBlockAsync(
        string folderPath,
        string filePath,
        CancellationToken cancellationToken = default) =>
        SendWithoutResultAsync(
            TiaCommands.ImportBlock,
            CreateTransfer(folderPath, string.Empty, filePath),
            cancellationToken);

    public Task ExportBlockAsync(
        string folderPath,
        string blockName,
        string filePath,
        CancellationToken cancellationToken = default) =>
        SendWithoutResultAsync(
            TiaCommands.ExportBlock,
            CreateTransfer(folderPath, blockName, filePath),
            cancellationToken);

    public Task ImportDataTypeAsync(
        string folderPath,
        string filePath,
        CancellationToken cancellationToken = default) =>
        SendWithoutResultAsync(
            TiaCommands.ImportDataType,
            CreateTransfer(folderPath, string.Empty, filePath),
            cancellationToken);

    public Task ExportDataTypeAsync(
        string folderPath,
        string dataTypeName,
        string filePath,
        CancellationToken cancellationToken = default) =>
        SendWithoutResultAsync(
            TiaCommands.ExportDataType,
            CreateTransfer(folderPath, dataTypeName, filePath),
            cancellationToken);

    public Task CreateBlockFolderAsync(
        string parentPath,
        string name,
        CancellationToken cancellationToken = default) =>
        SendWithoutResultAsync(
            TiaCommands.CreateBlockFolder,
            new TiaFolderPayload { ParentPath = parentPath, Name = name },
            cancellationToken);

    public Task CreateDataTypeFolderAsync(
        string parentPath,
        string name,
        CancellationToken cancellationToken = default) =>
        SendWithoutResultAsync(
            TiaCommands.CreateDataTypeFolder,
            new TiaFolderPayload { ParentPath = parentPath, Name = name },
            cancellationToken);

    public async Task<IReadOnlyList<TiaAxisInfo>> ListAxesAsync(
        CancellationToken cancellationToken = default) =>
        await SendAsync<EmptyPayload, List<TiaAxisInfo>>(
            TiaCommands.ListAxes,
            EmptyPayload.Instance,
            cancellationToken);

    public async Task<IReadOnlyList<TiaAxisInfo>> ConfigureAxesAsync(
        IReadOnlyCollection<string> axisIds,
        CancellationToken cancellationToken = default) =>
        await SendAsync<TiaAxisConfigurationPayload, List<TiaAxisInfo>>(
            TiaCommands.ConfigureAxes,
            new TiaAxisConfigurationPayload { AxisIds = axisIds?.ToList() ?? new List<string>() },
            cancellationToken);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        SendWithoutResultAsync(TiaCommands.Save, EmptyPayload.Instance, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        await DisconnectAsync();
        _disposed = true;
        _connectionCancellation.Dispose();
        _requestLock.Dispose();
    }

    private async Task OpenPipeAsync(CancellationToken cancellationToken)
    {
        _writer?.Dispose();
        _reader?.Dispose();
        _pipe?.Dispose();

        _pipe = new NamedPipeClientStream(
            _options.ServerName,
            _options.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.EffectiveConnectTimeout);

        try
        {
            await _pipe.ConnectAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"TIA Bridge Pipe '{_options.PipeName}' ist nicht erreichbar.");
        }

        _reader = new StreamReader(_pipe, leaveOpen: true);
        _writer = new StreamWriter(_pipe, leaveOpen: true) { AutoFlush = true };
    }

    private bool TryStartBridge()
    {
        if (string.IsNullOrWhiteSpace(_options.BridgeExecutablePath) ||
            !File.Exists(_options.BridgeExecutablePath))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _options.BridgeExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(_options.PipeName);
        _bridgeProcess = Process.Start(startInfo);
        return _bridgeProcess is not null;
    }

    private async Task SendWithoutResultAsync<TPayload>(
        string command,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        await SendAsync<TPayload, object?>(command, payload, cancellationToken);
    }

    private async Task<TResult> SendAsync<TPayload, TResult>(
        string command,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        if (!IsConnected || _reader is null || _writer is null)
            throw new InvalidOperationException("TIA Bridge ist nicht verbunden.");

        await _requestLock.WaitAsync(cancellationToken);
        try
        {
            return await SendCoreAsync<TPayload, TResult>(
                command,
                payload,
                cancellationToken,
                _connectionCancellation.Token);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task<TResult> SendCoreAsync<TPayload, TResult>(
        string command,
        TPayload payload,
        CancellationToken cancellationToken,
        CancellationToken connectionCancellation)
    {
        if (!IsConnected || _reader is null || _writer is null)
            throw new InvalidOperationException("TIA Bridge ist nicht verbunden.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            connectionCancellation);
        timeout.CancelAfter(_options.EffectiveRequestTimeout);

        var request = new TiaRequestEnvelope
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Command = command,
            PayloadJson = JsonSerializer.Serialize(payload, JsonOptions)
        };

        string? responseLine;
        try
        {
            await _writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions).AsMemory(), timeout.Token);
            responseLine = await _reader.ReadLineAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested &&
            !connectionCancellation.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"TIA Bridge-Befehl '{command}' hat nach {_options.EffectiveRequestTimeout.TotalSeconds:0.#} Sekunden nicht geantwortet. " +
                "TIA kann auf einen Openness-Freigabedialog oder den Abschluss des Projektladens warten.");
        }

        if (responseLine is null)
            throw new IOException("TIA Bridge hat die Verbindung beendet.");

        var response = JsonSerializer.Deserialize<TiaResponseEnvelope>(responseLine, JsonOptions)
            ?? throw new InvalidDataException("TIA Bridge lieferte keine gültige Antwort.");

        if (!string.Equals(request.RequestId, response.RequestId, StringComparison.Ordinal))
            throw new InvalidDataException("Antwort-ID der TIA Bridge stimmt nicht mit der Anfrage überein.");

        if (!response.Success)
        {
            throw new TiaBridgeException(
                response.Error?.Code ?? "bridge.error",
                response.Error?.Message ?? "TIA Bridge meldete einen unbekannten Fehler.",
                response.Error?.Details);
        }

        if (typeof(TResult) == typeof(object))
            return default!;

        return JsonSerializer.Deserialize<TResult>(response.PayloadJson, JsonOptions)
            ?? throw new InvalidDataException("Antwortdaten der TIA Bridge sind leer.");
    }

    private Task<object?> SendCoreAsync<TPayload>(
        string command,
        TPayload payload,
        CancellationToken cancellationToken,
        CancellationToken connectionCancellation) =>
        SendCoreAsync<TPayload, object?>(command, payload, cancellationToken, connectionCancellation);

    private void CloseTransport()
    {
        _writer?.Dispose();
        _reader?.Dispose();
        _pipe?.Dispose();
        _writer = null;
        _reader = null;
        _pipe = null;
    }

    private void StopOwnedBridge()
    {
        var process = _bridgeProcess;
        _bridgeProcess = null;
        if (process is null)
            return;

        try
        {
            if (!process.HasExited && !process.WaitForExit(1500))
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Process already exited between the state check and cleanup.
        }
        finally
        {
            process.Dispose();
        }
    }

    private void ResetConnectionCancellation()
    {
        _connectionCancellation.Dispose();
        _connectionCancellation = new CancellationTokenSource();
    }

    private static TiaTransferPayload CreateTransfer(
        string folderPath,
        string itemName,
        string filePath) =>
        new()
        {
            FolderPath = folderPath ?? string.Empty,
            ItemName = itemName ?? string.Empty,
            FilePath = filePath ?? string.Empty
        };
}
