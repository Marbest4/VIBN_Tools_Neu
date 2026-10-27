using VIBN_Tools.MiniTools;

namespace VIBN_Tools.MiniTools.Tests;

internal static class Program
{
    private static async Task<int> Main()
    {
        try
        {
            await PositionsEverySelectionAndScalesOnlySurfaces();
            await EmptySelectionDoesNotWrite();
            await RejectsInvalidScaleBeforeFeeAccess();
            ParsesBandHeightAndGermanDecimalInput();
            Console.WriteLine("Mini-Tools tests passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task PositionsEverySelectionAndScalesOnlySurfaces()
    {
        var surface = new FeeSelectedObject(Guid.NewGuid(), "Bandoberfläche", true);
        var frame = new FeeSelectedObject(Guid.NewGuid(), "Tragrahmen", false);
        var gateway = new RecordingGateway([surface, frame]);
        var service = new FeeSelectionPositioningService(gateway);
        var progress = new List<FeeSelectionPositioningProgress>();

        var result = await service.PositionSelectionAsync(
            new FeeSelectionPositioningRequest(
                new MiniToolsVector(10, 20, 30),
                new MiniToolsVector(4, 2, 0.25f)),
            new InlineProgress<FeeSelectionPositioningProgress>(progress.Add));

        Assert(result.SelectedCount == 2, "Selection count was not retained.");
        Assert(result.SuccessfulPositionCount == 2, "Not every selected object was positioned.");
        Assert(result.SurfaceCount == 1, "Exactly one selected Surface must be scaled.");
        Assert(gateway.Positions.Keys.Order().SequenceEqual(new[] { surface.Id, frame.Id }.Order()),
            "Position was not written exactly once for every selected object.");
        Assert(gateway.Scales.Count == 1 && gateway.Scales.ContainsKey(surface.Id),
            "Scale must only be written for the selected Surface.");
        Assert(gateway.Positions[surface.Id] == new MiniToolsVector(10, 20, 30),
            "The chosen band height was not used as Z position.");
        Assert(progress.Last().Completed == 2 && progress.Last().Total == 2,
            "Progress did not reach the complete selection.");
    }

    private static async Task EmptySelectionDoesNotWrite()
    {
        var gateway = new RecordingGateway([]);
        var service = new FeeSelectionPositioningService(gateway);

        var result = await service.PositionSelectionAsync(
            new FeeSelectionPositioningRequest(
                new MiniToolsVector(0, 0, 0),
                new MiniToolsVector(1, 1, 1)));

        Assert(result.SelectedCount == 0, "An empty FEE selection must remain empty.");
        Assert(gateway.Positions.Count == 0 && gateway.Scales.Count == 0,
            "An empty FEE selection must not produce writes.");
    }

    private static async Task RejectsInvalidScaleBeforeFeeAccess()
    {
        var gateway = new RecordingGateway([]);
        var service = new FeeSelectionPositioningService(gateway);
        try
        {
            await service.PositionSelectionAsync(
                new FeeSelectionPositioningRequest(
                    new MiniToolsVector(0, 0, 0),
                    new MiniToolsVector(1, 0, 1)));
        }
        catch (ArgumentException)
        {
            Assert(gateway.SelectionReadCount == 0,
                "Invalid input must be rejected before accessing FEE.");
            return;
        }

        throw new InvalidOperationException("A zero Surface scale was accepted.");
    }

    private static void ParsesBandHeightAndGermanDecimalInput()
    {
        var input = new FeeSelectionPositioningInput(
            "1,5",
            "-2.25",
            "0,8",
            "1,2",
            "1,6",
            "4",
            "2,5",
            "0.1",
            MiniToolsBandHeight.BandHeight2);

        Assert(input.TryCreateRequest(out var request, out var message),
            $"Valid positioning input was rejected: {message}");
        Assert(request.Position == new MiniToolsVector(1.5f, -2.25f, 1.2f),
            "The selected second band height or decimal parser is incorrect.");
        Assert(request.SurfaceScale == new MiniToolsVector(4, 2.5f, 0.1f),
            "Surface scale input was not mapped to X/Y/Z.");

        var invalid = input with { Length = "0" };
        Assert(!invalid.TryCreateRequest(out _, out var invalidMessage) &&
               invalidMessage.Contains("größer als 0", StringComparison.Ordinal),
            "Non-positive Surface scale must produce a concrete validation message.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class RecordingGateway(IReadOnlyList<FeeSelectedObject> selected)
        : IFeeSelectionTransformGateway
    {
        public int SelectionReadCount { get; private set; }
        public Dictionary<Guid, MiniToolsVector> Positions { get; } = [];
        public Dictionary<Guid, MiniToolsVector> Scales { get; } = [];

        public Task<IReadOnlyList<FeeSelectedObject>> GetSelectedObjectsAsync(
            CancellationToken cancellationToken = default)
        {
            SelectionReadCount++;
            return Task.FromResult(selected);
        }

        public Task<bool> SetPositionAsync(
            Guid objectId,
            MiniToolsVector position,
            CancellationToken cancellationToken = default)
        {
            Positions.Add(objectId, position);
            return Task.FromResult(true);
        }

        public Task<bool> SetSurfaceScaleAsync(
            Guid objectId,
            MiniToolsVector scale,
            CancellationToken cancellationToken = default)
        {
            Scales.Add(objectId, scale);
            return Task.FromResult(true);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
