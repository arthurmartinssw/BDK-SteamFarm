using System.Text.Json;

namespace BdkHourFarm;

/// <summary>Lista de jogos escolhida pelo painel (games.json). Tem prioridade sobre os AppIds do config.json.</summary>
internal sealed class GamesStore
{
    private readonly string _path;

    public GamesStore(string path) => _path = path;

    public uint[]? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;

            var ids = JsonSerializer.Deserialize<uint[]>(File.ReadAllText(_path));
            return ids is { Length: > 0 } ? ids : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Log.Warn($"Não consegui ler {Path.GetFileName(_path)}: {ex.Message}");
            return null;
        }
    }

    public void Save(IEnumerable<uint> ids)
    {
        try
        {
            AtomicFile.WriteText(_path, JsonSerializer.Serialize(ids.ToArray()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Não consegui salvar {Path.GetFileName(_path)}: {ex.Message}");
        }
    }
}
