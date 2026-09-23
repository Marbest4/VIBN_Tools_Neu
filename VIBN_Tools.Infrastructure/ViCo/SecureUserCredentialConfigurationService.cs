using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using VIBN_Tools.Core.ViCo;

namespace VIBN_Tools.Infrastructure.ViCo;

public interface IUserSecretStore
{
    string? Read(string targetName);

    void Write(string targetName, string secret);

    void Delete(string targetName);
}

/// <summary>
/// Stores generic secrets in the interactive Windows user's Credential
/// Manager. Entries are local to the Windows profile and never written to the
/// repository, application configuration or log.
/// </summary>
public sealed class WindowsCredentialManagerSecretStore : IUserSecretStore
{
    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumCredentialBlobBytes = 2560;

    public string? Read(string targetName)
    {
        ValidateTarget(targetName);
        if (!CredRead(targetName, GenericCredential, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
                return null;
            throw new Win32Exception(error, "Windows Credential Manager konnte den Eintrag nicht lesen.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                return string.Empty;
            if (credential.CredentialBlobSize > MaximumCredentialBlobBytes)
                throw new InvalidDataException("Der gespeicherte Credential-Manager-Eintrag ist zu groß.");

            var bytes = new byte[credential.CredentialBlobSize];
            try
            {
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                return Encoding.Unicode.GetString(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public void Write(string targetName, string secret)
    {
        ValidateTarget(targetName);
        ArgumentNullException.ThrowIfNull(secret);
        var bytes = Encoding.Unicode.GetBytes(secret);
        if (bytes.Length > MaximumCredentialBlobBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new ArgumentOutOfRangeException(nameof(secret), "Das Secret ist für Windows Credential Manager zu lang.");
        }

        var blob = IntPtr.Zero;
        try
        {
            blob = Marshal.AllocCoTaskMem(bytes.Length);
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = targetName,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = Environment.UserName
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows Credential Manager konnte den Eintrag nicht speichern.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (blob != IntPtr.Zero)
            {
                for (var index = 0; index < bytes.Length; index++)
                    Marshal.WriteByte(blob, index, 0);
                Marshal.FreeCoTaskMem(blob);
            }
        }
    }

    public void Delete(string targetName)
    {
        ValidateTarget(targetName);
        if (CredDelete(targetName, GenericCredential, 0))
            return;
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
            throw new Win32Exception(error, "Windows Credential Manager konnte den Eintrag nicht löschen.");
    }

    private static void ValidateTarget(string targetName)
    {
        if (string.IsNullOrWhiteSpace(targetName))
            throw new ArgumentException("Ein Credential-Manager-Zielname ist erforderlich.", nameof(targetName));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}

/// <summary>
/// Uses Credential Manager as the primary store and migrates the former
/// per-user environment variables on first successful access.
/// </summary>
public sealed class SecureUserCredentialConfigurationService : IUserCredentialConfigurationService
{
    public const string KanbanizeTarget = "GROB/VIBN_Tools/KanbanizeApiKey";
    public const string RemoteDesktopTarget = "GROB/VIBN_Tools/RemoteDesktopPassword";
    public const string FeeUsernameTarget = "GROB/VIBN_Tools/FeeUsername";
    public const string FeePasswordTarget = "GROB/VIBN_Tools/FeePassword";

    private readonly IUserSecretStore _store;
    private readonly IUserCredentialConfigurationService _legacy;

    public SecureUserCredentialConfigurationService()
        : this(new WindowsCredentialManagerSecretStore(), new UserEnvironmentCredentialConfigurationService())
    {
    }

    public SecureUserCredentialConfigurationService(
        IUserSecretStore store,
        IUserCredentialConfigurationService legacy)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _legacy = legacy ?? throw new ArgumentNullException(nameof(legacy));
    }

    public UserCredentialConfigurationStatus ReadStatus() => new(
        !string.IsNullOrWhiteSpace(GetKanbanizeApiKey()),
        !string.IsNullOrWhiteSpace(GetRemoteDesktopPassword()),
        !string.IsNullOrWhiteSpace(GetFeeUsername()) && !string.IsNullOrEmpty(GetFeePassword()));

    public string? GetKanbanizeApiKey() => ReadOrMigrate(
        KanbanizeTarget,
        _legacy.GetKanbanizeApiKey,
        _legacy.DeleteKanbanizeApiKey)?.Trim();

    public string? GetRemoteDesktopPassword() => ReadOrMigrate(
        RemoteDesktopTarget,
        _legacy.GetRemoteDesktopPassword,
        _legacy.DeleteRemoteDesktopPassword);

    public string? GetFeeUsername() => ReadOrMigrate(
        FeeUsernameTarget,
        _legacy.GetFeeUsername,
        DeleteLegacyFeeCredentialsIfComplete)?.Trim();

    public string? GetFeePassword() => ReadOrMigrate(
        FeePasswordTarget,
        _legacy.GetFeePassword,
        DeleteLegacyFeeCredentialsIfComplete);

    public void SaveKanbanizeApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Der Kanbanize API-Key darf nicht leer sein.", nameof(apiKey));
        _store.Write(KanbanizeTarget, apiKey.Trim());
        _legacy.DeleteKanbanizeApiKey();
    }

    public void SaveRemoteDesktopPassword(string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Das Remote-Desktop-Passwort darf nicht leer sein.", nameof(password));
        _store.Write(RemoteDesktopTarget, password);
        _legacy.DeleteRemoteDesktopPassword();
    }

    public void SaveFeeCredentials(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Der FEE-Benutzer darf nicht leer sein.", nameof(username));
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Das FEE-Passwort darf nicht leer sein.", nameof(password));

        // Write the password first. A partially completed operation therefore
        // never reports a usable credential pair.
        _store.Write(FeePasswordTarget, password);
        try
        {
            _store.Write(FeeUsernameTarget, username.Trim());
        }
        catch
        {
            try
            {
                _store.Delete(FeePasswordTarget);
            }
            catch
            {
                // Preserve the original username-write failure. With no
                // username, the leftover password can never form a usable pair.
            }
            throw;
        }
        _legacy.DeleteFeeCredentials();
    }

    public void DeleteKanbanizeApiKey()
    {
        _store.Delete(KanbanizeTarget);
        _legacy.DeleteKanbanizeApiKey();
    }

    public void DeleteRemoteDesktopPassword()
    {
        _store.Delete(RemoteDesktopTarget);
        _legacy.DeleteRemoteDesktopPassword();
    }

    public void DeleteFeeCredentials()
    {
        _store.Delete(FeeUsernameTarget);
        _store.Delete(FeePasswordTarget);
        _legacy.DeleteFeeCredentials();
    }

    private void DeleteLegacyFeeCredentialsIfComplete()
    {
        if (!string.IsNullOrWhiteSpace(_store.Read(FeeUsernameTarget)) &&
            !string.IsNullOrEmpty(_store.Read(FeePasswordTarget)))
        {
            _legacy.DeleteFeeCredentials();
        }
    }

    private string? ReadOrMigrate(string target, Func<string?> readLegacy, Action deleteLegacy)
    {
        string? secureValue;
        try
        {
            secureValue = _store.Read(target);
        }
        catch (Exception exception) when (IsStoreUnavailable(exception))
        {
            // Services and restricted launch contexts may have no interactive
            // Windows logon session. Keep the application usable and retain
            // the legacy value; explicit Save still fails rather than storing
            // a new secret insecurely.
            return readLegacy();
        }
        if (!string.IsNullOrEmpty(secureValue))
            return secureValue;

        var legacyValue = readLegacy();
        if (string.IsNullOrEmpty(legacyValue))
            return null;

        try
        {
            _store.Write(target, legacyValue);
            deleteLegacy();
        }
        catch (Exception exception) when (IsStoreUnavailable(exception))
        {
            // Migration is all-or-retain: never delete the only usable value
            // when Credential Manager cannot persist it.
        }
        return legacyValue;
    }

    private static bool IsStoreUnavailable(Exception exception) => exception is
        Win32Exception or
        InvalidDataException or
        UnauthorizedAccessException or
        PlatformNotSupportedException or
        DllNotFoundException;
}
