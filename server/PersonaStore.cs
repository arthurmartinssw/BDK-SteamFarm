using SteamKit2;

namespace BdkHourFarm;

internal sealed class PersonaStore
{
    public static readonly EPersonaState[] Allowed =
    {
        EPersonaState.Online, EPersonaState.Away, EPersonaState.Busy, EPersonaState.Invisible, EPersonaState.Offline,
    };

    private readonly string _path;
    private volatile int _current;

    public PersonaStore(string path, EPersonaState fallback)
    {
        _path = path;
        _current = (int)fallback;

        try
        {
            if (File.Exists(path)
                && Enum.TryParse(File.ReadAllText(path).Trim(), ignoreCase: true, out EPersonaState saved)
                && Allowed.Contains(saved))
                _current = (int)saved;
        }
        catch (IOException ex)
        {
            Log.Warn($"Não consegui ler o status salvo ({ex.Message}). Usando o do config.json.");
        }
    }

    public EPersonaState Current => (EPersonaState)_current;

    public static bool TryParse(string? text, out EPersonaState state) =>
        Enum.TryParse(text?.Trim(), ignoreCase: true, out state) && Allowed.Contains(state) && !int.TryParse(text, out _);

    public void Set(EPersonaState state)
    {
        AtomicFile.WriteText(_path, state.ToString());
        _current = (int)state;
    }

    public static string Label(EPersonaState state) => state switch
    {
        EPersonaState.Online => "Online",
        EPersonaState.Away => "Ausente",
        EPersonaState.Busy => "Ocupado",
        EPersonaState.Invisible => "Invisível",
        EPersonaState.Offline => "Offline",
        _ => state.ToString(),
    };
}
