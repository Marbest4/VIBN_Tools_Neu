namespace VIBN_Tools.Infrastructure.ViCo;

public static class RemoteDesktopProfileBuilder
{
    public static IReadOnlyList<string> Build(
        string hostName,
        string userName,
        IReadOnlyCollection<int> monitorIndexes,
        IReadOnlyCollection<int> availableMonitorIds,
        bool promptForCredentials = false)
    {
        if (string.IsNullOrWhiteSpace(hostName))
            throw new ArgumentException("A workstation is required.", nameof(hostName));
        if (!promptForCredentials && string.IsNullOrWhiteSpace(userName))
            throw new InvalidOperationException("Die Kanbanize-Karte enthält keinen gültigen Remote-Benutzer.");

        ArgumentNullException.ThrowIfNull(availableMonitorIds);
        var availableMonitors = availableMonitorIds.Count == 0
            ? new[] { 0 }
            : availableMonitorIds.Distinct().OrderBy(index => index).ToArray();
        var monitors = monitorIndexes
            .Where(availableMonitors.Contains)
            .Distinct()
            .ToArray();
        if (monitors.Length == 0)
            monitors = [availableMonitors[0]];

        var lines = new List<string>
        {
            "screen mode id:i:2",
            "session bpp:i:32",
            "compression:i:1",
            "keyboardhook:i:2",
            "networkautodetect:i:1",
            "bandwidthautodetect:i:1",
            "displayconnectionbar:i:1",
            "redirectclipboard:i:1",
            "autoreconnection enabled:i:1",
            $"full address:s:{hostName}",
            $"prompt for credentials:i:{(promptForCredentials ? 1 : 0)}",
            "administrative session:i:0",
            "enablecredsspsupport:i:1",
            "redirectprinters:i:0",
            "redirectcomports:i:0",
            "redirectsmartcards:i:0",
            "drivestoredirect:s:"
        };

        if (!string.IsNullOrWhiteSpace(userName))
            lines.Insert(lines.IndexOf($"prompt for credentials:i:{(promptForCredentials ? 1 : 0)}"), $"username:s:{userName}");

        if (monitors.Length == availableMonitors.Length &&
            monitors.ToHashSet().SetEquals(availableMonitors))
            lines.Add("use multimon:i:1");
        else if (monitors.Length > 1)
        {
            lines.Add($"selectedmonitors:s:{string.Join(',', monitors)}");
            lines.Add("use multimon:i:1");
        }
        else
        {
            lines.Add("use multimon:i:0");
        }

        return lines;
    }
}
