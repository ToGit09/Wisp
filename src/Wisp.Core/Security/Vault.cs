using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;

namespace Wisp.Core.Security;

public enum VaultProtection
{
    /// <summary>Key protected by the operating system (DPAPI on Windows, Secret Service on Linux).</summary>
    System,
    /// <summary>Key wrapped with a key derived from a master password (Argon2id).</summary>
    MasterPassword,
}

/// <summary>
/// Holds the 256-bit data key. The key itself never touches the disk in plain form;
/// vault.json only contains a DPAPI blob, a Secret Service reference, or a password-wrapped key.
/// </summary>
public sealed class Vault
{
    // Argon2id parameters (OWASP 2024+ recommendation is >= 19 MiB / 2 passes; we go higher).
    const int ArgonMemoryKiB = 64 * 1024;
    const int ArgonIterations = 3;
    const int ArgonParallelism = 2;
    static readonly byte[] DpapiEntropy = "Wisp vault v1"u8.ToArray();
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    readonly string _path;

    public Vault(string path) => _path = path;

    sealed record VaultFile(int Version, VaultProtection Protection, string? Dpapi, string? SecretId,
        string? Salt, int? Memory, int? Iterations, int? Parallelism, string? Wrapped);

    public bool Exists => File.Exists(_path);

    public VaultProtection? Protection => Exists ? Read().Protection : null;

    /// <summary>True if the OS can protect the key (always on Windows, on Linux only with a running Secret Service).</summary>
    public static bool SystemProtectionAvailable =>
        OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && LibSecret.IsAvailable());

    /// <summary>Creates a fresh vault and returns the new data key.</summary>
    public byte[] Create(VaultProtection protection, string? masterPassword = null)
    {
        if (Exists) throw new InvalidOperationException("Vault already exists.");
        var key = RandomNumberGenerator.GetBytes(32);
        Write(Protect(key, protection, masterPassword));
        return key;
    }

    /// <summary>Unlocks a vault that is protected by the operating system.</summary>
    public byte[] UnlockWithSystem()
    {
        var file = Read();
        if (file.Protection != VaultProtection.System)
            throw new InvalidOperationException("Vault needs the master password.");

        if (OperatingSystem.IsWindows())
            return ProtectedData.Unprotect(Convert.FromBase64String(file.Dpapi!), DpapiEntropy, DataProtectionScope.CurrentUser);
        if (OperatingSystem.IsLinux())
        {
            var stored = LibSecret.Lookup(file.SecretId!)
                ?? throw new CryptographicException("Der Schlüssel wurde im Schlüsselbund nicht gefunden.");
            return Convert.FromBase64String(stored);
        }
        throw new PlatformNotSupportedException();
    }

    /// <summary>Unlocks with the master password. Throws <see cref="CryptographicException"/> if it is wrong.</summary>
    public byte[] UnlockWithPassword(string password)
    {
        var file = Read();
        if (file.Protection != VaultProtection.MasterPassword)
            throw new InvalidOperationException("Vault is not password protected.");
        var kek = DeriveKek(password, Convert.FromBase64String(file.Salt!), file.Memory!.Value, file.Iterations!.Value, file.Parallelism!.Value);
        try
        {
            using var crypto = new Crypto(kek);
            return crypto.Decrypt(Convert.FromBase64String(file.Wrapped!), "vault.key");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>Switches protection mode (e.g. add or remove a master password). Needs the unlocked key.</summary>
    public void ChangeProtection(byte[] key, VaultProtection protection, string? masterPassword = null)
    {
        var old = Read();
        Write(Protect(key, protection, masterPassword));
        if (old.SecretId is not null && OperatingSystem.IsLinux())
        {
            try { LibSecret.Clear(old.SecretId); } catch { /* best effort */ }
        }
    }

    VaultFile Protect(byte[] key, VaultProtection protection, string? masterPassword)
    {
        if (protection == VaultProtection.MasterPassword)
        {
            if (string.IsNullOrEmpty(masterPassword)) throw new ArgumentException("Master password required.");
            var salt = RandomNumberGenerator.GetBytes(16);
            var kek = DeriveKek(masterPassword, salt, ArgonMemoryKiB, ArgonIterations, ArgonParallelism);
            try
            {
                using var crypto = new Crypto(kek);
                var wrapped = crypto.Encrypt(key, "vault.key");
                return new VaultFile(1, protection, null, null, Convert.ToBase64String(salt),
                    ArgonMemoryKiB, ArgonIterations, ArgonParallelism, Convert.ToBase64String(wrapped));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
        }

        if (OperatingSystem.IsWindows())
        {
            var blob = ProtectedData.Protect(key, DpapiEntropy, DataProtectionScope.CurrentUser);
            return new VaultFile(1, protection, Convert.ToBase64String(blob), null, null, null, null, null, null);
        }
        if (OperatingSystem.IsLinux())
        {
            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            LibSecret.Store(id, "Wisp – Datenschlüssel", Convert.ToBase64String(key));
            return new VaultFile(1, protection, null, id, null, null, null, null, null);
        }
        throw new PlatformNotSupportedException();
    }

    static byte[] DeriveKek(string password, byte[] salt, int memory, int iterations, int parallelism)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memory,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(32);
    }

    VaultFile Read() =>
        JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(_path), JsonOptions)
        ?? throw new InvalidDataException("vault.json is empty.");

    void Write(VaultFile file)
    {
        // Write to a temp file first so a crash never leaves a half-written vault.
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOptions));
        AppPaths.RestrictFile(tmp);
        File.Move(tmp, _path, overwrite: true);
    }
}
