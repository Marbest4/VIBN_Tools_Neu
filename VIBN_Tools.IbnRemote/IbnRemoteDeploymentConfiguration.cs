using System.Reflection;
using System.IO;
using System.Text.Json;
using VIBN_Tools.Infrastructure.ViCo;

namespace VIBN_Tools.IbnRemote;

internal static class IbnRemoteDeploymentConfiguration
{
    private const string FilterMetadataName = "IbnRemoteInWorkFilter";

    public static string InWorkFilter =>
        (Assembly.GetEntryAssembly() ?? typeof(IbnRemoteDeploymentConfiguration).Assembly)
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => string.Equals(
            attribute.Key,
            FilterMetadataName,
            StringComparison.Ordinal))?.Value?.Trim() ?? string.Empty;

    public static void ConfigureFromStandardInput()
    {
        var payload = Console.In.ReadToEnd();
        var configuration = JsonSerializer.Deserialize<CredentialInput>(payload) ??
            throw new InvalidDataException("Die IBN-Konfiguration auf stdin ist leer oder ungültig.");
        if (string.IsNullOrWhiteSpace(configuration.KanbanizeApiKey))
            throw new InvalidDataException("Ein Kanbanize API-Key ist erforderlich.");
        if (string.IsNullOrEmpty(configuration.RemoteDesktopPassword))
            throw new InvalidDataException("Ein Remote-Desktop-Passwort ist erforderlich.");

        var credentials = new SecureUserCredentialConfigurationService();
        credentials.SaveKanbanizeApiKey(configuration.KanbanizeApiKey);
        credentials.SaveRemoteDesktopPassword(configuration.RemoteDesktopPassword);
    }

    private sealed record CredentialInput(string KanbanizeApiKey, string RemoteDesktopPassword);
}
