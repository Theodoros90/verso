using System.Security.Cryptography;
using System.Text;
using Verso.Application.Mail;

namespace Verso.Infrastructure.Security;

/// <summary>
/// AES-256-GCM with a master key that is not in the database (ADR 0015): <c>Secrets:MasterKey</c>, 32 random bytes as base64
/// (environment variable <c>Secrets__MasterKey</c> or the bank's secret store). Stored form: <c>v1.</c> + base64(nonce 12 | tag 16 | ciphertext).
/// The context is the associated data, so a value copied to another row fails to open.
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const string Version = "v1.";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[]? _key;

    /// <param name="masterKeyBase64">Null, empty or not exactly 32 bytes of base64 means "not configured".</param>
    public AesGcmSecretProtector(string? masterKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(masterKeyBase64))
        {
            return;
        }

        try
        {
            var key = Convert.FromBase64String(masterKeyBase64.Trim());
            if (key.Length == 32)
            {
                _key = key;
            }
        }
        catch (FormatException)
        {
            // Not base64: treated as not configured, the API says so when a password is saved.
        }
    }

    public bool IsConfigured => _key is not null;

    /// <summary>A new random master key, for the administrator to set once (see the runbook).</summary>
    public static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public string Protect(string secret, string context)
    {
        var key = _key ?? throw new InvalidOperationException("Secrets:MasterKey is not configured.");
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plain = Encoding.UTF8.GetBytes(secret);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(context));
        CryptographicOperations.ZeroMemory(plain);
        return Version + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string? Unprotect(string protectedValue, string context)
    {
        if (_key is null || !protectedValue.StartsWith(Version, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var data = Convert.FromBase64String(protectedValue[Version.Length..]);
            if (data.Length < NonceSize + TagSize)
            {
                return null;
            }

            var nonce = data.AsSpan(0, NonceSize);
            var tag = data.AsSpan(NonceSize, TagSize);
            var cipher = data.AsSpan(NonceSize + TagSize);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(context));
            var text = Encoding.UTF8.GetString(plain);
            CryptographicOperations.ZeroMemory(plain);
            return text;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
