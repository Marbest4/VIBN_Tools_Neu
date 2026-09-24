using VIBN_Tools.Core.ViCo;
using VIBN_Tools.IbnRemote.Infrastructure;

internal static class IbnRemoteSelectionSmokeTests
{
    public static void Verify()
    {
        var matching = Workstation("PC-01",
            new ViCoProjectCardInfo(1, "GM1000 Motor", "In Arbeit", null, DateTimeOffset.Now));
        var planningOnly = Workstation("PC-02",
            new ViCoProjectCardInfo(2, "GM1000 Motor", "Planung", null, DateTimeOffset.Now));
        var differentWorking = Workstation("PC-03",
            new ViCoProjectCardInfo(3, "GM2000 Sensor", "In Arbeit", null, DateTimeOffset.Now));

        var selected = IbnRemoteWorkstationSelectionPolicy.Select(
            [planningOnly, differentWorking, matching],
            "motor");

        Assert(selected.Count == 1 && selected[0].PcName == "PC-01",
            "IBN Remote filter must match only the In Arbeit project column.");
    }

    private static ViCoWorkstation Workstation(string pc, ViCoProjectCardInfo card) => new(
        pc,
        pc,
        "user",
        string.Empty,
        string.Empty,
        string.Empty,
        [card.Title],
        [$"{card.Title} ({card.Status})"],
        ProjectCards: [card]);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
