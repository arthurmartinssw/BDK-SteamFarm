using System.Net;
using System.Text.Json;
using SteamKit2;

namespace BdkHourFarm;

internal sealed class FarmConfig
{
    public const int MaxGames = 32;

    public List<uint> AppIds { get; set; } = new();

    public int StartupWaitSeconds { get; set; } = 8;

    public int ResumeDelaySeconds { get; set; } = 20;

    public string PersonaState { get; set; } = "Online";

    public bool ApiEnabled { get; set; } = true;

    public int ApiPort { get; set; } = 8080;

    public string ApiBindAddress { get; set; } = "127.0.0.1";

    public string ApiToken { get; set; } = "";

    public string ApiAllowedOrigin { get; set; } = "";

    public string SteamWebApiKey { get; set; } = "";

    public bool LogToConsole { get; set; } = true;

    public int BackupKeepDays { get; set; } = 7;

    public string AiModel { get; set; } = "claude-haiku-5-5";

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

        if (!System.Text.RegularExpressions.Regex.IsMatch(AiModel ?? "", "^[a-z0-9.\\-]{3,60}$"))
            AiModel = "claude-haiku-5-5";

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
              "BackupKeepDays": 7,

              // Modelo do Claude usado na ajuda com conquistas (precisa da chave da Anthropic, colada no painel).
              "AiModel": "claude-haiku-5-5"
            }
            """;

        AtomicFile.WriteText(path, template);
    }
}
