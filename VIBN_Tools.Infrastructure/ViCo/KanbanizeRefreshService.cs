using System.Net.Http.Headers;
using System.Net;
using System.Text.Json;
using VIBN_Tools.Core.Kanbanize;
using VIBN_Tools.Core.ViCo;

namespace VIBN_Tools.Infrastructure.ViCo;

public sealed class KanbanizeRefreshService : IViCoOnlineRefreshService
{
    private const string ApiBase = "https://grobgroup.kanbanize.com/api/v2";
    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private readonly HttpClient _httpClient;
    private readonly Func<string?> _apiKeyProvider;
    private readonly string _cacheRoot;

    public KanbanizeRefreshService(HttpClient httpClient, string? apiKey, string cacheRoot)
        : this(httpClient, () => apiKey, cacheRoot)
    {
    }

    public KanbanizeRefreshService(
        HttpClient httpClient,
        Func<string?> apiKeyProvider,
        string cacheRoot)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
        _cacheRoot = cacheRoot;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Kanbanize API access is not configured.");

        var lanesTask = GetJsonAsync("/boards/1541/lanes", cancellationToken);
        var cardsTask = LoadCardsAsync(1541, loadConfigurationSubtasks: true, cancellationToken);
        var robotCardsTask = LoadCardsAsync(846, loadConfigurationSubtasks: false, cancellationToken);
        var robotColumnsTask = GetJsonAsync("/boards/846/columns?fields=column_id,name", cancellationToken);
        await Task.WhenAll(lanesTask, cardsTask, robotCardsTask, robotColumnsTask);
        using var lanes = await lanesTask;
        var cards = await cardsTask;
        var robotCards = await robotCardsTask;
        using var robotColumns = await robotColumnsTask;

        var laneLines = new List<string>();
        var structuredLanes = new List<WorkstationLaneCacheEntry>();
        foreach (var lane in EnumerateObjects(lanes.RootElement))
        {
            if (!TryGetScalar(lane, "lane_id", out var id) || !TryGetScalar(lane, "name", out var name))
                continue;
            laneLines.Add(id);
            laneLines.Add(name);
            structuredLanes.Add(new WorkstationLaneCacheEntry { Id = id, Name = name });
        }

        var cardLines = new List<string>();
        foreach (var card in cards)
        {
            if (card.LaneId.Length == 0 || card.Title.Length == 0)
                continue;
            cardLines.Add(MapStatus(card.ColumnId) + card.Title);
            cardLines.Add(card.LaneId);
        }

        ValidateWorkstationCache(structuredLanes, cards);

        Directory.CreateDirectory(_cacheRoot);
        await WriteAtomicallyAsync(
            Path.Combine(_cacheRoot, "AllPCLaneInfosWithChilds.txt"),
            laneLines,
            cancellationToken);
        await WriteAtomicallyAsync(
            Path.Combine(_cacheRoot, "AllCardsOfPCsV2.txt"),
            cardLines,
            cancellationToken);
        await WriteJsonAtomicallyAsync(
            Path.Combine(_cacheRoot, "WorkstationBoardCache.json"),
            new WorkstationBoardCache
            {
                Lanes = structuredLanes
                    .GroupBy(lane => lane.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList(),
                Cards = cards
            },
            cancellationToken);

        var robotCardLines = new List<string>();
        var robotNameLines = new List<string>();
        var knownRobotCards = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in robotCards)
        {
            if (!card.Title.Contains("Software Robotik", StringComparison.OrdinalIgnoreCase) ||
                card.ColumnId.Length == 0)
            {
                continue;
            }
            var title = card.Title;
            var columnId = card.ColumnId;
            var identity = card.Id > 0 ? card.Id.ToString() : $"{title}|{columnId}";
            if (!knownRobotCards.Add(identity))
                continue;
            robotCardLines.Add(title);
            robotCardLines.Add(columnId);
            robotNameLines.Add(ExtractRobotName(title));
            robotNameLines.Add(columnId);
        }

