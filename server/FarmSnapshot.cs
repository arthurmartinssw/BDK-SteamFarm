namespace BdkHourFarm;

internal sealed record GameStatus(uint AppId, string Name, long FarmedSeconds, long? SteamMinutes);

internal sealed record FarmSnapshot(
    string Account,
    string? SteamId,
    string State,
    string StateText,
    DateTimeOffset ServerTime,
    DateTimeOffset StartedAt,
    long UptimeSeconds,
    long? SessionSeconds,
    long TotalFarmedSeconds,
    DateTimeOffset? TokenExpiresAt,
    int Reconnects,
    int PausesByOtherPc,
    bool ManualPause,
    bool SteamHoursAvailable,
    IReadOnlyList<GameStatus> Games,
    string Persona,
    string PersonaText);

internal sealed record LibraryView(
    bool Configured,
    bool Available,
    string? Error,
    IReadOnlyList<OwnedGame> Games,
    IReadOnlyList<uint> Selected);
