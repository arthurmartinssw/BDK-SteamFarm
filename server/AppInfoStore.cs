using System.Text.Json;

namespace BdkHourFarm;

/// <summary>Nomes dos jogos (buscados na loja da Steam uma única vez e guardados em appinfo.json).</summary>
internal sealed class AppInfoStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, string> _names = new();

    public AppInfoStore(string path)
    {
        _path = path;

        try
        {
            if (File.Exists(path))
                _names = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Warn($"Não consegui ler {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    public string GetName(uint appId)
    {
        lock (_gate)
        {
            return _names.TryGetValue(appId.ToString(), out var name) ? name : $"AppID {appId}";
        }
    }

    public async Task RefreshAsync(IEnumerable<uint> appIds, CancellationToken ct)
    {
        List<uint> missing;
        lock (_gate)
        {
            missing = appIds.Where(id => !_names.ContainsKey(id.ToString())).ToList();
        }

        if (missing.Count == 0) return;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BdkHourFarm/1.0");

        foreach (var appId in missing)
        {
            if (ct.IsCancellationRequested) return;

            try
            {
                var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic";
                var json = await http.GetStringAsync(url, ct);

                using var doc = JsonDocument.Parse(json);
                var name = $"AppID {appId}";

                if (doc.RootElement.TryGetProperty(appId.ToString(), out var entry)
                    && entry.TryGetProperty("success", out var success)
                    && success.ValueKind == JsonValueKind.True
                    && entry.TryGetProperty("data", out var data)
                    && data.TryGetProperty("name", out var nameElement)
                    && nameElement.GetString() is { Length: > 0 } found)
                {
                    name = found;
                }

                lock (_gate) _names[appId.ToString()] = name;
                Save();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"Não consegui buscar o nome do AppID {appId}: {ex.Message}");
            }

            try
            {
                await Task.Delay(1500, ct); // a loja limita o ritmo das consultas
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Save()
    {
        string json;
        lock (_gate)
        {
            json = JsonSerializer.Serialize(_names);
        }

        try
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Não consegui salvar {Path.GetFileName(_path)}: {ex.Message}");
        }
    }
}
