namespace VIBN_Tools.ContainerGeneration.Models;

/// <summary>Action boundaries can recover from ordinary managed failures, not fatal process corruption.</summary>
public static class ContainerGenerationExceptionPolicy
{
    public static bool IsRecoverable(Exception exception) => exception is not
        (OutOfMemoryException or StackOverflowException or AccessViolationException) &&
        (exception.InnerException is null || IsRecoverable(exception.InnerException)) &&
        (exception is not AggregateException aggregate || aggregate.InnerExceptions.All(IsRecoverable));

    public static bool IsContainerGenerationUiFailure(Exception exception) => IsRecoverable(exception) &&
        new[] { "VIBN_Tools.Application.VM.ContainerGenerationPageVM", "VIBN_Tools.Application.View.ContainerGenerationPage",
                "VIBN_Tools.ContainerGeneration." }.Any(marker => exception.ToString().Contains(marker, StringComparison.Ordinal));
}
