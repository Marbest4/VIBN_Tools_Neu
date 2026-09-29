namespace VIBN_Tools.Application.Behaviors;

/// <summary>
/// Recognizes the narrow WPF virtualization race that can occur when a
/// DataGrid's items are replaced while its container generator still owns a
/// deferred index request. Other argument errors remain fatal and visible.
/// </summary>
public static class WpfVirtualizationExceptionPolicy
{
    private static readonly string[] WpfMarkers =
    [
        "System.Windows.Controls.DataGrid",
        "System.Windows.Controls.ItemContainerGenerator",
        "System.Windows.Controls.ItemCollection",
        "System.Windows.Controls.VirtualizingStackPanel",
        "System.Windows.Controls.Primitives.DataGrid",
        "System.Windows.Data.ListCollectionView",
        "MS.Internal.Data.IndexedEnumerable",
    ];

    public static bool IsRecoverable(Exception exception, string? diagnosticText = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var candidate = exception;
        while (candidate is not null)
        {
            if (candidate is ArgumentOutOfRangeException argumentError &&
                string.Equals(argumentError.ParamName, "index", StringComparison.OrdinalIgnoreCase))
            {
                var diagnostic = diagnosticText ?? exception.ToString();
                return diagnostic.Contains(
                           "ThrowArgumentOutOfRange_IndexMustBeLessException",
                           StringComparison.Ordinal) &&
                       WpfMarkers.Any(marker => diagnostic.Contains(marker, StringComparison.Ordinal));
            }

            candidate = candidate.InnerException;
        }

        return false;
    }
}
