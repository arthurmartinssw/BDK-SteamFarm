using System.Globalization;
using System.Net;
using System.Text.Json;

namespace BdkHourFarm;

internal sealed record AchievementItem(
    string Id, string Name, string Description, bool Hidden, string? Icon, string? IconGray,
    bool Achieved, long UnlockTime, double? Percent);

internal sealed record AchievementList(
    uint AppId, string Name, int Total, int Unlocked, IReadOnlyList<AchievementItem> Items, string? Error);

internal sealed class AchievementsService
{
    private static readonly TimeSpan PlayerTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SchemaTtl = TimeSpan.FromHours(24);

    private sealed record SchemaAchievement(string Id, string Name, string Description, bool Hidden, string? Icon, string? IconGray);

    private readonly FarmService _farm;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private readonly Dictionary<uint, (DateTime At, List<SchemaAchievement> Items)> _schemas = new();
    private readonly Dictionary<uint, (DateTime At, Dictionary<string, double> Map)> _percents = new();
    private readonly Dictionary<uint, (DateTime At, AchievementList Result)> _lists = new();
    private readonly SemaphoreSlim _limit = new(3);

    public AchievementsService(FarmService farm)
    {
        _farm = farm;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BdkHourFarm/1.0");
    }

    public async Task<AchievementList> GetAsync(uint appId, bool refresh, CancellationToken ct)
    {
        var name = _farm.GameName(appId);

        if (!refresh)
        {
            lock (_gate)
            {
                if (_lists.TryGetValue(appId, out var cached) && DateTime.UtcNow - cached.At < PlayerTtl)
                    return cached.Result;
            }
        }

        var key = _farm.WebApiKey;
        if (string.IsNullOrWhiteSpace(key))
            return Failure(appId, name, "Cole a chave da Web API da Steam na tela \"Escolher jogos\" para ver as conquistas.");

        var steamId = _farm.SteamId;
        if (steamId is null)
            return Failure(appId, name, "O farm ainda não conectou à Steam. Tente em instantes.");

        await _limit.WaitAsync(ct);
        try
        {
            var schema = await GetSchemaAsync(appId, key, ct);
            if (schema.Error is not null) return Failure(appId, name, schema.Error);

            if (schema.Items!.Count == 0)
            {
                var empty = new AchievementList(appId, name, 0, 0, Array.Empty<AchievementItem>(), "Este jogo não tem conquistas.");
                Store(appId, empty);
                return empty;
            }

            var player = await GetPlayerAsync(appId, key, steamId.Value, ct);
            if (player.Error is not null) return Failure(appId, name, player.Error);

            var percents = await GetPercentsAsync(appId, ct);

            var items = schema.Items
                .Select(a =>
                {
                    player.Map!.TryGetValue(a.Id, out var state);
                    double? percent = percents is not null && percents.TryGetValue(a.Id, out var p) ? p : null;
                    return new AchievementItem(a.Id, a.Name, a.Description, a.Hidden, a.Icon, a.IconGray,
                        state.Achieved, state.Time, percent);
                })
                .ToList();

            var result = new AchievementList(appId, name, items.Count, items.Count(i => i.Achieved), items, null);
            Store(appId, result);
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            return Failure(appId, name, "Não consegui falar com a Steam agora. Tente de novo em instantes.");
        }
        finally
        {
            _limit.Release();
        }
    }

    private static AchievementList Failure(uint appId, string name, string error) =>
        new(appId, name, 0, 0, Array.Empty<AchievementItem>(), error);

    private void Store(uint appId, AchievementList result)
    {
        lock (_gate) _lists[appId] = (DateTime.UtcNow, result);
    }

