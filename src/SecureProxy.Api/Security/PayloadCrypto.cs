using System.Security.Cryptography;
using System.Text;

namespace SecureProxy.Api.Security;

/// <summary>Resolves key material by reference. The database only ever stores the reference.</summary>
public interface ISecretKeyProvider
{
    /// <summary>Returns the 256-bit key, or null when the reference cannot be resolved.</summary>
    byte[]? GetKey(string keyReference);
}

/// <summary>
/// Local/assessment key source: reads "EncryptionKeys:{reference}" (base64, 32 bytes) from
/// configuration, which is expected to be fed by environment variables or user-secrets.
/// Production swaps this class for a Key Vault / secret manager implementation.
/// </summary>
public sealed class ConfigurationSecretKeyProvider(IConfiguration configuration) : ISecretKeyProvider
{
    public byte[]? GetKey(string keyReference)
    {
        var value = configuration[$"EncryptionKeys:{keyReference}"];
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var key = Convert.FromBase64String(value);
            return key.Length == PayloadCrypto.KeySize ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// AES-256-GCM envelope: nonce (12 bytes), ciphertext, tag (16 bytes), all base64.
/// The associated data binds a ciphertext to one operation and destination, so a payload
/// captured for one operation cannot be replayed against another.
/// </summary>
public static class PayloadCrypto
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;

    public static byte[] AssociatedData(string clientId, string urlId) =>
        Encoding.UTF8.GetBytes($"{clientId}|{urlId}");

    /// <exception cref="CryptographicException">Authentication failed or sizes are wrong.</exception>
    /// <exception cref="FormatException">A field is not valid base64.</exception>
    public static byte[] Decrypt(byte[] key, string nonceB64, string ciphertextB64, string tagB64, byte[] associatedData)
    {
        var nonce = Convert.FromBase64String(nonceB64);
        var ciphertext = Convert.FromBase64String(ciphertextB64);
        var tag = Convert.FromBase64String(tagB64);
        if (nonce.Length != NonceSize || tag.Length != TagSize || key.Length != KeySize)
            throw new CryptographicException("Invalid envelope sizes.");

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        return plaintext;
    }

    public static (string Nonce, string Ciphertext, string Tag) Encrypt(byte[] key, byte[] plaintext, byte[] associatedData)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        return (Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
    }
}
