using System.Security.Cryptography;
using XenCenterLib;

namespace XcpNgCenter.Shell.Services;

/// <summary>Owns the credential format, migration commit and unlocked session key.</summary>
public sealed class MainPasswordVault : IDisposable
{
    private readonly SavedServerStore _store;
    private readonly ShellAppSettings _settings;
    private readonly object _gate = new();
    private SavedServerStore.Document? _document;
    private MainPasswordProtection? _session;
    private Exception? _loadError;

    public MainPasswordVault(SavedServerStore store, ShellAppSettings settings)
    {
        _store = store;
        _settings = settings;
        try
        {
            using var fileLock = _store.AcquireVaultLock();
            _document = store.ReadDocument();
            // Retry cleanup after a process interruption between the two file writes.
            if (_document != null)
                ClearLegacySettings();
        }
        catch (Exception ex)
        {
            _loadError = ex;
        }
    }

    public bool RequiresMainPassword => _loadError != null || (_document != null
        ? _document.MainPassword != null
        : _settings.RequireMainPassword || _settings.GetMainPasswordHash() != null);

    public bool IsUnlocked => _session != null;
    public string? CleanupWarning { get; private set; }

    public bool VerifyPassword(string password)
    {
        lock (_gate)
        {
            ThrowIfLoadFailed();
            if (_document?.MainPassword is { } metadata)
            {
                using var candidate = MainPasswordProtection.Unlock(password, metadata);
                return candidate != null;
            }
            var expected = _settings.GetMainPasswordHash();
            var actual = EncryptionUtils.ComputeHash(password);
            try
            {
                return !string.IsNullOrEmpty(password) && expected != null
                    && CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actual);
            }
        }
    }

    public bool Unlock(string password, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ThrowIfLoadFailed();
            using var fileLock = _store.AcquireVaultLock();
            EnsureCurrentProtection();
            if (_document?.MainPassword is { } metadata)
            {
                var candidate = MainPasswordProtection.Unlock(password, metadata);
                if (candidate == null)
                    return false;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ReplaceSession(candidate);
                    candidate = null;
                    return true;
                }
                finally { candidate?.Dispose(); }
            }
            if (!VerifyPassword(password))
                return false;
            // Legacy mp1 used a persisted hash as its AES key. Upgrade immediately
            // after a successful unlock, without retaining the plaintext password.
            ChangeProtection(password, password, cancellationToken);
            return true;
        }
    }

    public void ChangePassword(string? newPassword, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ThrowIfLoadFailed();
            using var fileLock = _store.AcquireVaultLock();
            EnsureCurrentProtection();
            if (RequiresMainPassword && !IsUnlocked)
                throw new InvalidOperationException("Unlock saved credentials before changing their protection.");
            ChangeProtection(newPassword, null, cancellationToken);
        }
    }

    private void ChangeProtection(string? newPassword, string? legacyPassword, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MainPasswordProtection? next = newPassword == null ? null : MainPasswordProtection.Create(newPassword);
        try
        {
            var entries = new List<SavedServerEntry>();
            foreach (var entry in _store.LoadRequired())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entry.HasSavedPassword)
                {
                    entries.Add(entry);
                    continue;
                }
                var plain = legacyPassword != null && SavedServerStore.IsMainPasswordProtected(entry.EncryptedPassword)
                    ? SavedServerStore.UnprotectPasswordWithMainPassword(entry.EncryptedPassword, legacyPassword)
                    : Unprotect(entry.EncryptedPassword);
                if (plain == null)
                    throw new CryptographicException("A saved credential could not be decrypted. No credentials were changed.");
                var encrypted = next != null ? next.Protect(plain) : _store.ProtectDevicePassword(plain);
                if (encrypted == null)
                    throw new CryptographicException("A saved credential could not be protected. No credentials were changed.");
                entries.Add(entry with { EncryptedPassword = encrypted });
            }
            var document = new SavedServerStore.Document(2, next?.Metadata, entries);
            // The only commit point: metadata and every ciphertext change together.
            _store.Commit(document, cancellationToken);
            _document = document;
            ReplaceSession(next);
            next = null;
            ClearLegacySettings();
        }
        finally { next?.Dispose(); }
    }

    public string? Protect(string password)
    {
        lock (_gate)
        {
            ThrowIfLoadFailed();
            return RequiresMainPassword ? _session?.Protect(password) : _store.ProtectDevicePassword(password);
        }
    }

    public void Save(IEnumerable<SavedServerEntry> entries)
    {
        lock (_gate)
        {
            ThrowIfLoadFailed();
            using var fileLock = _store.AcquireVaultLock();
            EnsureCurrentProtection();
            var snapshot = entries.ToList();
            var persisted = _store.LoadRequired();
            foreach (var entry in snapshot.Where(e => e.HasSavedPassword))
            {
                // A UI snapshot created before a concurrent migration must never
                // overwrite the newly committed ciphertext with the previous format/key.
                if (persisted.Any(p => p.EncryptedPassword == entry.EncryptedPassword))
                    continue;
                if (RequiresMainPassword)
                {
                    if (_session == null || entry.EncryptedPassword?.StartsWith("mp2:", StringComparison.Ordinal) != true)
                        throw new CryptographicException("Saved credentials changed. Reload before saving.");
                    _session.Unprotect(entry.EncryptedPassword);
                }
                else if (SavedServerStore.IsMainPasswordProtected(entry.EncryptedPassword))
                    throw new CryptographicException("Saved credentials changed. Reload before saving.");
            }
            _store.Save(snapshot);
            if (_store.LastSaveError != null)
                throw new IOException(_store.LastSaveError);
        }
    }

    public string? Unprotect(string? encrypted)
    {
        lock (_gate)
        {
            ThrowIfLoadFailed();
            if (string.IsNullOrEmpty(encrypted))
                return null;
            if (SavedServerStore.IsMainPasswordProtected(encrypted))
                return _session?.Unprotect(encrypted);
            return _store.UnprotectDevicePassword(encrypted);
        }
    }

    private void ClearLegacySettings()
    {
        try
        {
            _settings.ClearLegacyMainPassword();
            CleanupWarning = null;
        }
        catch (Exception ex)
        {
            // The vault is already committed and usable. Never roll it back to mp1.
            CleanupWarning = "Credentials migrated, but obsolete settings cleanup will be retried: " + ex.Message;
        }
    }

    private void ThrowIfLoadFailed()
    {
        if (_loadError != null)
            throw new InvalidDataException("Saved credentials could not be loaded. The file was left unchanged.", _loadError);
    }

    private void EnsureCurrentProtection()
    {
        var current = _store.ReadDocument();
        if (current?.Version != _document?.Version || current?.MainPassword != _document?.MainPassword)
            throw new InvalidOperationException("Saved credential protection changed in another app instance. Restart to load it before saving or unlocking.");
    }

    private void ReplaceSession(MainPasswordProtection? session)
    {
        _session?.Dispose();
        _session = session;
    }

    public void Dispose()
    {
        lock (_gate)
            ReplaceSession(null);
    }
}
