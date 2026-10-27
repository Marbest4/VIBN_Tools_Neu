using System.Globalization;

namespace VIBN_Tools.MiniTools;

public enum MiniToolsBandHeight
{
    BandHeight1 = 1,
    BandHeight2 = 2,
    BandHeight3 = 3,
}
public readonly record struct MiniToolsVector(float X, float Y, float Z);

public sealed record FeeSelectionPositioningRequest(
    MiniToolsVector Position,
    MiniToolsVector SurfaceScale);

public sealed record FeeSelectionPositioningInput(
    string? X,
    string? Y,
    string? BandHeight1,
    string? BandHeight2,
    string? BandHeight3,
    string? Length,
    string? Width,
    string? Height,
    MiniToolsBandHeight SelectedBandHeight)
{
    public bool TryCreateRequest(
        out FeeSelectionPositioningRequest request,
        out string validationMessage)
    {
        request = default!;
        var fields = new[]
        {
            ("X", X),
            ("Y", Y),
            ("Z (Bandhöhe 1)", BandHeight1),
            ("Z (Bandhöhe 2)", BandHeight2),
            ("Z (Bandhöhe 3)", BandHeight3),
            ("Länge", Length),
            ("Breite", Width),
            ("Höhe", Height),
        };
        var parsed = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var (name, value) in fields)
        {
            if (!TryParseFiniteFloat(value, out var number))
            {
                validationMessage = $"'{name}' muss eine gültige endliche Zahl sein. Punkt und Dezimalkomma werden unterstützt.";
                return false;
            }
            parsed[name] = number;
        }

        if (parsed["Länge"] <= 0 || parsed["Breite"] <= 0 || parsed["Höhe"] <= 0)
        {
            validationMessage = "Länge, Breite und Höhe müssen größer als 0 sein.";
            return false;
        }

        var selectedZ = SelectedBandHeight switch
        {
            MiniToolsBandHeight.BandHeight2 => parsed["Z (Bandhöhe 2)"],
            MiniToolsBandHeight.BandHeight3 => parsed["Z (Bandhöhe 3)"],
            _ => parsed["Z (Bandhöhe 1)"],
        };
        request = new FeeSelectionPositioningRequest(
            new MiniToolsVector(parsed["X"], parsed["Y"], selectedZ),
            new MiniToolsVector(parsed["Länge"], parsed["Breite"], parsed["Höhe"]));
        validationMessage = string.Empty;
        return true;
    }

    private static bool TryParseFiniteFloat(string? value, out float number)
    {
        var text = value?.Trim();
        var parsed = float.TryParse(
                         text,
                         NumberStyles.Float,
                         CultureInfo.CurrentCulture,
                         out number) ||
                     float.TryParse(
                         text,
                         NumberStyles.Float,
                         CultureInfo.InvariantCulture,
                         out number) ||
                     float.TryParse(
                         text?.Replace(',', '.'),
                         NumberStyles.Float,
                         CultureInfo.InvariantCulture,
                         out number);
        return parsed && !float.IsNaN(number) && !float.IsInfinity(number);
    }
}
public sealed record FeeSelectedObject(
    Guid Id,
    string Name,
    bool IsSurface);

public sealed record FeeSelectionPositioningItemResult(
    string ObjectName,
    bool IsSurface,
    bool PositionChanged,
    bool ScaleChanged,
    string Message);

public sealed record FeeSelectionPositioningResult(
    IReadOnlyList<FeeSelectionPositioningItemResult> Items)
{
    public int SelectedCount => Items.Count;
    public int SuccessfulPositionCount => Items.Count(item => item.PositionChanged);
    public int SurfaceCount => Items.Count(item => item.ScaleChanged);
    public int FailedCount => Items.Count(item =>
        !item.PositionChanged || (item.IsSurface && !item.ScaleChanged));
}

public readonly record struct FeeSelectionPositioningProgress(
    int Completed,
    int Total,
    string Message);

/// <summary>
/// Small, testable boundary around the stateful FEE object API. Calls remain
/// sequential because the vendor client is not guaranteed to be thread-safe.
/// </summary>
public interface IFeeSelectionTransformGateway
{
    Task<IReadOnlyList<FeeSelectedObject>> GetSelectedObjectsAsync(
        CancellationToken cancellationToken = default);

    Task<bool> SetPositionAsync(
        Guid objectId,
        MiniToolsVector position,
        CancellationToken cancellationToken = default);

    Task<bool> SetSurfaceScaleAsync(
        Guid objectId,
        MiniToolsVector scale,
        CancellationToken cancellationToken = default);
}

public sealed class FeeSelectionPositioningService(IFeeSelectionTransformGateway gateway)
{
    public async Task<FeeSelectionPositioningResult> PositionSelectionAsync(
        FeeSelectionPositioningRequest request,
        IProgress<FeeSelectionPositioningProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(request);

        var selected = await gateway.GetSelectedObjectsAsync(cancellationToken);
        if (selected.Count == 0)
            return new FeeSelectionPositioningResult([]);

        var results = new List<FeeSelectionPositioningItemResult>(selected.Count);
        for (var index = 0; index < selected.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = selected[index];
            progress?.Report(new FeeSelectionPositioningProgress(
                index,
                selected.Count,
                $"{item.Name} wird positioniert …"));

            try
            {
                var positionChanged = await gateway.SetPositionAsync(
                    item.Id,
                    request.Position,
                    cancellationToken);
                var scaleChanged = false;
                if (positionChanged && item.IsSurface)
                {
                    scaleChanged = await gateway.SetSurfaceScaleAsync(
                        item.Id,
                        request.SurfaceScale,
                        cancellationToken);
                }

                var message = !positionChanged
                    ? "Position wurde von FEE nicht bestätigt."
                    : item.IsSurface && !scaleChanged
                        ? "Position geändert; Surface-Skalierung wurde von FEE nicht bestätigt."
                        : item.IsSurface
                            ? "Position und Surface-Skalierung geändert."
                            : "Position geändert; Skalierung entfällt (kein Surface).";
                results.Add(new FeeSelectionPositioningItemResult(
                    item.Name,
                    item.IsSurface,
                    positionChanged,
                    scaleChanged,
                    message));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                results.Add(new FeeSelectionPositioningItemResult(
                    item.Name,
                    item.IsSurface,
                    false,
                    false,
                    $"FEE-Änderung fehlgeschlagen: {exception.Message}"));
            }

            progress?.Report(new FeeSelectionPositioningProgress(
                index + 1,
                selected.Count,
                $"{index + 1} von {selected.Count} Objekten verarbeitet."));
        }

        return new FeeSelectionPositioningResult(results);
    }

    private static void Validate(FeeSelectionPositioningRequest request)
    {
        ValidateVector(request.Position, "Position", positiveOnly: false);
        ValidateVector(request.SurfaceScale, "Surface-Skalierung", positiveOnly: true);
    }

    private static void ValidateVector(MiniToolsVector vector, string label, bool positiveOnly)
    {
        var values = new[] { vector.X, vector.Y, vector.Z };
        if (values.Any(value => float.IsNaN(value) || float.IsInfinity(value)))
            throw new ArgumentException($"{label} enthält keinen endlichen Zahlenwert.");
        if (positiveOnly && values.Any(value => value <= 0))
            throw new ArgumentException($"{label} muss in allen drei Richtungen größer als 0 sein.");
    }
}
