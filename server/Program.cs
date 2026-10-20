using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Bdk.Crypto;
using BdkHourFarm;

// Pasta de dados: config.json, session.json etc. (sobrescreva com a variável BDK_DATA_DIR).
var dataDir = Environment.GetEnvironmentVariable("BDK_DATA_DIR");
if (string.IsNullOrWhiteSpace(dataDir))
    dataDir = Path.Combine(AppContext.BaseDirectory, "data");

Directory.CreateDirectory(dataDir);

var configPath = Path.Combine(dataDir, "config.json");
var sessionPath = Path.Combine(dataDir, "session.json");
var tokenPath = Path.Combine(dataDir, "api-token.txt");
var publicKeyPath = Path.Combine(dataDir, Vault.FileName);
var command = args.Length > 0 ? args[0].ToLowerInvariant() : "run";

switch (command)
{
    case "login":
        return await LoginFlow.RunAsync(sessionPath);

    case "run":
        return await RunFarmAsync();

    case "token":
        return ShowToken();

    case "protect":
        return Protect(args.Length > 1 ? args[1].ToLowerInvariant() : "");

    case "backup":
        return MakeBackup(args.Length > 1 ? args[1] : null);

    default:
        Console.WriteLine("Uso:");
        Console.WriteLine("  BdkHourFarm login                 entra na conta (uma vez) e salva a sessão criptografada");
        Console.WriteLine("  BdkHourFarm run                   inicia o farm e o painel (padrão)");
        Console.WriteLine("  BdkHourFarm token                 mostra o token de acesso do painel");
        Console.WriteLine("  BdkHourFarm protect passphrase    exige uma senha para abrir a sessão (mais seguro)");
        Console.WriteLine("  BdkHourFarm protect machine       volta a abrir sozinho (protegido só pela máquina)");
        Console.WriteLine("  BdkHourFarm backup [arquivo]      cria um backup cifrado (precisa de data/backup-public.pem)");
        return 1;
}

int ShowToken()
{
    var configured = "";
    if (File.Exists(configPath))
    {
        try { configured = FarmConfig.Load(configPath).ApiToken; }
        catch { /* usa o arquivo */ }
    }

    Console.WriteLine(ApiToken.LoadOrCreate(tokenPath, configured));
    return 0;
}

int MakeBackup(string? destination)
{
    using var key = Vault.TryLoad(publicKeyPath, out var keyError);
    if (key is null)
    {
        Console.WriteLine(keyError ?? $"Sem chave pública. Gere o par com o BdkTool e coloque {Vault.FileName} em {dataDir}.");
        return 1;
    }

    var result = new BackupService(dataDir, key).Create(destination);
    if (result.Error is not null)
    {
        Console.WriteLine("Falha no backup: " + result.Error);
        return 1;
    }

    Console.WriteLine($"Backup cifrado criado: {result.Path} ({result.Bytes / 1024} KB).");
    Console.WriteLine("Só a chave privada (no seu PC) abre este arquivo. Baixe-o com scp.");
    return 0;
}

int Protect(string target)
{
    if (target is not ("passphrase" or "machine"))
    {
        Console.WriteLine("Uso: BdkHourFarm protect passphrase   ou   BdkHourFarm protect machine");
        return 1;
    }

    var publicKey = Vault.TryLoad(publicKeyPath, out var keyError);
    if (keyError is not null) Log.Warn(keyError);

    Log.Configure(publicKey, Path.Combine(dataDir, "logs"), config.LogToConsole);
    if (publicKey is not null)
        Log.Info($"Logs e backups cifrados com a chave pública (impressão {Hybrid.Fingerprint(publicKey)}).");

    ReprotectRestoredSecrets();

    var loaded = SessionStore.Load(sessionPath);
    if (loaded is null)
    {
        Log.Error("Nenhuma sessão salva. Rode primeiro: BdkHourFarm login");
        return 2;
    }

    var session = loaded.Session;

    if (loaded.Mode == Protection.Passphrase)
    {
        Console.Write("Senha de proteção atual: ");
        session = SessionStore.Unlock(sessionPath, LoginFlow.ReadSecret());

        if (session is null)
        {
            Log.Error("Senha incorreta.");
            return 1;
        }
    }
    else if (session is null)
    {
        Log.Error(loaded.Error ?? "Não consegui ler a sessão. Rode \"login\" de novo.");
        return 2;
    }

    if (target == "machine")
    {
        var ok = SessionStore.Save(sessionPath, session, Protection.Machine);
        Log.Info(ok
            ? "Pronto: a sessão abre sozinha neste servidor (protegida pela máquina)."
            : "Não consegui criptografar; a sessão ficou em texto puro. Veja o aviso acima.");
        return ok ? 0 : 1;
    }

    Console.Write("Nova senha de proteção (mínimo 10 caracteres): ");
    var first = LoginFlow.ReadSecret();

    if (first.Length < 10)
    {
        Log.Error("Senha curta demais. Use pelo menos 10 caracteres.");
        return 1;
    }

    Console.Write("Repita a senha: ");
    if (first != LoginFlow.ReadSecret())
    {
        Log.Error("As senhas não conferem. Nada foi alterado.");
        return 1;
    }

    SessionStore.Save(sessionPath, session, Protection.Passphrase, first);
    Log.Info("Pronto. A partir de agora o farm inicia BLOQUEADO e você digita a senha no painel.");
    Log.Info("Reinicie o serviço para aplicar:  systemctl restart bdkhourfarm");
    return 0;
}

