using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BdkHourFarm;

internal sealed class NotifyOptions
{
    public string MentionType { get; set; } = "user";
    public string? MentionId { get; set; }
    public string MentionMode { get; set; } = "off";
    public string? Username { get; set; }
    public int SummaryHour { get; set; } = -1;
    public string? LastSummaryDate { get; set; }
}

internal sealed record NotifyOptionsView(string MentionType, string? MentionId, string MentionMode, string Username, int SummaryHour);

internal sealed partial class Notifier
{
    public const int Green = 0x22C55E;
    public const int Amber = 0xF59E0B;
    public const int Red = 0xEF4444;
    public const int Blue = 0x3B82F6;

    public const string DefaultUsername = "BDK Hour Farm";

    public static readonly string[] EventKeys = { "inicio", "conexao", "painel", "outro_pc", "agenda", "login", "seguranca", "status" };

    private static readonly Dictionary<string, string> KindCategory = new()
    {
        ["started"] = "inicio",
        ["stopping"] = "inicio",
        ["offline"] = "conexao",
        ["back"] = "conexao",
        ["pause_painel"] = "painel",
        ["resume_painel"] = "painel",
        ["pause_outro"] = "outro_pc",
        ["resume_outro"] = "outro_pc",
        ["pause_agenda"] = "agenda",
        ["resume_agenda"] = "agenda",
        ["token"] = "login",
        ["relogin"] = "login",
        ["locked"] = "login",
        ["auth_fail"] = "seguranca",
        ["token_rotated"] = "seguranca",
        ["persona"] = "status",
    };

    private static readonly HashSet<string> OffByDefault = new() { "status" };

    private readonly string _path;
    private readonly string _prefsPath;
    private readonly string _optionsPath;
    private volatile string? _url;
    private Dictionary<string, bool> _prefs = new();
    private NotifyOptions _options = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _lastSent = new();

    public Notifier(string path, string prefsPath, string optionsPath)
    {
        _path = path;
        _prefsPath = prefsPath;
        _optionsPath = optionsPath;

        if (File.Exists(path))
        {
            var read = SecureFile.Read(path);
            if (read.Plaintext is { } text && IsValidUrl(text.Trim()))
                _url = text.Trim();
            else
                Log.Warn($"Não consegui ler o webhook do Discord salvo ({read.Error ?? "conteúdo inválido"}). Cole de novo no painel.");
        }

        try
        {
            if (File.Exists(prefsPath))
                _prefs = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(prefsPath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _prefs = new();
        }

        try
        {
            if (File.Exists(optionsPath))
                _options = JsonSerializer.Deserialize<NotifyOptions>(File.ReadAllText(optionsPath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _options = new();
        }

        Log.Info(_url is null ? "Avisos do Discord: desligados." : "Avisos do Discord: ligados.");

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BdkHourFarm/1.0");
    }

    public bool Configured => _url is not null;

    public static bool IsValidUrl(string url) => WebhookRegex().IsMatch(url);

    public void Set(string url)
    {
        SecureFile.Write(_path, url, Protection.Machine);
        _url = url;
        Log.Info("Avisos do Discord: webhook salvo.");
    }

    public bool IsEnabled(string key) =>
        _prefs.TryGetValue(key, out var on) ? on : !OffByDefault.Contains(key);

    public IReadOnlyDictionary<string, bool> Events() => EventKeys.ToDictionary(k => k, IsEnabled);

    public bool SetEnabled(string key, bool enabled)
    {
        if (!EventKeys.Contains(key)) return false;

        lock (_gate)
        {
            var copy = new Dictionary<string, bool>(_prefs) { [key] = enabled };
            AtomicFile.WriteText(_prefsPath, JsonSerializer.Serialize(copy));
            _prefs = copy;
        }

        return true;
    }

    public NotifyOptionsView Options()
    {
        var o = _options;
        return new NotifyOptionsView(o.MentionType, o.MentionId, o.MentionMode, o.Username ?? DefaultUsername, o.SummaryHour);
    }

    public string? SetOptions(string? mentionType, string? mentionId, string? mentionMode, string? username, int? summaryHour)
    {
        var type = (mentionType ?? "user").Trim().ToLowerInvariant();
        if (type is not ("user" or "role")) return "Tipo de menção inválido.";

        var mode = (mentionMode ?? "off").Trim().ToLowerInvariant();
        if (mode is not ("off" or "critical" or "all")) return "Quando mencionar: escolha nunca, só importantes ou sempre.";

        var id = (mentionId ?? "").Trim();
        if (id.Length > 0 && !SnowflakeRegex().IsMatch(id))
            return "O ID tem só números (17 a 20 dígitos). No Discord, ative o Modo desenvolvedor e use Copiar ID.";
        if (mode != "off" && id.Length == 0) return "Cole o ID de quem deve ser mencionado.";

        var name = (username ?? "").Trim();
        if (name.Length > 32) return "O nome do bot tem no máximo 32 caracteres.";
        if (name.Contains("discord", StringComparison.OrdinalIgnoreCase) || name.Contains("clyde", StringComparison.OrdinalIgnoreCase))
            return "O Discord não aceita nomes com \"discord\" ou \"clyde\".";

        var hour = summaryHour ?? -1;
        if (hour is < -1 or > 23) return "Hora do resumo inválida.";

        lock (_gate)
        {
            var copy = new NotifyOptions
            {
                MentionType = type,
                MentionId = id.Length == 0 ? null : id,
                MentionMode = mode,
                Username = name.Length == 0 || name == DefaultUsername ? null : name,
                SummaryHour = hour,
                LastSummaryDate = _options.LastSummaryDate,
            };
            AtomicFile.WriteText(_optionsPath, JsonSerializer.Serialize(copy));
            _options = copy;
        }

        return null;
    }

    public void Clear()
    {
        _url = null;
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }

    public void Send(string kind, string title, string text, int color, TimeSpan? cooldown = null)
    {
        if (!ShouldSend(kind, cooldown)) return;
        _ = Task.Run(() => PostAsync(title, text, color));
    }

    public void SendAndWait(string kind, string title, string text, int color)
    {
        if (!ShouldSend(kind, null)) return;

        try
        {
            PostAsync(title, text, color).Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
        }
    }

    public async Task<string?> TestAsync()
    {
        if (_url is null) return "Nenhum webhook configurado.";
        return await PostAsync("Teste de aviso", "Se você está vendo isto, os avisos do BDK Hour Farm estão funcionando.", Blue, forceMention: _options.MentionMode != "off");
    }

    public Task<string?> SendSummaryAsync(string text) =>
        _url is null ? Task.FromResult<string?>("Nenhum webhook configurado.") : PostAsync("Resumo do dia", text, Blue);

    public async Task RunSummaryAsync(ScheduleStore schedule, Func<string> compose, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var options = _options;
            if (_url is null || options.SummaryHour < 0) continue;

            var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, schedule.Zone);
            var today = local.ToString("yyyy-MM-dd");
            if (local.Hour != options.SummaryHour || options.LastSummaryDate == today) continue;

            lock (_gate)
            {
                _options.LastSummaryDate = today;
                try
                {
                    AtomicFile.WriteText(_optionsPath, JsonSerializer.Serialize(_options));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"Não consegui salvar as opções do Discord: {ex.Message}");
                }
            }

            string text;
            try
            {
                text = compose();
            }
            catch (Exception ex)
            {
                Log.Warn($"Não consegui montar o resumo do dia: {ex.Message}");
                continue;
            }

            var error = await PostAsync("Resumo do dia", text, Blue);
            if (error is not null) Log.Warn($"Resumo do dia não enviado: {error}");
        }
    }

