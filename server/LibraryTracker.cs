using System.Net;
using System.Text.Json;

namespace BdkHourFarm;

internal sealed record OwnedGame(uint AppId, string Name, long Minutes);

/// <summary>A biblioteca da conta (nomes e horas totais), vinda da Web API da Steam.</summary>
internal sealed class LibraryTracker
{
    private readonly object _gate = new();
    private List<OwnedGame>? _games;
    private Dictionary<uint, OwnedGame> _index = new();
    private string? _error;

    public IReadOnlyList<OwnedGame>? Games()
    {
        lock (_gate)
        {
            return _games?.ToList();
        }
    }

    public string? Error
    {
        get
        {
            lock (_gate) return _error;
        }
    }

    public string? NameOf(uint appId)
    {
        lock (_gate)
        {
            return _index.TryGetValue(appId, out var game) ? game.Name : null;
        }
    }

    /// <summary>Horas totais na Steam (em minutos), por jogo. null enquanto não houver dados.</summary>
    public IReadOnlyDictionary<uint, long>? MinutesMap()
    {
        lock (_gate)
        {
            return _games is null ? null : _games.ToDictionary(g => g.AppId, g => g.Minutes);
        }
    }

    public void SetError(string message)
    {
        lock (_gate) _error = message;
    }

    public async Task<bool> RefreshAsync(string apiKey, ulong steamId, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BdkHourFarm/1.0");

            var url = "https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/" +
                      $"?key={Uri.EscapeDataString(apiKey)}&steamid={steamId}" +
                      "&include_appinfo=true&include_played_free_games=true&format=json";

            using var response = await http.GetAsync(url, ct);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                SetError("A Steam recusou a chave. Confira se ela foi copiada inteira.");
                return false;
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("response", out var body)
                || !body.TryGetProperty("games", out var games)
                || games.ValueKind != JsonValueKind.Array)
            {
                SetError("A Steam não devolveu a biblioteca. Deixe os \"detalhes dos jogos\" do seu perfil como Público e tente de novo.");
                return false;
            }

            var list = new List<OwnedGame>();

            foreach (var game in games.EnumerateArray())
            {
                if (!game.TryGetProperty("appid", out var idElement) || !idElement.TryGetUInt32(out var appId))
                    continue;

                var name = game.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                var minutes = game.TryGetProperty("playtime_forever", out var minutesElement)
                              && minutesElement.TryGetInt64(out var value) ? value : 0L;

                list.Add(new OwnedGame(appId, string.IsNullOrWhiteSpace(name) ? $"AppID {appId}" : name, minutes));
            }

            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

            lock (_gate)
            {
                _games = list;
                _index = list.ToDictionary(g => g.AppId);
                _error = null;
            }

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false; // programa encerrando ou pedido cancelado
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            // A mensagem de HttpRequestException não inclui a URL, então a chave não vaza no log.
            // OperationCanceledException aqui é o tempo esgotado do HttpClient.
            SetError($"Não consegui falar com a Steam: {ex.Message}");
            return false;
        }
    }
}
