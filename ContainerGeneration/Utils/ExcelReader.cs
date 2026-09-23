using ClosedXML.Excel;

using System.Diagnostics;

namespace VIBN_Tools.ContainerGeneration.Utils
{
    /// <summary>
    /// Static class containing read methods for Excel files relevant to the application.
    /// </summary>
    public static class ExcelReader
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Reads an Excel file from the specified file path.
        /// This method reads the Excel file, logs the success or failure, and returns the result.
        /// </summary>
        /// <param name="excelPath">The path to the Excel file.</param>
        /// <returns>A <see cref="Result{T}"/> containing the Excel workbook or an error message.</returns>
        public static Result<IXLWorkbook> Read(string excelPath)
        {
            try
            {
                XLWorkbook workbook = new(excelPath);
                Logger.Info("Excel document at {path} read successfully", excelPath);
                return Result<IXLWorkbook>.Success(workbook);
            }
            catch (MissingMethodException ex) when (
                ex.Message.Contains("SixLabors.Fonts", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("FontMetrics", StringComparison.OrdinalIgnoreCase))
            {
                var fontsAssembly = typeof(SixLabors.Fonts.Font).Assembly;
                var fileVersion = FileVersionInfo.GetVersionInfo(fontsAssembly.Location).FileVersion ?? "unbekannt";
                var message =
                    "Die Excel-Laufzeit enthält eine inkompatible SixLabors.Fonts-Version. " +
                    $"Geladen wurde {fileVersion} aus '{fontsAssembly.Location}'. " +
                    "Ausgabeordner bereinigen und die Anwendung vollständig neu bauen bzw. installieren.";
                Logger.Error(ex, "Incompatible spreadsheet dependency while reading {path}. {details}", excelPath, message);
                return Result<IXLWorkbook>.Failure(message);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error while reading {path}", excelPath);
                return Result<IXLWorkbook>.Failure(ex.Message);
            }
        }
    }
}