    private async Task<(List<SchemaAchievement>? Items, string? Error)> GetSchemaAsync(uint appId, string key, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_schemas.TryGetValue(appId, out var cached) && DateTime.UtcNow - cached.At < SchemaTtl)
                return (cached.Items, null);
        }

        var url = "https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/" +
                  $"?key={Uri.EscapeDataString(key)}&appid={appId}&l=brazilian&format=json";

        using var response = await _http.GetAsync(url, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return (null, "A Steam recusou a chave da Web API. Confira se ela foi copiada inteira.");

        if (!response.IsSuccessStatusCode)
            return (null, "A Steam não devolveu as conquistas deste jogo.");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var items = new List<SchemaAchievement>();

        if (doc.RootElement.TryGetProperty("game", out var game)
            && game.TryGetProperty("availableGameStats", out var stats)
            && stats.TryGetProperty("achievements", out var list)
            && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in list.EnumerateArray())
            {
                var id = Text(a, "name");
                if (id.Length == 0) continue;

                var display = Text(a, "displayName");
                items.Add(new SchemaAchievement(
                    id,
                    display.Length > 0 ? display : id,
                    Text(a, "description"),
                    a.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.Number && h.GetInt32() == 1,
                    SafeIcon(Text(a, "icon")),
                    SafeIcon(Text(a, "icongray"))));
            }
        }

        lock (_gate) _schemas[appId] = (DateTime.UtcNow, items);
        return (items, null);
    }

    private async Task<(Dictionary<string, (bool Achieved, long Time)>? Map, string? Error)> GetPlayerAsync(
        uint appId, string key, ulong steamId, CancellationToken ct)
    {
        var url = "https://api.steampowered.com/ISteamUserStats/GetPlayerAchievements/v1/" +
                  $"?key={Uri.EscapeDataString(key)}&steamid={steamId}&appid={appId}&format=json";

        using var response = await _http.GetAsync(url, ct);

        if (response.StatusCode == HttpStatusCode.Forbidden)
            return (null, "A Steam bloqueou a leitura. Deixe os \"detalhes dos jogos\" do seu perfil como Público e tente de novo.");

        if (response.StatusCode == HttpStatusCode.BadRequest)
            return (null, "Este jogo não tem conquistas (ou você ainda não o jogou).");

        if (!response.IsSuccessStatusCode)
            return (null, "A Steam não devolveu o seu progresso neste jogo.");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        if (!doc.RootElement.TryGetProperty("playerstats", out var stats))
            return (null, "Resposta inesperada da Steam.");

        if (stats.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
            return (null, "A Steam não deixou ler as conquistas. Deixe os \"detalhes dos jogos\" do seu perfil como Público.");

        var map = new Dictionary<string, (bool, long)>();
        if (stats.TryGetProperty("achievements", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in list.EnumerateArray())
            {
                var id = Text(a, "apiname");
                if (id.Length == 0) continue;

                var achieved = a.TryGetProperty("achieved", out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() == 1;
                var time = a.TryGetProperty("unlocktime", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0;
                map[id] = (achieved, time);
            }
        }

        return (map, null);
    }

    private async Task<Dictionary<string, double>?> GetPercentsAsync(uint appId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_percents.TryGetValue(appId, out var cached) && DateTime.UtcNow - cached.At < SchemaTtl)
                return cached.Map;
        }

        try
        {
            var url = "https://api.steampowered.com/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2/" +
                      $"?gameid={appId}&format=json";

            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var map = new Dictionary<string, double>();

            if (doc.RootElement.TryGetProperty("achievementpercentages", out var root)
                && root.TryGetProperty("achievements", out var list)
                && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in list.EnumerateArray())
                {
                    var id = Text(a, "name");
                    if (id.Length == 0 || !a.TryGetProperty("percent", out var p)) continue;

                    double value;
                    if (p.ValueKind == JsonValueKind.Number) value = p.GetDouble();
                    else if (!double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) continue;

                    map[id] = Math.Round(value, 1);
                }
            }

            lock (_gate) _percents[appId] = (DateTime.UtcNow, map);
            return map;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            return null;
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string? SafeIcon(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps) return null;
        return uri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase) ? url : null;
    }
}
