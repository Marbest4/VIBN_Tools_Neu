using VIBN_Tools.Application;

namespace VIBN_Tools.ContainerToFeeVisual;

internal interface IVisualPlanLogger
{
    void Information(string message);

    void Warning(string message, string details = "");

    void Error(string message, Exception exception);
}

internal sealed class VisualPlanLogger : IVisualPlanLogger
{
    private const string Area = "Container2FEE Visual";

    public void Information(string message) =>
        ApplicationLogService.Instance.Information(Area, message);

    public void Warning(string message, string details = "") =>
        ApplicationLogService.Instance.Warning(Area, message, details);

    public void Error(string message, Exception exception) =>
        ApplicationLogService.Instance.Error(Area, message, exception);
}
