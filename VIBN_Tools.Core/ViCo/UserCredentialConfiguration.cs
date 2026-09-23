namespace VIBN_Tools.Core.ViCo;

/// <summary>
/// Reports whether the current Windows user has configured the two secrets
/// used by the ViCo/Kanbanize and automatic RDP workflows. Secret values are
/// deliberately not part of this status object.
/// </summary>
public sealed record UserCredentialConfigurationStatus(
    bool HasKanbanizeApiKey,
    bool HasRemoteDesktopPassword,
    bool HasFeeCredentials = false);

/// <summary>
/// Manages per-user integration credentials without exposing stored values to
/// view models. Blank UI fields therefore never overwrite an existing secret.
/// </summary>
public interface IUserCredentialConfigurationService
{
    UserCredentialConfigurationStatus ReadStatus();

    string? GetKanbanizeApiKey();

    string? GetRemoteDesktopPassword();

    string? GetFeeUsername();

    string? GetFeePassword();

    void SaveKanbanizeApiKey(string apiKey);

    void SaveRemoteDesktopPassword(string password);

    void SaveFeeCredentials(string username, string password);

    void DeleteKanbanizeApiKey();

    void DeleteRemoteDesktopPassword();

    void DeleteFeeCredentials();
}
