using System.Security.Cryptography;
using System.Text;
using Bdk.Crypto;

namespace BdkHourFarm;

/// <summary>
/// Log no console (o systemd/journald guarda a saída) e, se houver chave pública de backup,
/// também em arquivos CRIPTOGRAFADOS em data/logs. Só o seu PC (chave privada) consegue ler.
/// Mantém as últimas linhas em memória para o painel.
/// </summary>
internal static class Log
{
    private const int MaxBuffered = 300;
    private const int MaxPending = 5000;
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const int RetentionDays = 30;

    private static readonly object Gate = new();
    private static readonly object FileGate = new();
    private static readonly LinkedList<string> Buffer = new();
    private static readonly List<string> Pending = new();

    private static bool _console = true;
    private static RSA? _key;
    private static string? _dir;
    private static Timer? _timer;

    private static Hybrid.LogEncryptor? _encryptor;
    private static string? _file;
    private static long _fileBytes;

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("AVISO", message);
    public static void Error(string message) => Write("ERRO ", message);

    /// <summary>Últimas linhas de log, da mais antiga para a mais recente.</summary>
    public static string[] Recent(int count)
    {
        count = Math.Clamp(count, 1, MaxBuffered);

        lock (Gate)
        {
            return Buffer.Skip(Math.Max(0, Buffer.Count - count)).ToArray();
        }
    }

    /// <summary>Liga os arquivos cifrados (se houver chave) e escolhe se o console continua recebendo o log.</summary>
    public static void Configure(RSA? publicKey, string logDir, bool console)
    {
        lock (Gate) _console = console;

        if (publicKey is null) return;

        try
        {
            Directory.CreateDirectory(logDir);
            RemoveOld(logDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"Não consegui preparar a pasta de logs cifrados: {ex.Message}");
            return;
        }

        lock (Gate)
        {
            _key = publicKey;
            _dir = logDir;
        }

        _timer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
    }

    /// <summary>Grava no disco o que está pendente (chamado a cada 10 s e ao encerrar).</summary>
    public static void Flush()
    {
        string[] lines;
        RSA? key;
        string? dir;

        lock (Gate)
        {
            key = _key;
            dir = _dir;
            if (key is null || dir is null || Pending.Count == 0) return;

            lines = Pending.ToArray();
            Pending.Clear();
        }

        lock (FileGate)
        {
            try
            {
                if (_encryptor is null || _file is null || _fileBytes > MaxFileBytes)
                    OpenNewFile(key, dir);

                var record = _encryptor!.Seal(Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"));

                using var stream = new FileStream(_file!, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(record, 0, record.Length);
                _fileBytes += record.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                // Perde só este lote; o console continua mostrando.
                Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [AVISO] Não consegui gravar o log cifrado: {ex.Message}");
                _encryptor = null;
            }
        }
    }

    private static void OpenNewFile(RSA key, string dir)
    {
        var encryptor = new Hybrid.LogEncryptor(key);
        var path = Path.Combine(dir, $"log-{DateTime.Now:yyyyMMdd-HHmmss}.bdklog");

        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using (var stream = new FileStream(path, options))
        {
            stream.Write(encryptor.Header, 0, encryptor.Header.Length);
        }

        _encryptor = encryptor;
        _file = path;
        _fileBytes = encryptor.Header.Length;
    }

    private static void RemoveOld(string dir)
    {
        var limit = DateTime.UtcNow.AddDays(-RetentionDays);

        foreach (var file in Directory.EnumerateFiles(dir, "*.bdklog"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < limit) File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // fica para a próxima
            }
        }
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";

        lock (Gate)
        {
            if (_console) Console.WriteLine(line);

            Buffer.AddLast(line);
            while (Buffer.Count > MaxBuffered)
                Buffer.RemoveFirst();

            if (_key is not null)
            {
                Pending.Add(line);
                if (Pending.Count > MaxPending)
                    Pending.RemoveRange(0, Pending.Count - MaxPending);
            }
        }
    }
}
