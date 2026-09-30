using System.Reflection;
using VIBN_Tools.Core.ViCo;

namespace VIBN_Tools.IbnRemote;

internal static class IbnRemoteDeploymentConfiguration
{
    private const string FilterMetadataName = "IbnRemoteInWorkFilter";
    private const string ApiKeyMetadataName = "IbnRemoteApiKey";
    private const string RemoteDesktopPasswordMetadataName = "IbnRemoteRemoteDesktopPassword";

    public static string InWorkFilter => ReadMetadata(FilterMetadataName);

    public static string KanbanizeApiKey => ReadMetadata(ApiKeyMetadataName);

    public static string RemoteDesktopPassword => ReadMetadata(RemoteDesktopPasswordMetadataName);

    private static string ReadMetadata(string name) =>
        (Assembly.GetEntryAssembly() ?? typeof(IbnRemoteDeploymentConfiguration).Assembly)
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => string.Equals(
            attribute.Key,
            name,
            StringComparison.Ordinal))?.Value?.Trim() ?? string.Empty;
}

/// <summary>
/// Supplies the deliberately embedded IBN deployment values. Values live only
/// in process memory at runtime; the RDP service creates and removes the
/// Windows credential entry immediately around the connection launch.
/// </summary>
internal sealed class IbnRemoteEmbeddedCredentialConfigurationService : IUserCredentialConfigurationService
{
    public UserCredentialConfigurationStatus ReadStatus() => new(
        !string.IsNullOrWhiteSpace(IbnRemoteDeploymentConfiguration.KanbanizeApiKey),
        !string.IsNullOrWhiteSpace(IbnRemoteDeploymentConfiguration.RemoteDesktopPassword));

    public string? GetKanbanizeApiKey() => EmptyAsNull(
        IbnRemoteDeploymentConfiguration.KanbanizeApiKey);

    public string? GetRemoteDesktopPassword() => EmptyAsNull(
        IbnRemoteDeploymentConfiguration.RemoteDesktopPassword);

    public string? GetFeeUsername() => null;

    public string? GetFeePassword() => null;

    public void SaveKanbanizeApiKey(string apiKey) => ThrowReadOnly();

    public void SaveRemoteDesktopPassword(string password) => ThrowReadOnly();

    public void SaveFeeCredentials(string username, string password) => ThrowReadOnly();

    public void DeleteKanbanizeApiKey() => ThrowReadOnly();

    public void DeleteRemoteDesktopPassword() => ThrowReadOnly();

    public void DeleteFeeCredentials() => ThrowReadOnly();

    private static string? EmptyAsNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static void ThrowReadOnly() => throw new NotSupportedException(
        "Die Zugangswerte der IBN-Remote-EXE sind beim Publish fest eingebettet und schreibgeschützt.");
}
