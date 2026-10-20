using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BdkHourFarm;

/// <summary>Avisos no Discord por webhook. A URL é um segredo: fica criptografada em disco e nunca vai para o log.</summary>
internal sealed partial class Notifier
{
    public const int Green = 0x22C55E;
    public const int Amber = 0xF59E0B;
    public const int Red = 0xEF4444;
    public const int Blue = 0x3B82F6;

    /// <summary>Tipos de aviso que o usuário pode ligar/desligar (padrão: ligados).</summary>
    public static readonly string[] EventKeys = { "painel", "outro_pc", "agenda" };

    private readonly string _path;
    private readonly string _prefsPath;
    private volatile string? _url;
    private Dictionary<string, bool> _prefs = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _lastSent = new();

    public Notifier(string path, string prefsPath)
    {
        _path = path;
        _prefsPath = prefsPath;

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

        Log.Info(_url is null ? "Avisos do Discord: desligados." : "Avisos do Discord: ligados.");

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BdkHourFarm/1.0");
    }

    public bool Configured => _url is not null;

    /// <summary>Só aceita webhooks do próprio Discord (evita usar o farm para chamar outros endereços).</summary>
    public static bool IsValidUrl(string url) => WebhookRegex().IsMatch(url);

    public void Set(string url)
    {
        SecureFile.Write(_path, url, Protection.Machine);
        _url = url;
        Log.Info("Avisos do Discord: webhook salvo.");
    }

    public bool IsEnabled(string key) => !_prefs.TryGetValue(key, out var on) || on;

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

    public void Clear()
    {
        _url = null;
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (IOException)
        {
            // sem problema: o arquivo some na próxima gravação
        }
    }

    /// <summary>Envia em segundo plano. cooldown evita repetir o mesmo aviso (por tipo) em sequência.</summary>
    public void Send(string kind, string title, string text, int color, TimeSpan? cooldown = null)
    {
        if (!ShouldSend(kind, cooldown)) return;
        _ = Task.Run(() => PostAsync(title, text, color));
    }

    /// <summary>Para avisos de última hora (programa parando): espera até 5 s pelo envio.</summary>
    public void SendAndWait(string kind, string title, string text, int color)
    {
        if (!ShouldSend(kind, null)) return;

        try
        {
            PostAsync(title, text, color).Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // melhor esforço
        }
    }

    /// <returns>null se enviou; senão, o motivo da falha.</returns>
    public async Task<string?> TestAsync()
    {
        if (_url is null) return "Nenhum webhook configurado.";
        return await PostAsync("Teste de aviso", "Se você está vendo isto, os avisos do BDK Hour Farm estão funcionando.", Blue);
    }

    private bool ShouldSend(string kind, TimeSpan? cooldown)
    {
        if (_url is null) return false;

        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (cooldown is { } wait && _lastSent.TryGetValue(kind, out var last) && now - last < wait)
                return false;

            _lastSent[kind] = now;
            return true;
        }
    }

    private async Task<string?> PostAsync(string title, string text, int color)
    {
        var url = _url;
        if (url is null) return "Nenhum webhook configurado.";

        try
        {
            var body = new
            {
                username = "BDK Hour Farm",
                embeds = new[]
                {
                    new { title, description = text, color, timestamp = DateTime.UtcNow.ToString("o") },
                },
            };

            using var response = await _http.PostAsJsonAsync(url, body);
            if (response.IsSuccessStatusCode) return null;

            // A mensagem não inclui a URL, então o segredo não vaza no log.
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
}
