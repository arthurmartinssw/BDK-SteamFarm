using System.Text.Json;

namespace BdkHourFarm;

internal sealed class SessionRecord
{
    public long Start { get; set; }
    public long End { get; set; }
    public long LastSeen { get; set; }
    public string Reason { get; set; } = "";
    public uint[] Games { get; set; } = Array.Empty<uint>();
}

internal sealed class HistoryData
{
    public List<SessionRecord> Sessions { get; set; } = new();
    public SessionRecord? Open { get; set; }
}

/// <summary>Uma sessão de farm, como o painel recebe. End = null significa "em andamento".</summary>
internal sealed record SessionView(long Start, long? End, string Reason, uint[] Games);

/// <summary>
/// Histórico de sessões. Uma sessão começa quando o farm passa a "jogar" e termina quando para
/// (outro PC, pausa pelo painel, queda de conexão, lista alterada ou programa encerrado).
/// </summary>
internal sealed class HistoryStore
{
    private const int MaxSessions = 1500;
    private const long MinSeconds = 30;
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(60);

    private readonly string _path;
    private readonly object _gate = new();
    private HistoryData _data = new();
    private bool _dirty;
    private DateTime _lastSave = DateTime.UtcNow;

    public HistoryStore(string path)
    {
        _path = path;
        Load();
        RecoverOpenSession();
    }

    private static long Unix(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            _data = JsonSerializer.Deserialize<HistoryData>(File.ReadAllText(_path)) ?? new HistoryData();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Log.Warn($"Não consegui ler {Path.GetFileName(_path)}: {ex.Message}");
            _data = new HistoryData();
        }
    }

    /// <summary>Se o programa caiu no meio de uma sessão (servidor desligado, por exemplo), fecha-a no último sinal de vida.</summary>
    private void RecoverOpenSession()
    {
        lock (_gate)
        {
            if (_data.Open is not { } open) return;

            var end = Math.Max(open.Start, open.LastSeen);
            if (end - open.Start >= MinSeconds)
            {
                open.End = end;
                open.Reason = "queda";
                _data.Sessions.Add(open);
            }

            _data.Open = null;
            _dirty = true;
        }

        Save();
    }

    public void Begin(DateTime utc, IEnumerable<uint> games)
    {
        lock (_gate)
        {
            var now = Unix(utc);
            _data.Open = new SessionRecord { Start = now, LastSeen = now, Games = games.ToArray() };
            _dirty = true;
        }

        Save();
    }

    public void End(DateTime utc, string reason)
    {
        lock (_gate)
        {
            if (_data.Open is not { } open) return;

            var now = Unix(utc);
            _data.Open = null;
            _dirty = true;

            // Sessões muito curtas (ligar e parar na hora) só fazem barulho.
            if (now - open.Start >= MinSeconds)
            {
                open.End = now;
                open.Reason = reason;
                _data.Sessions.Add(open);

                if (_data.Sessions.Count > MaxSessions)
                    _data.Sessions.RemoveRange(0, _data.Sessions.Count - MaxSessions);
            }
        }

        Save();
    }

    /// <summary>Marca que o farm continua vivo (usado para fechar a sessão certa se o servidor cair).</summary>
    public void Heartbeat(DateTime utc)
    {
        lock (_gate)
        {
            if (_data.Open is null) return;

            _data.Open.LastSeen = Unix(utc);
            _dirty = true;
        }

        SaveIfDue();
    }

    public void SaveIfDue()
    {
        lock (_gate)
        {
            if (!_dirty || DateTime.UtcNow - _lastSave < SaveInterval) return;
        }

        Save();
    }

    public void Save()
    {
        string json;

        lock (_gate)
        {
            if (!_dirty) return;

            json = JsonSerializer.Serialize(_data);
            _dirty = false;
            _lastSave = DateTime.UtcNow;
        }

        try
        {
            AtomicFile.WriteText(_path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Não consegui salvar {Path.GetFileName(_path)}: {ex.Message}");
            lock (_gate) _dirty = true;
        }
    }

    /// <summary>Sessões que tocam os últimos N dias, da mais antiga para a mais recente. A em andamento vem por último.</summary>
    public IReadOnlyList<SessionView> Query(int days)
    {
        days = Math.Clamp(days, 1, 365);
        var since = Unix(DateTime.UtcNow.AddDays(-days));

        lock (_gate)
        {
            var list = _data.Sessions
                .Where(s => s.End >= since)
                .Select(s => new SessionView(s.Start, s.End, s.Reason, s.Games))
                .ToList();

            if (_data.Open is { } open)
                list.Add(new SessionView(open.Start, null, "", open.Games));

            return list;
        }
    }
}
