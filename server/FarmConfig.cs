using System.Net;
using System.Text.Json;
using SteamKit2;

namespace BdkHourFarm;

internal sealed class FarmConfig
{
    /// <summary>Limite de jogos simultâneos que a Steam aceita num único "ClientGamesPlayed".</summary>
    public const int MaxGames = 32;

    public List<uint> AppIds { get; set; } = new();

    /// <summary>Espera após o login antes de começar, para a Steam avisar se já há jogo em outro PC.</summary>
    public int StartupWaitSeconds { get; set; } = 8;

    /// <summary>Espera antes de retomar depois que a Steam avisa que o outro PC parou de jogar.</summary>
    public int ResumeDelaySeconds { get; set; } = 20;

    /// <summary>Online, Invisible, Offline, Away, Busy ou Snooze.</summary>
    public string PersonaState { get; set; } = "Online";

    // ---- Painel / API ----

    /// <summary>Liga o painel web e a API de monitoramento.</summary>
    public bool ApiEnabled { get; set; } = true;

    public int ApiPort { get; set; } = 8080;

    /// <summary>127.0.0.1 = só a própria VPS (o Caddy faz o HTTPS na frente). 0.0.0.0 = todas as interfaces.</summary>
    public string ApiBindAddress { get; set; } = "127.0.0.1";

    /// <summary>Se vazio, um token aleatório é criado em data/api-token.txt.</summary>
    public string ApiToken { get; set; } = "";

    /// <summary>Opcional: origem permitida via CORS (ex.: https://meu-painel.vercel.app).</summary>
    public string ApiAllowedOrigin { get; set; } = "";

    /// <summary>Opcional: chave da Web API da Steam (também dá para colar pelo painel, na tela "Escolher jogos").</summary>
    public string SteamWebApiKey { get; set; } = "";

    /// <summary>
    /// Com a chave pública de backup instalada, os logs também vão cifrados para data/logs.
    /// Coloque false para o console/journald não guardar nada em texto puro.
    /// </summary>
    public bool LogToConsole { get; set; } = true;

    /// <summary>Backups mais velhos que isso (em dias) são apagados da VPS. Mínimo de 7.</summary>
    public int BackupKeepDays { get; set; } = 7;

    [System.Text.Json.Serialization.JsonIgnore]
    public EPersonaState Persona { get; private set; } = EPersonaState.Online;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    public static FarmConfig Load(string path)
    {
        var config = JsonSerializer.Deserialize<FarmConfig>(File.ReadAllText(path), JsonOptions)
                     ?? throw new InvalidDataException("config.json está vazio.");
        config.Validate();
        return config;
    }

    private void Validate()
    {
        AppIds = AppIds.Where(id => id > 0).Distinct().ToList();

        if (AppIds.Count == 0)
            throw new InvalidDataException("Coloque pelo menos um AppID em \"AppIds\".");

        if (AppIds.Count > MaxGames)
        {
            Log.Warn($"A Steam aceita no máximo {MaxGames} jogos ao mesmo tempo; usando só os {MaxGames} primeiros.");
            AppIds = AppIds.Take(MaxGames).ToList();
        }

        StartupWaitSeconds = Math.Clamp(StartupWaitSeconds, 3, 300);
        ResumeDelaySeconds = Math.Clamp(ResumeDelaySeconds, 5, 3600);

        if (!Enum.TryParse(PersonaState, ignoreCase: true, out EPersonaState persona))
            throw new InvalidDataException(
                $"PersonaState inválido: \"{PersonaState}\". Use Online, Invisible, Offline, Away, Busy ou Snooze.");

        Persona = persona;

        ApiPort = Math.Clamp(ApiPort, 1, 65535);
        BackupKeepDays = Math.Clamp(BackupKeepDays, 7, 365);

        if (!IPAddress.TryParse(ApiBindAddress, out _))
            throw new InvalidDataException($"ApiBindAddress inválido: \"{ApiBindAddress}\". Use um IP, como 127.0.0.1.");
    }

    public static void WriteTemplate(string path)
    {
        const string template = """
            {
              // AppIDs dos jogos que o farm deve manter "rodando" (máximo de 32).
              "AppIds": [730, 440, 570],

              // Segundos de espera depois do login, antes de começar a farmar.
              // Dá tempo da Steam avisar se você já está jogando em outro PC.
              "StartupWaitSeconds": 8,

              // Segundos de espera antes de voltar a farmar depois que o jogo
              // no outro PC fechou (evita retomar cedo demais e derrubar você).
              "ResumeDelaySeconds": 20,

              // Online, Invisible, Offline, Away, Busy ou Snooze.
              "PersonaState": "Online",

              // ---- Painel web / API de monitoramento ----
              "ApiEnabled": true,
              "ApiPort": 8080,
              "ApiBindAddress": "127.0.0.1",

              // Deixe vazio para gerar um token em data/api-token.txt.
              "ApiToken": "",

              // Só preencha se hospedar o painel em outro site (CORS). Ex.: "https://meu-painel.vercel.app"
              "ApiAllowedOrigin": "",

              // Opcional: chave da Web API da Steam (https://steamcommunity.com/dev/apikey).
              // Não precisa preencher aqui: dá para colar a chave pelo painel, na tela "Escolher jogos".
              "SteamWebApiKey": "",

              // Com a chave pública de backup instalada, os logs vão cifrados para data/logs.
              // false = o console/journald não guarda nada em texto puro (o painel ainda mostra os recentes).
              "LogToConsole": true,

              // Backups cifrados mais velhos que isso (em dias, mínimo 7) são apagados da VPS para não encher o disco.
              "BackupKeepDays": 7
            }
            """;

        File.WriteAllText(path, template);
    }
}