    private bool ShouldSend(string kind, TimeSpan? cooldown)
    {
        if (_url is null) return false;
        if (KindCategory.TryGetValue(kind, out var category) && !IsEnabled(category)) return false;

        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (cooldown is { } wait && _lastSent.TryGetValue(kind, out var last) && now - last < wait)
                return false;

            _lastSent[kind] = now;
            return true;
        }
    }

    private async Task<string?> PostAsync(string title, string text, int color, bool forceMention = false)
    {
        var url = _url;
        if (url is null) return "Nenhum webhook configurado.";

        var options = _options;
        var mention = options.MentionId is not null
            && (forceMention || options.MentionMode == "all" || (options.MentionMode == "critical" && color == Red));

        try
        {
            var embeds = new[]
            {
                new { title, description = text, color, timestamp = DateTime.UtcNow.ToString("o") },
            };

            object body = mention
                ? new
                {
                    username = options.Username ?? DefaultUsername,
                    content = options.MentionType == "role" ? $"<@&{options.MentionId}>" : $"<@{options.MentionId}>",
                    allowed_mentions = options.MentionType == "role"
                        ? (object)new { roles = new[] { options.MentionId } }
                        : new { users = new[] { options.MentionId } },
                    embeds,
                }
                : new
                {
                    username = options.Username ?? DefaultUsername,
                    allowed_mentions = new { parse = Array.Empty<string>() },
                    embeds,
                };

            using var response = await _http.PostAsJsonAsync(url, body);
            if (response.IsSuccessStatusCode) return null;

            return (int)response.StatusCode is 401 or 404
                ? "O Discord não aceitou o webhook (apagado ou URL errada)."
                : $"O Discord respondeu com erro {(int)response.StatusCode}.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return "Não consegui falar com o Discord.";
        }
    }

    [GeneratedRegex(@"^https://(?:(?:canary|ptb)\.)?discord(?:app)?\.com/api/webhooks/\d+/[A-Za-z0-9_\-]+$")]
    private static partial Regex WebhookRegex();

    [GeneratedRegex(@"^\d{17,20}$")]
    private static partial Regex SnowflakeRegex();
}