        var robotColumnLines = new List<string>();
        foreach (var column in EnumerateObjects(robotColumns.RootElement))
        {
            if (!TryGetScalar(column, "column_id", out var columnId) ||
                !TryGetScalar(column, "name", out var name))
            {
                continue;
            }
            robotColumnLines.Add(columnId);
            robotColumnLines.Add(name);
        }

        await WriteAtomicallyAsync(Path.Combine(_cacheRoot, "AllRobyCards.txt"), robotCardLines, cancellationToken);
        await WriteAtomicallyAsync(Path.Combine(_cacheRoot, "AllRobyCardsRobyName.txt"), robotNameLines, cancellationToken);
        await WriteAtomicallyAsync(Path.Combine(_cacheRoot, "AllRobyColumns.txt"), robotColumnLines, cancellationToken);
    }

    private async Task<List<WorkstationCardCacheEntry>> LoadCardsAsync(
        int boardId,
        bool loadConfigurationSubtasks,
        CancellationToken cancellationToken)
    {
        const int pageSize = 1000;
        using var firstPage = await GetJsonAsync(
            BuildCardsUrl(boardId, 1, pageSize, loadConfigurationSubtasks),
            cancellationToken);
        var cards = GetCardEntries(firstPage.RootElement);
        var pageCount = Math.Max(1, ReadPageCount(firstPage.RootElement));
        for (var page = 2; page <= pageCount; page++)
        {
            using var nextPage = await GetJsonAsync(
                BuildCardsUrl(boardId, page, pageSize, loadConfigurationSubtasks),
                cancellationToken);
            cards.AddRange(GetCardEntries(nextPage.RootElement));
        }

        cards = cards
            .GroupBy(card => card.Id)
            .Select(group => group.First())
            .ToList();
        await LoadMissingCardPositionsAsync(cards, cancellationToken);
        cards = cards
            .Where(card => card.Id > 0 && card.LaneId.Length > 0 && card.Title.Length > 0)
            .ToList();
        await LoadMissingProjectDeadlinesAsync(cards, cancellationToken);
        if (loadConfigurationSubtasks)
            await LoadConfigurationSubtasksAsync(cards, cancellationToken);
        return cards;
    }

    private static string BuildCardsUrl(int boardId, int page, int pageSize, bool expandSubtasks) =>
        $"/cards?board_ids={boardId}&page={page}&per_page={pageSize}" +
        (expandSubtasks ? "&expand=custom_fields" : string.Empty);

    /// <summary>
    /// The Businessmap tenant does not accept lane/column as explicit
    /// <c>fields</c> values and may omit positional data from a reduced list
    /// response. The unrestricted list is the normal path; this card-detail
    /// fallback protects the workstation join if a server version still omits
    /// one of the position values.
    /// </summary>
    private async Task LoadMissingCardPositionsAsync(
        IEnumerable<WorkstationCardCacheEntry> cards,
        CancellationToken cancellationToken)
    {
        var candidates = cards
            .Where(card => card.Id > 0 &&
                           (card.LaneId.Length == 0 || card.ColumnId.Length == 0 || card.Title.Length == 0))
            .ToArray();
        if (candidates.Length == 0)
            return;

        using var throttle = new SemaphoreSlim(6);
        await Task.WhenAll(candidates.Select(async card =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                using var detail = await GetJsonAsync($"/cards/{card.Id}", cancellationToken);
                var cardObject = EnumerateObjects(detail.RootElement)
                    .FirstOrDefault(element => TryGetInt(element, "card_id", "id") == card.Id);
                if (cardObject.ValueKind != JsonValueKind.Object)
                    return;

                if (card.LaneId.Length == 0)
                    card.LaneId = ReadCardPosition(cardObject, "lane_id", "laneId");
                if (card.ColumnId.Length == 0)
                    card.ColumnId = ReadCardPosition(cardObject, "column_id", "columnId");
                if (card.Title.Length == 0 && TryGetScalar(cardObject, "title", out var title))
                    card.Title = title;
            }
            finally
            {
                throttle.Release();
            }
        }));
    }

    /// <summary>
    /// Businessmap installations can omit <c>deadline</c> from the board list.
    /// Resolve only active project cards with a
    /// missing value through the authoritative card endpoint; populated list
    /// values do not cause additional requests.
    /// </summary>
    private async Task LoadMissingProjectDeadlinesAsync(
        IEnumerable<WorkstationCardCacheEntry> cards,
        CancellationToken cancellationToken)
    {
        var candidates = cards
            .Where(card => card.Deadline is null && !IsConfigurationTitle(card.Title))
            .Where(card => MapStatus(card.ColumnId) is "#Planning#" or "#Working#")
            .ToArray();
        if (candidates.Length == 0)
            return;

        using var throttle = new SemaphoreSlim(6);
        await Task.WhenAll(candidates.Select(async card =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                using var detail = await GetJsonAsync(
                    $"/cards/{card.Id}?fields=card_id,deadline",
                    cancellationToken);
                card.Deadline = EnumerateObjects(detail.RootElement)
                    .Select(element => TryGetDate(element, "deadline") ??
                                       TryGetDate(element, "due_date") ??
                                       TryGetDate(element, "end_date"))
                    .FirstOrDefault(value => value is not null);
            }
            finally
            {
                throttle.Release();
            }
        }));
    }

    /// <summary>
    /// Positional fields are returned by Businessmap independently. Deadline
    /// must be requested explicitly; otherwise the board response can omit it
    /// although the Kanbanize-card tab receives it from its own detail query.
    /// Subtasks are loaded only for the small set of KONFIGURATION cards.
    /// </summary>
    private async Task LoadConfigurationSubtasksAsync(
        IEnumerable<WorkstationCardCacheEntry> cards,
        CancellationToken cancellationToken)
    {
        using var throttle = new SemaphoreSlim(6);
        var requests = cards
            // The per-card endpoint is authoritative. Businessmap may return
            // only IDs or a partial expansion in the board response, notably
            // for subtasks created directly in the web UI. Always merge the
            // detail response for the small number of configuration cards.
            .Where(card => IsConfigurationTitle(card.Title))
            .Select(async card =>
            {
                await throttle.WaitAsync(cancellationToken);
                try
                {
                    using var document = await GetJsonAsync($"/cards/{card.Id}/subtasks", cancellationToken);
                    card.Subtasks = card.Subtasks
                        .Concat(GetSubtasks(document.RootElement, isEndpointPayload: true))
                        .Where(subtask => subtask.Id > 0 && subtask.Description.Length > 0)
                        .GroupBy(subtask => subtask.Id)
                        .Select(group => group.Last())
                        .ToList();
                }
                finally
                {
                    throttle.Release();
                }
            });
        await Task.WhenAll(requests);
    }

    private async Task<JsonDocument> GetJsonAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        const int maximumAttempts = 3;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + relativeUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("apikey", ResolveApiKey());

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
            }
            catch (HttpRequestException) when (attempt < maximumAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
                continue;
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                }

                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                if (attempt < maximumAttempts && IsTransient(response.StatusCode))
                {
                    var delay = response.Headers.RetryAfter?.Delta ??
                        TimeSpan.FromMilliseconds(250 * attempt);
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                throw CreateApiException(response.StatusCode, response.ReasonPhrase, error);
            }
        }

        throw new HttpRequestException("Kanbanize konnte nach mehreren Versuchen nicht erreicht werden.");
    }

    private string ResolveApiKey() => _apiKeyProvider()?.Trim() ?? string.Empty;

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or
        HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static HttpRequestException CreateApiException(
        HttpStatusCode statusCode,
        string? reasonPhrase,
        string responseBody)
    {
        var detail = responseBody.Trim();
        if (detail.Length > 600)
            detail = detail[..600];
        var prefix = statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? "Kanbanize-Anmeldung fehlgeschlagen. API-Key und Board-Berechtigung prüfen."
            : $"Kanbanize API meldet {(int)statusCode} ({reasonPhrase}).";
        return new HttpRequestException(detail.Length == 0 ? prefix : $"{prefix} {detail}");
    }

    private static IEnumerable<JsonElement> EnumerateObjects(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            yield return root;
            foreach (var property in root.EnumerateObject())
            {
                foreach (var child in EnumerateObjects(property.Value))
                    yield return child;
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                foreach (var child in EnumerateObjects(item))
                    yield return child;
            }
        }
    }

    /// <summary>
    /// Reads only actual board cards (rather than recursively walking every
    /// JSON object) so configuration subtasks retain their parent card ID and
    /// can later be edited without touching any other card field.
    /// </summary>
    private static List<WorkstationCardCacheEntry> GetCardEntries(JsonElement root)
    {
        var data = root;
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("data", out var nested))
            data = nested;
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("data", out nested))
            data = nested;
        if (data.ValueKind != JsonValueKind.Array)
            return new List<WorkstationCardCacheEntry>();

        return data.EnumerateArray()
            .Select(card => new WorkstationCardCacheEntry
            {
                Id = TryGetInt(card, "card_id", "id"),
                LaneId = ReadCardPosition(card, "lane_id", "laneId"),
                ColumnId = ReadCardPosition(card, "column_id", "columnId"),
                Title = TryGetScalar(card, "title", out var title) ? title : string.Empty,
                // On the workstation board the project start is maintained in
                // the established custom field 508. Keep the legacy property as
                // a fallback for old cache/test payloads.
                StartDate = TryGetCustomDate(
                                card,
                                VibnWorkplaceSynchronizationPolicy.WorkplaceStartDateFieldId)
                            ?? TryGetDate(card, "start_date"),
                Deadline = TryGetDate(card, "deadline") ??
                           TryGetDate(card, "due_date") ??
                           TryGetDate(card, "end_date"),
                Subtasks = GetSubtasks(card, isEndpointPayload: false)
            })
            .Where(card => card.Id > 0)
            .GroupBy(card => card.Id)
            .Select(group => group.First())
            .ToList();
    }

    private static string ReadCardPosition(JsonElement card, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetScalar(card, name, out var direct))
                return direct;
        }

        foreach (var wrapperName in new[] { "current_position", "currentPosition", "position", "location" })
        {
            if (card.ValueKind != JsonValueKind.Object ||
                !card.TryGetProperty(wrapperName, out var wrapper) ||
                wrapper.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var name in names)
            {
                if (TryGetScalar(wrapper, name, out var nested))
                    return nested;
            }
        }

        return string.Empty;
    }

    private static List<WorkstationSubtaskCacheEntry> GetSubtasks(
        JsonElement root,
        bool isEndpointPayload) =>
        BusinessmapSubtaskJsonParser.Parse(root, isEndpointPayload)
            .Select(subtask => new WorkstationSubtaskCacheEntry
            {
                Id = subtask.Id,
                Description = subtask.Description
            })
            .ToList();

    private static bool IsConfigurationTitle(string title) =>
        title.Contains("KONFIGURATION", StringComparison.OrdinalIgnoreCase);

    private static int ReadPageCount(JsonElement root)
    {
        var data = root;
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("data", out var nested))
            data = nested;
        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("pagination", out var pagination))
        {
            return TryGetInt(pagination, "all_pages");
        }
        return 1;
    }

    private static bool TryGetScalar(JsonElement value, string name, out string result)
    {
        result = string.Empty;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property))
            return false;
        if (property.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null)
            return false;
        result = property.ToString();
        return true;
    }

    private static int TryGetInt(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetScalar(value, name, out var raw) || !int.TryParse(raw, out var number))
                continue;
            return number;
        }
        return 0;
    }

    private static DateTimeOffset? TryGetDate(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty(name, out var property))
            return null;
        if (property.ValueKind == JsonValueKind.Object)
        {
            foreach (var nestedName in new[] { "value", "date", "datetime" })
            {
                var nested = TryGetDate(property, nestedName);
                if (nested is not null)
                    return nested;
            }
            return null;
        }
        var raw = property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.ToString();
        if (string.IsNullOrWhiteSpace(raw) ||
            !DateTimeOffset.TryParse(
                raw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return null;
        }

        return parsed;
    }

    private static DateTimeOffset? TryGetCustomDate(JsonElement card, int fieldId)
    {
        if (card.ValueKind != JsonValueKind.Object ||
            !card.TryGetProperty("custom_fields", out var fields) ||
            fields.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var field in fields.EnumerateArray())
        {
            if (TryGetInt(field, "field_id", "id") != fieldId)
                continue;
            if (!TryGetScalar(field, "value", out var raw) ||
                !DateTimeOffset.TryParse(
                    raw,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                return null;
            }

            return parsed;
        }

        return null;
    }

    private static string MapStatus(string columnId) => columnId switch
    {
        "29373" or "29368" => "#Backlog#",
        "29374" or "29369" => "#Planning#",
        "29375" or "29370" => "#Working#",
        "29376" or "29371" => "#Done#",
        _ => string.Empty
    };

    private static string ExtractRobotName(string title)
    {
        var values = System.Text.RegularExpressions.Regex.Matches(title, @"\[([^\]]+)\]")
            .Select(match => match.Groups[1].Value.Trim())
            .Where(value => value.Length > 0)
            .ToArray();
        if (values.Length > 1)
            return values[^1];
        return title.Replace("Software Robotik", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim(' ', '-', ':', '|');
    }

    private static void ValidateWorkstationCache(
        IReadOnlyCollection<WorkstationLaneCacheEntry> lanes,
        IReadOnlyCollection<WorkstationCardCacheEntry> cards)
    {
        var workstationLaneIds = lanes
            .Where(lane => lane.Id.Length > 0 &&
                           (lane.Name.Contains("GM", StringComparison.OrdinalIgnoreCase) ||
                            lane.Name.Contains("GU", StringComparison.OrdinalIgnoreCase) ||
                            lane.Name.Contains("Tool", StringComparison.OrdinalIgnoreCase)))
            .Select(lane => lane.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var joinedCardCount = cards.Count(card =>
            workstationLaneIds.Contains(card.LaneId) && card.Title.Length > 0);
        if (workstationLaneIds.Count > 0 && joinedCardCount > 0)
            return;

        throw new InvalidDataException(
            "Die Kanbanize-Antwort enthält keinen verwendbaren Arbeitsplatzstand " +
            $"({lanes.Count} Lanes, {workstationLaneIds.Count} Arbeitsplatz-Lanes, " +
            $"{cards.Count} Karten, {joinedCardCount} zuordenbare Karten). " +
            "Der vorhandene ViCo-Cache wurde deshalb nicht überschrieben.");
    }

    private static async Task WriteAtomicallyAsync(
        string destination,
        IEnumerable<string> lines,
        CancellationToken cancellationToken)
    {
        var temporary = CreateTemporaryPath(destination);
        try
        {
            await File.WriteAllLinesAsync(temporary, lines, cancellationToken);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static async Task WriteJsonAtomicallyAsync(
        string destination,
        WorkstationBoardCache value,
        CancellationToken cancellationToken)
    {
        var temporary = CreateTemporaryPath(destination);
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, value, CacheJsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static string CreateTemporaryPath(string destination) =>
        $"{destination}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
}
