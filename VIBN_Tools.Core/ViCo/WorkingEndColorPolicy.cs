using System.Globalization;

namespace VIBN_Tools.Core.ViCo;

/// <summary>Shared deadline colours for the workstation overview and IBN-Remote.</summary>
public static class WorkingEndColorPolicy
{
    public static string GetBackground(string? summary, DateTime? currentDate = null)
    {
        var today = (currentDate ?? DateTime.Today).Date;
        var dates = (summary ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => DateTime.TryParseExact(value.Trim(), "dd.MM.yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date) ? (DateTime?)date.Date : null)
            .Where(date => date.HasValue).Select(date => date!.Value).ToArray();
        if (dates.Any(date => date < today)) return "#FFFFC7CE";
        if (dates.Any(date => date <= today.AddDays(7))) return "#FFFFEB9C";
        return "#00FFFFFF";
    }
}
