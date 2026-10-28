using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wisp.Core.Security;

/// <summary>
/// Field-level encryption for everything Wisp writes to disk.
/// Format: [version:1][nonce:12][ciphertext:n][tag:16], AES-256-GCM.
/// The <c>purpose</c> string is authenticated as associated data, so a ciphertext
/// copied from one column into another fails to decrypt.
/// </summary>
public sealed class Crypto : IDisposable
{
    const byte FormatVersion = 1;
    const int NonceSize = 12;
    const int TagSize = 16;
    const int Overhead = 1 + NonceSize + TagSize;

    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    readonly byte[] _encKey;
    readonly byte[] _macKey;

    public Crypto(ReadOnlySpan<byte> masterKey)
    {
        if (masterKey.Length != 32)
            throw new ArgumentException("Master key must be 256 bit.", nameof(masterKey));
        _encKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey.ToArray(), 32, info: "wisp/v1/encrypt"u8.ToArray());
        _macKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey.ToArray(), 32, info: "wisp/v1/hash"u8.ToArray());
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, string purpose)
    {
        var output = new byte[plaintext.Length + Overhead];
        output[0] = FormatVersion;
        var nonce = output.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_encKey, TagSize);
        aes.Encrypt(nonce, plaintext,
            output.AsSpan(1 + NonceSize, plaintext.Length),
            output.AsSpan(1 + NonceSize + plaintext.Length, TagSize),
            Encoding.UTF8.GetBytes(purpose));
        return output;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> blob, string purpose)
    {
        if (blob.Length < Overhead || blob[0] != FormatVersion)
            throw new CryptographicException("Unknown or damaged ciphertext.");
        var length = blob.Length - Overhead;
        var plaintext = new byte[length];
        using var aes = new AesGcm(_encKey, TagSize);
        aes.Decrypt(blob.Slice(1, NonceSize),
            blob.Slice(1 + NonceSize, length),
            blob.Slice(1 + NonceSize + length, TagSize),
            plaintext,
            Encoding.UTF8.GetBytes(purpose));
        return plaintext;
    }

    public byte[] EncryptString(string value, string purpose) => Encrypt(Encoding.UTF8.GetBytes(value), purpose);

    public string DecryptString(ReadOnlySpan<byte> blob, string purpose) => Encoding.UTF8.GetString(Decrypt(blob, purpose));

    public byte[] EncryptJson<T>(T value, string purpose) =>
        Encrypt(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), purpose);

    public T DecryptJson<T>(ReadOnlySpan<byte> blob, string purpose) =>
        JsonSerializer.Deserialize<T>(Decrypt(blob, purpose), JsonOptions)
        ?? throw new CryptographicException("Empty payload.");

    /// <summary>Keyed hash used as a lookup key for values that are stored encrypted (Message-ID, folder path).</summary>
    public string Hash(string value, string purpose)
    {
        var input = Encoding.UTF8.GetBytes(purpose + "\0" + value);
        return Convert.ToHexStringLower(HMACSHA256.HashData(_macKey, input));
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_encKey);
        CryptographicOperations.ZeroMemory(_macKey);
    }
}
