using System.Text.Json;

namespace BdkHourFarm;

internal sealed class StatsData
{
    public Dictionary<string, double> Seconds { get; set; } = new();
}

/// <summary>Soma, por jogo, o tempo que ESTE farm ficou jogando. Salvo em stats.json.</summary>
internal sealed class StatsStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, double> _seconds = new();
    private bool _dirty;
    private DateTime _lastSave = DateTime.UtcNow;

    public StatsStore(string path)
    {
        _path = path;
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            var data = JsonSerializer.Deserialize<StatsData>(File.ReadAllText(_path));
            if (data?.Seconds is not null)
                _seconds = data.Seconds;
        }
        catch (Exception ex)
        {
            Log.Warn($"Não consegui ler {Path.GetFileName(_path)}: {ex.Message}");
        }
    }

    public void Add(IEnumerable<uint> appIds, TimeSpan delta)
    {
        var seconds = delta.TotalSeconds;
        if (seconds <= 0) return;

        lock (_gate)
        {
            foreach (var appId in appIds)
            {
                var key = appId.ToString();
                _seconds[key] = (_seconds.TryGetValue(key, out var current) ? current : 0) + seconds;
            }

            _dirty = true;
        }
    }

    public long GetSeconds(uint appId)
    {
        lock (_gate)
        {
            return _seconds.TryGetValue(appId.ToString(), out var value) ? (long)value : 0;
        }
    }

    public long TotalSeconds()
    {
        lock (_gate)
        {
            return (long)_seconds.Values.Sum();
        }
    }

    public void SaveIfDue()
    {
        lock (_gate)
        {
            if (!_dirty || DateTime.UtcNow - _lastSave < TimeSpan.FromSeconds(60))
                return;
        }

        Save();
    }

    public void Save()
    {
        string json;

        lock (_gate)
        {
            if (!_dirty) return;

            json = JsonSerializer.Serialize(new StatsData { Seconds = _seconds });
            _dirty = false;
            _lastSave = DateTime.UtcNow;
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
            lock (_gate) _dirty = true;
        }
    }
}