bool CheckToken(SavedSession session)
{
    if (TokenInfo.GetExpiry(session.RefreshToken) is not { } expiry)
        return true;

    var left = expiry - DateTimeOffset.UtcNow;

    if (left <= TimeSpan.Zero)
    {
        Log.Error("O token de login expirou. Rode: BdkHourFarm login");
        return false;
    }

    Log.Info($"Token de login válido por mais {(int)left.TotalDays} dia(s).");
    if (left < TimeSpan.FromDays(14))
        Log.Warn("O token expira em breve. Rode \"BdkHourFarm login\" para renovar.");

    return true;
}

async Task<int> RunFarmAsync()
{
    if (!File.Exists(configPath))
    {
        FarmConfig.WriteTemplate(configPath);
        Log.Warn($"Criei o arquivo de configuração: {configPath}");
        Log.Warn("Edite a lista de AppIds e rode de novo.");
        return 1;
    }

    FarmConfig config;
    try
    {
        config = FarmConfig.Load(configPath);
    }
    catch (Exception ex) when (ex is JsonException or InvalidDataException)
    {
        Log.Error($"config.json inválido: {ex.Message}");
        return 1;
    }

    var loaded = SessionStore.Load(sessionPath);
    if (loaded is null)
    {
        Log.Error("Nenhuma sessão salva. Rode primeiro: BdkHourFarm login");
        return 2;
    }

    if (loaded.Error is not null)
    {
        Log.Error(loaded.Error);
        return 2;
    }

    var session = loaded.Session;

    if (session is not null)
    {
        if (loaded.Mode == Protection.None)
        {
            // Sessão do formato antigo (texto puro): passa a guardar criptografada.
            try
            {
                var ok = SessionStore.Save(sessionPath, session, Protection.Machine);
                if (ok) Log.Info("A sessão agora é guardada criptografada.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Não consegui criptografar a sessão: {ex.Message}");
            }
        }

        if (!CheckToken(session))
            return 2;
    }
    else if (!config.ApiEnabled)
    {
        Log.Error("A sessão está protegida por senha e o painel está desligado (ApiEnabled). " +
                  "Ligue o painel para poder desbloquear, ou rode: BdkHourFarm protect machine");
        return 2;
    }

    var stats = new StatsStore(Path.Combine(dataDir, "stats.json"));
    var appInfo = new AppInfoStore(Path.Combine(dataDir, "appinfo.json"));
    var history = new HistoryStore(Path.Combine(dataDir, "history.json"));
    var gamesStore = new GamesStore(Path.Combine(dataDir, "games.json"));
    var keys = new ApiKeyStore(Path.Combine(dataDir, "webapikey.dat"), config.SteamWebApiKey);
    var notifier = new Notifier(Path.Combine(dataDir, "webhook.dat"), Path.Combine(dataDir, "notify.json"));
    var schedule = new ScheduleStore(Path.Combine(dataDir, "schedule.json"));
    var backups = new BackupService(dataDir, publicKey);
    var service = new FarmService(config, session, stats, appInfo, history, gamesStore, keys, notifier, schedule);

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        service.RequestStop();
    };

    // systemd e Docker param o processo com SIGTERM.
    using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
    {
        ctx.Cancel = true;
        service.RequestStop();
    });

    WebApplication? web = null;
    if (config.ApiEnabled)
    {
        try
        {
            var token = ApiToken.LoadOrCreate(tokenPath, config.ApiToken);

            bool TryUnlock(string passphrase)
            {
                var unlocked = SessionStore.Unlock(sessionPath, passphrase);
                if (unlocked is null) return false;

                CheckToken(unlocked);
                service.Unlock(unlocked);
                return true;
            }

            web = ApiServer.Build(config, service, token, TryUnlock, notifier, schedule, backups);
            await web.StartAsync();
            Log.Info($"Painel em http://{config.ApiBindAddress}:{config.ApiPort}/");
        }
        catch (Exception ex)
        {
            // O farm continua mesmo se o painel não subir (ex.: porta ocupada).
            Log.Error($"Não consegui iniciar o painel: {ex.Message}");
            web = null;
        }
    }

    using var stopBackups = new CancellationTokenSource();
    _ = Task.Run(() => backups.RunScheduledAsync(stopBackups.Token));

    // O farm roda numa thread própria para não travar o servidor web.
    var exitCode = await Task.Factory.StartNew(
        () => service.Run(), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    stopBackups.Cancel();

    if (web is not null)
    {
        await web.StopAsync(TimeSpan.FromSeconds(5));
        await web.DisposeAsync();
    }

    return exitCode;
}

// Depois de restaurar um backup, webhook/chave da Steam chegam em texto: volta a criptografar com a chave desta máquina.
void ReprotectRestoredSecrets()
{
    foreach (var name in new[] { "webhook.dat", "webapikey.dat" })
    {
        var path = Path.Combine(dataDir, name);
        if (!File.Exists(path)) continue;

        try
        {
            var read = SecureFile.Read(path);
            if (read.Mode == Protection.None && read.Error is null && !string.IsNullOrWhiteSpace(read.Plaintext))
                SecureFile.Write(path, read.Plaintext.Trim(), Protection.Machine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Não consegui proteger {name}: {ex.Message}");
        }
    }
}
