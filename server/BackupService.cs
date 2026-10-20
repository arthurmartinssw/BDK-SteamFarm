using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Bdk.Crypto;

namespace BdkHourFarm;

internal sealed record BackupOutcome(string? Path, long Bytes, string? Error);

internal sealed record BackupStatus(bool Enabled, string? Fingerprint, int Count, string? LastAt, long LastBytes, int Keep);

/// <summary>
/// Backup da pasta data/ em um único arquivo .bdkb, cifrado com a chave pública (só o seu PC abre).
/// Os segredos que dependem desta máquina (sessão, webhook, chave da Steam) entram em texto dentro
/// do pacote cifrado, para o backup funcionar em outra VPS.
/// </summary>
internal sealed class BackupService
{
    public const int Keep = 7;

    private static readonly string[] MachineBound = { "session.json", "webhook.dat", "webapikey.dat" };

    private readonly object _gate = new();
    private readonly string _dataDir;
    private readonly string _backupDir;
    private readonly RSA? _key;

    public BackupService(string dataDir, RSA? publicKey)
    {
        _dataDir = dataDir;
        _backupDir = Path.Combine(dataDir, "backups");
        _key = publicKey;
    }

    public bool Enabled => _key is not null;

    public BackupOutcome Create(string? destination = null)
    {
        if (_key is null)
            return new BackupOutcome(null, 0, $"Sem chave pública. Coloque o arquivo {Vault.FileName} na pasta data/ e reinicie.");

        lock (_gate)
        {
            try
            {
                var blob = Hybrid.EncryptBlob(BuildZip(), _key);
                var path = destination ?? Path.Combine(_backupDir, $"backup-{DateTime.Now:yyyyMMdd-HHmmss}.bdkb");

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                AtomicFile.WriteBytes(path, blob);

                if (destination is null) Prune();
                return new BackupOutcome(path, blob.Length, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                return new BackupOutcome(null, 0, ex.Message);
            }
        }
    }

    public BackupStatus Status()
    {
        var files = ListBackups();
        var last = files.LastOrDefault();

        return new BackupStatus(
            Enabled,
            _key is null ? null : Hybrid.Fingerprint(_key),
            files.Count,
            last is null ? null : File.GetLastWriteTimeUtc(last).ToString("o"),
            last is null ? 0 : new FileInfo(last).Length,
            Keep);
    }

    public string? LatestPath() => ListBackups().LastOrDefault();

    /// <summary>Faz um backup por dia, enquanto o programa estiver rodando.</summary>
    public async Task RunScheduledAsync(CancellationToken stop)
    {
        if (!Enabled) return;

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stop);

            while (!stop.IsCancellationRequested)
            {
                var latest = LatestPath();
                var due = latest is null || DateTime.UtcNow - File.GetLastWriteTimeUtc(latest) > TimeSpan.FromHours(24);

                if (due)
                {
                    var result = Create();
                    if (result.Error is null) Log.Info($"Backup cifrado criado: {Path.GetFileName(result.Path)} ({result.Bytes / 1024} KB).");
                    else Log.Warn($"Falha no backup automático: {result.Error}");
                }

                await Task.Delay(TimeSpan.FromMinutes(30), stop);
            }
        }
        catch (OperationCanceledException)
        {
            // encerrando
        }
    }

    // ------------------------------------------------------------------ internos

    private List<string> ListBackups()
    {
        try
        {
            if (!Directory.Exists(_backupDir)) return new List<string>();

            return Directory.EnumerateFiles(_backupDir, "backup-*.bdkb")
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    private void Prune()
    {
        var files = ListBackups();

        foreach (var old in files.Take(Math.Max(0, files.Count - Keep)))
        {
            try { File.Delete(old); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* fica para a próxima */ }
        }
    }

    private byte[] BuildZip()
    {
        var included = new List<string>();
        var skipped = new List<string>();

        using var memory = new MemoryStream();

        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(_dataDir).OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;

                var bytes = ReadForBackup(file, name);
                if (bytes is null)
                {
                    skipped.Add(name);
                    continue;
                }

                AddEntry(zip, name, bytes);
                included.Add(name);
            }

            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1,
                createdAt = DateTime.UtcNow.ToString("o"),
                files = included,
                skipped,
                note = "Contém segredos em texto. Copie para a pasta data/ do servidor e apague esta pasta.",
            }, new JsonSerializerOptions { WriteIndented = true });

            AddEntry(zip, "manifest.json", manifest);
        }

        return memory.ToArray();
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    private static byte[]? ReadForBackup(string path, string name)
    {
        try
        {
            if (!MachineBound.Contains(name, StringComparer.OrdinalIgnoreCase))
                return File.ReadAllBytes(path);

            var read = SecureFile.Read(path);
            if (read.Error is not null) return null;

            // Protegido por senha: o envelope já é portátil, vai como está.
            if (read.Mode == Protection.Passphrase) return File.ReadAllBytes(path);

            return read.Plaintext is null ? null : System.Text.Encoding.UTF8.GetBytes(read.Plaintext);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
