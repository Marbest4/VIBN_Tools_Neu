namespace VIBN_Tools.Application.Behaviors;

/// <summary>
/// Recognizes stale WPF item indices and negative layout sizes. Unrelated
/// argument errors are not hidden by this recovery policy.
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
            if (candidate is ArgumentException && IsNegativeLayoutSize(candidate, diagnosticText ?? exception.ToString()))
                return true;
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

    private static bool IsNegativeLayoutSize(Exception exception, string diagnostic) =>
        exception.Message.Contains("Width and Height must be non-negative", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("Breite und die Höhe dürfen nicht negativ", StringComparison.OrdinalIgnoreCase) ||
        diagnostic.Contains("System.Windows.Size..ctor", StringComparison.Ordinal) ||
        diagnostic.Contains("System.Windows.Rect..ctor", StringComparison.Ordinal) ||
        diagnostic.Contains("System.Windows.Size.set_Width", StringComparison.Ordinal) ||
        diagnostic.Contains("System.Windows.Size.set_Height", StringComparison.Ordinal) ||
        diagnostic.Contains("SyncUniformSizeFlags", StringComparison.Ordinal) &&
        (exception.Message.Contains("negative", StringComparison.OrdinalIgnoreCase) ||
         exception.Message.Contains("negativ", StringComparison.OrdinalIgnoreCase));
}
