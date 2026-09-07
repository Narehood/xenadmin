using System.Security.Cryptography;
using System.Text;

namespace XcpNgCenter.Shell.Services;

public sealed record MainPasswordMetadata(string Kdf, int Iterations, string Salt, string Verifier);

/// <summary>A session-only encryption key. Persist Metadata, never this object or its key.</summary>
public sealed class MainPasswordProtection : IDisposable
{
    private const int Iterations = 600_000;
    private const string Kdf = "PBKDF2-SHA256";
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("XcpNgCenter saved password mp2");
    private byte[]? _key;

    public MainPasswordMetadata Metadata { get; }

    private MainPasswordProtection(MainPasswordMetadata metadata, byte[] key)
    {
        Metadata = metadata;
        _key = key;
    }

    public static MainPasswordProtection Create(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var metadata = new MainPasswordMetadata(Kdf, Iterations,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), "");
        return Derive(password, metadata, verify: false)!;
    }

    public static MainPasswordProtection? Unlock(string password, MainPasswordMetadata metadata)
    {
        if (string.IsNullOrEmpty(password))
            return null;
        return Derive(password, metadata, verify: true);
    }

    private static MainPasswordProtection? Derive(string password, MainPasswordMetadata metadata, bool verify)
    {
        // Bound untrusted parameters before spending CPU or allocating key material.
        if (metadata.Kdf != Kdf || metadata.Iterations < Iterations || metadata.Iterations > 2_000_000)
            throw new CryptographicException("Unsupported main-password key derivation parameters.");
        var salt = Convert.FromBase64String(metadata.Salt);
        if (salt.Length != 32)
            throw new CryptographicException("Invalid main-password salt.");
        var root = Rfc2898DeriveBytes.Pbkdf2(password, salt, metadata.Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            // Domain-separated one-way outputs: knowing the verifier does not reveal the encryption key.
            var verifier = HMACSHA256.HashData(root, Encoding.UTF8.GetBytes("XcpNgCenter main password verifier v2"));
            if (verify && !CryptographicOperations.FixedTimeEquals(verifier, Convert.FromBase64String(metadata.Verifier)))
                return null;
            var key = HMACSHA256.HashData(root, Encoding.UTF8.GetBytes("XcpNgCenter main password encryption v2"));
            return new MainPasswordProtection(metadata with { Verifier = Convert.ToBase64String(verifier) }, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(root);
        }
    }

    public string Protect(string password)
    {
        ObjectDisposedException.ThrowIf(_key == null, this);
        var plain = Encoding.UTF8.GetBytes(password);
        var payload = new byte[12 + 16 + plain.Length];
        RandomNumberGenerator.Fill(payload.AsSpan(0, 12));
        try
        {
            using var aes = new AesGcm(_key!, 16);
            aes.Encrypt(payload.AsSpan(0, 12), plain, payload.AsSpan(28), payload.AsSpan(12, 16), AssociatedData);
            return "mp2:" + Convert.ToBase64String(payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public string Unprotect(string encrypted)
    {
        ObjectDisposedException.ThrowIf(_key == null, this);
        if (!encrypted.StartsWith("mp2:", StringComparison.Ordinal))
            throw new CryptographicException("Unsupported main-password credential format.");
        var payload = Convert.FromBase64String(encrypted[4..]);
        if (payload.Length < 28)
            throw new CryptographicException("Invalid saved credential.");
        var plain = new byte[payload.Length - 28];
        try
        {
            using var aes = new AesGcm(_key!, 16);
            aes.Decrypt(payload.AsSpan(0, 12), payload.AsSpan(28), payload.AsSpan(12, 16), plain, AssociatedData);
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Dispose()
    {
        if (_key != null)
            CryptographicOperations.ZeroMemory(_key);
        _key = null;
    }
}
