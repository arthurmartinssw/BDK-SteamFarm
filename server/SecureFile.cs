using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BdkHourFarm;

internal enum Protection
{
    /// <summary>Texto puro (formato antigo).</summary>
    None,

    /// <summary>Criptografado com uma chave ligada a esta máquina. Inicia sozinho, mas só abre nesta máquina.</summary>
    Machine,

    /// <summary>Criptografado com uma senha que não fica em disco. Exige desbloqueio a cada início.</summary>
    Passphrase,
}

internal sealed record SecureLoad(Protection Mode, string? Plaintext, string? Error);

/// <summary>Arquivos secretos criptografados com AES-256-GCM.</summary>
internal static class SecureFile
{
    private const int Version = 2;
    private const int PassphraseIterations = 600_000;
    private const int MachineIterations = 10_000;
    private const int KeyBytes = 32;
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private sealed class Envelope
    {
        public int V { get; set; }
        public string Mode { get; set; } = "";
        public int Iter { get; set; }
        public string Salt { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string Tag { get; set; } = "";
        public string Data { get; set; } = "";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // ------------------------------------------------------------------ leitura

    public static SecureLoad Read(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SecureLoad(Protection.None, null, ex.Message);
        }

        var envelope = TryParseEnvelope(text);
        if (envelope is null)
            return new SecureLoad(Protection.None, text, null);

        if (envelope.Mode == "passphrase")
            return new SecureLoad(Protection.Passphrase, null, null);

        if (envelope.Mode != "machine")
            return new SecureLoad(Protection.None, null, "Formato de arquivo desconhecido.");

        try
        {
            var secret = MachineSecret() ?? throw new CryptographicException("sem identificador da máquina");
            var key = DeriveMachineKey(secret, Convert.FromBase64String(envelope.Salt));
            return new SecureLoad(Protection.Machine, Decrypt(envelope, key), null);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return new SecureLoad(Protection.Machine, null,
                "Não consegui abrir o arquivo protegido (ele foi criado em outra máquina?). Rode \"login\" de novo.");
        }
    }

    /// <summary>Abre um arquivo protegido por senha. Devolve null se a senha estiver errada.</summary>
    public static string? Unlock(string path, string passphrase)
    {
        try
        {
            var envelope = TryParseEnvelope(File.ReadAllText(path));
            if (envelope is null || envelope.Mode != "passphrase") return null;

            var iterations = Math.Clamp(envelope.Iter, 100_000, 5_000_000);
            var key = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(passphrase),
                Convert.FromBase64String(envelope.Salt),
                iterations,
                HashAlgorithmName.SHA256,
                KeyBytes);

            return Decrypt(envelope, key);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException or IOException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ escrita

    /// <summary>
    /// Grava o arquivo. Devolve false se pediu proteção "Machine" e não deu para proteger
    /// (nesse caso o arquivo foi gravado em texto puro, com aviso no log). No modo "Passphrase"
    /// nunca grava em texto puro: se falhar, lança exceção.
    /// </summary>
    public static bool Write(string path, string plaintext, Protection mode, string? passphrase = null)
    {
        if (mode == Protection.None)
        {
            AtomicFile.WriteText(path, plaintext);
            return true;
        }

        if (mode == Protection.Passphrase)
        {
            AtomicFile.WriteText(path, Encrypt(plaintext, mode, passphrase));
            return true;
        }

        try
        {
            AtomicFile.WriteText(path, Encrypt(plaintext, mode, passphrase));
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException or InvalidOperationException)
        {
            Log.Warn($"Não consegui criptografar {Path.GetFileName(path)}: {ex.Message}. Gravei sem criptografia.");
            AtomicFile.WriteText(path, plaintext);
            return false;
        }
    }

    // ------------------------------------------------------------------ interno

    private static Envelope? TryParseEnvelope(string text)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(text, JsonOptions);
            return envelope is { V: >= 2 } && !string.IsNullOrEmpty(envelope.Data) ? envelope : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Encrypt(string plaintext, Protection mode, string? passphrase)
    {
        if (!AesGcm.IsSupported)
            throw new PlatformNotSupportedException("AES-GCM indisponível neste sistema");

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] key;
        int iterations;
        string modeName;

        if (mode == Protection.Passphrase)
        {
            if (string.IsNullOrEmpty(passphrase))
                throw new InvalidOperationException("senha vazia");

            modeName = "passphrase";
            iterations = PassphraseIterations;
            key = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, KeyBytes);
        }
        else
        {
            var secret = MachineSecret() ?? throw new InvalidOperationException("sem identificador da máquina");

            modeName = "machine";
            iterations = MachineIterations;
            key = DeriveMachineKey(secret, salt);
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        var tag = new byte[TagBytes];

        using (var aes = new AesGcm(key, TagBytes))
        {
            aes.Encrypt(nonce, data, cipher, tag, Aad(modeName));
        }

        var envelope = new Envelope
        {
            V = Version,
            Mode = modeName,
            Iter = iterations,
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag),
            Data = Convert.ToBase64String(cipher),
        };

        return JsonSerializer.Serialize(envelope, JsonOptions);
    }

    private static string Decrypt(Envelope envelope, byte[] key)
    {
        var nonce = Convert.FromBase64String(envelope.Nonce);
        var tag = Convert.FromBase64String(envelope.Tag);
        var cipher = Convert.FromBase64String(envelope.Data);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(nonce, cipher, tag, plain, Aad(envelope.Mode));

        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] Aad(string mode) => Encoding.UTF8.GetBytes($"bdk-secure|v{Version}|{mode}");

    private static byte[] DeriveMachineKey(string secret, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes("bdk-machine|" + secret), salt, MachineIterations, HashAlgorithmName.SHA256, KeyBytes);

    /// <summary>Identificador único desta instalação do sistema (não sai da máquina).</summary>
    private static string? MachineSecret()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                return key?.GetValue("MachineGuid") as string;
            }

            foreach (var file in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
            {
                if (!File.Exists(file)) continue;

                var id = File.ReadAllText(file).Trim();
                if (id.Length >= 16) return id;
            }
        }
        catch (Exception)
        {
            // sem identificador: o chamador cai para o aviso/texto puro
        }

        return null;
    }
}
