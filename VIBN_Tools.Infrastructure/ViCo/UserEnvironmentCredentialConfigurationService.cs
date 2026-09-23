using VIBN_Tools.Core.ViCo;

namespace VIBN_Tools.Infrastructure.ViCo;

/// <summary>
/// Replaces the former one-time PowerShell commands with the equivalent
/// per-user environment configuration. Values are also updated in the current
/// process so a running application can use a changed API key immediately.
/// </summary>
public sealed class UserEnvironmentCredentialConfigurationService : IUserCredentialConfigurationService
{
    public const string KanbanizeApiKeyVariable = "VIBN_VICO_KANBANIZE_API_KEY";
    public const string RemoteDesktopPasswordVariable = "VIBN_RDP_PASSWORD";
    public const string FeeUsernameVariable = "VIBN_FEE_USERNAME";
    public const string FeePasswordVariable = "VIBN_FEE_PASSWORD";

    private readonly Func<string, EnvironmentVariableTarget, string?> _read;
    private readonly Action<string, string?, EnvironmentVariableTarget> _write;

    public UserEnvironmentCredentialConfigurationService()
        : this(Environment.GetEnvironmentVariable, Environment.SetEnvironmentVariable)
    {
    }

    /// <summary>Test seam which avoids writing to a developer's real environment.</summary>
    public UserEnvironmentCredentialConfigurationService(
        Func<string, EnvironmentVariableTarget, string?> read,
        Action<string, string?, EnvironmentVariableTarget> write)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _write = write ?? throw new ArgumentNullException(nameof(write));
    }

    public UserCredentialConfigurationStatus ReadStatus() => new(
        HasValue(KanbanizeApiKeyVariable),
        HasValue(RemoteDesktopPasswordVariable),
        HasValue(FeeUsernameVariable) && HasValue(FeePasswordVariable));

    public string? GetKanbanizeApiKey() => ReadValue(KanbanizeApiKeyVariable)?.Trim();

    public string? GetRemoteDesktopPassword() => ReadValue(RemoteDesktopPasswordVariable);

    public string? GetFeeUsername() => ReadValue(FeeUsernameVariable)?.Trim();

    public string? GetFeePassword() => ReadValue(FeePasswordVariable);

    public void SaveKanbanizeApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Der Kanbanize API-Key darf nicht leer sein.", nameof(apiKey));
        WriteValue(KanbanizeApiKeyVariable, apiKey.Trim());
    }

    public void SaveRemoteDesktopPassword(string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Das Remote-Desktop-Passwort darf nicht leer sein.", nameof(password));
        WriteValue(RemoteDesktopPasswordVariable, password);
    }

    public void SaveFeeCredentials(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Der FEE-Benutzer darf nicht leer sein.", nameof(username));
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Das FEE-Passwort darf nicht leer sein.", nameof(password));
        WriteValue(FeeUsernameVariable, username.Trim());
        WriteValue(FeePasswordVariable, password);
    }

    public void DeleteKanbanizeApiKey() => WriteValue(KanbanizeApiKeyVariable, null);

    public void DeleteRemoteDesktopPassword() => WriteValue(RemoteDesktopPasswordVariable, null);

    public void DeleteFeeCredentials()
    {
        WriteValue(FeeUsernameVariable, null);
        WriteValue(FeePasswordVariable, null);
    }

    private bool HasValue(string name) => !string.IsNullOrWhiteSpace(ReadValue(name));

    private string? ReadValue(string name)
    {
        try
        {
            return _read(name, EnvironmentVariableTarget.User) ??
                   _read(name, EnvironmentVariableTarget.Process);
        }
        catch (Exception exception) when (
            exception is System.Security.SecurityException or
                UnauthorizedAccessException or ArgumentException)
        {
            // Status checks are diagnostic and must not prevent either WPF
            // application from starting under a restricted user policy.
            try
            {
                return _read(name, EnvironmentVariableTarget.Process);
            }
            catch (Exception processException) when (
                processException is System.Security.SecurityException or
                    UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }
    }

    private void WriteValue(string name, string? value)
    {
        // Persist first. If policy blocks the registry-backed user variable,
        // do not pretend that configuration succeeded only for this process.
        _write(name, value, EnvironmentVariableTarget.User);
        _write(name, value, EnvironmentVariableTarget.Process);
    }
}
