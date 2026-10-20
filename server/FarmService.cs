using SteamKit2;
using SteamKit2.Internal;

namespace BdkHourFarm;

/// <summary>
/// Mantém a conta "jogando" a lista de AppIDs. Quando a Steam avisa que a conta começou a
/// jogar em outro lugar, para de farmar sem brigar pela sessão; quando o outro jogo fecha,
/// espera um pouco e volta a farmar a mesma lista.
/// </summary>
internal sealed class FarmService
{
    // Falhas de login que fazem sentido tentar de novo. O resto (token expirado/revogado etc.)
    // é fatal: exige rodar "login" outra vez.
    private static readonly HashSet<EResult> RetryableLogonResults = new()
    {
        EResult.ServiceUnavailable,
        EResult.TryAnotherCM,
        EResult.Timeout,
        EResult.NoConnection,
        EResult.Busy,
        EResult.Fail,
        EResult.RateLimitExceeded,
        EResult.AccountLoginDeniedThrottle,
    };

    private static readonly TimeSpan LibraryRefreshInterval = TimeSpan.FromMinutes(10);

    private readonly FarmConfig _config;
    private readonly StatsStore _stats;
    private readonly AppInfoStore _appInfo;
    private readonly HistoryStore _history;
    private readonly GamesStore _gamesStore;
    private readonly ApiKeyStore _keys;
    private readonly Notifier _notify;
    private readonly ScheduleStore _schedule;
    private readonly LibraryTracker _library = new();
    private readonly SteamClient _client = new();
    private readonly CallbackManager _manager;
    private readonly SteamUser _user;
    private readonly SteamFriends _friends;

    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _startTimer;

    private SavedSession? _session;          // null = bloqueado (protegido por senha)
    private volatile bool _unlockRequested;
    private volatile uint[] _appIds;

    private bool _connected;
    private bool _loggedOn;
    private bool _playing;
    private bool _blocked;          // true = a conta está jogando em outro lugar
    private bool _manualPause;      // true = pausado pelo painel
    private bool _scheduleOff;      // true = fora do horário da agenda
    private DateTime _lastScheduleCheck = DateTime.MinValue;
    private DateTime _playingSince;
    private DateTime _lastStatus = DateTime.UtcNow;
    private DateTime _lastTick = DateTime.UtcNow;
    private DateTime _lastLibraryRefresh = DateTime.MinValue;
    private ulong? _steamId;

    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private int _reconnects;
    private int _pausesByOtherPc;

    private readonly object _alertGate = new();
    private DateTime? _offlineSince;
    private bool _offlineNotified;
    private bool _startNotified;
    private DateTime _lastTokenCheck = DateTime.MinValue;

    private static readonly TimeSpan OfflineAlertAfter = TimeSpan.FromMinutes(3);

    private int _reconnectAttempts;
    private TimeSpan? _nextReconnectDelay;
    private volatile bool _fatal;

    public FarmService(
        FarmConfig config,
        SavedSession? session,
        StatsStore stats,
        AppInfoStore appInfo,
        HistoryStore history,
        GamesStore gamesStore,
        ApiKeyStore keys,
        Notifier notify,
        ScheduleStore schedule)
    {
        _config = config;
        _session = session;
        _stats = stats;
        _appInfo = appInfo;
        _history = history;
        _gamesStore = gamesStore;
        _keys = keys;
        _notify = notify;
        _schedule = schedule;
        _scheduleOff = !schedule.IsActive(DateTimeOffset.UtcNow);

        // A lista escolhida pelo painel (games.json) vale mais que a do config.json.
        var chosen = (gamesStore.Load() ?? config.AppIds.ToArray())
            .Where(id => id > 0)
            .Distinct()
            .Take(FarmConfig.MaxGames)
            .ToArray();
        _appIds = chosen.Length > 0 ? chosen : config.AppIds.ToArray();

        _manager = new CallbackManager(_client);
        _user = _client.GetHandler<SteamUser>()!;
        _friends = _client.GetHandler<SteamFriends>()!;

        _manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        _manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        _manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
        _manager.Subscribe<SteamUser.PlayingSessionStateCallback>(OnPlayingSessionState);
    }

    public bool IsLocked => _session is null;

    public string? AccountName => _session?.AccountName;

    public void RequestStop() => _stop.Cancel();

    /// <returns>0 = encerrado normalmente; 2 = login inválido (rode "login" de novo).</returns>
    public int Run()
    {
        var games = _appIds;
        Log.Info($"Iniciando. Jogos configurados: {string.Join(", ", games)}");

        // Busca os nomes dos jogos em segundo plano (só o que ainda não está em cache).
        _ = Task.Run(() => _appInfo.RefreshAsync(games, _stop.Token));

        if (_session is not null)
            _client.Connect();
        else
        {
            Log.Warn("Farm BLOQUEADO: digite a senha de proteção no painel para iniciar.");
            _notify.Send("locked", "Farm bloqueado",
                "O programa iniciou, mas a sessão está protegida por senha. Abra o painel ou o app e digite a senha para começar.",
                Notifier.Amber, TimeSpan.FromHours(1));
        }

        while (!_stop.IsCancellationRequested && !_fatal)
        {
            if (_unlockRequested)
            {
                _unlockRequested = false;
                _client.Connect();
            }

            _manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(500));
            AccumulateStats();
            RefreshLibraryIfDue();
            CheckSchedule();
            CheckAlerts();
            LogStatusIfDue();
        }

        Shutdown();
        return _fatal ? 2 : 0;
    }

    // ------------------------------------------------------------ painel / API

    public FarmSnapshot GetSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var minutesMap = _library.MinutesMap();

        string state;
        string stateText;
        long? sessionSeconds = null;
        bool manualPause;
        ulong? steamId;
        var session = _session;

        lock (_gate)
        {
            manualPause = _manualPause;
            steamId = _steamId;

            if (session is null)
            {
                state = "locked";
                stateText = "Bloqueado: falta a senha de proteção";
            }
            else if (!_loggedOn)
            {
                state = "offline";
                stateText = "Sem conexão com a Steam";
            }
            else if (_blocked)
            {
                state = "paused_other";
                stateText = "Pausado: conta em uso em outro PC";
            }
            else if (_manualPause)
            {
                state = "paused_manual";
                stateText = "Pausado pelo painel";
            }
            else if (_scheduleOff)
            {
                state = "paused_schedule";
                stateText = "Fora do horário da agenda";
            }
            else if (_playing)
            {
                state = "farming";
                stateText = "Farmando";
                sessionSeconds = (long)(DateTime.UtcNow - _playingSince).TotalSeconds;
            }
            else
            {
                state = "starting";
                stateText = "Aguardando para iniciar";
            }
        }

        var games = _appIds
            .Select(id => new GameStatus(
                id,
                NameFor(id),
                _stats.GetSeconds(id),
                minutesMap is not null && minutesMap.TryGetValue(id, out var minutes) ? minutes : null))
            .ToList();

        return new FarmSnapshot(
            Account: session?.AccountName ?? "(bloqueado)",
            SteamId: steamId?.ToString(),
            State: state,
            StateText: stateText,
            ServerTime: now,
            StartedAt: _startedAt,
            UptimeSeconds: (long)(now - _startedAt).TotalSeconds,
            SessionSeconds: sessionSeconds,
            TotalFarmedSeconds: _stats.TotalSeconds(),
            TokenExpiresAt: session is null ? null : TokenInfo.GetExpiry(session.RefreshToken),
            Reconnects: _reconnects,
            PausesByOtherPc: _pausesByOtherPc,
            ManualPause: manualPause,
            SteamHoursAvailable: minutesMap is not null,
            Games: games);
    }

    private string NameFor(uint appId) => _library.NameOf(appId) ?? _appInfo.GetName(appId);

    public IReadOnlyList<SessionView> GetHistory(int days) => _history.Query(days);

    /// <summary>Desbloqueio pedido pelo painel (sessão protegida por senha).</summary>
    public void Unlock(SavedSession session)
    {
        lock (_gate)
        {
            if (_session is not null) return;
            _session = session;
        }

        Log.Info("Farm desbloqueado pelo painel.");
        _unlockRequested = true;
    }

    /// <summary>Troca o login salvo por um renovado (a conexão atual continua; o próximo login já usa o novo).</summary>
    public void ReplaceSession(SavedSession session)
    {
        lock (_gate) _session = session;
    }

    /// <summary>Pausa pedida pelo painel. Vale até "Retomar" ou até o programa reiniciar.</summary>
    public void PauseManually()
    {
        lock (_gate)
        {
            if (_manualPause) return;

            _manualPause = true;
            _startTimer?.Cancel();

            if (_playing && _loggedOn)
            {
                EndSessionLocked("painel");
                SendGamesPlayed(Array.Empty<uint>());
                _playing = false;
            }
        }

        Log.Info("Farm PAUSADO pelo painel.");
        if (_notify.IsEnabled("painel"))
            _notify.Send("pause_painel", "Farm pausado", "Pausado pelo painel ou pelo app. Toque em Retomar para voltar.", Notifier.Amber);
    }

    public void ResumeManually()
    {
        lock (_gate)
        {
            if (!_manualPause) return;
            _manualPause = false;
        }

        Log.Info("Retomada pedida pelo painel.");
        if (_notify.IsEnabled("painel"))
            _notify.Send("resume_painel", "Farm retomado", "Retomado pelo painel ou pelo app. O farm volta em instantes.", Notifier.Green);
        ScheduleStart(TimeSpan.FromSeconds(2), "retomada pelo painel");
    }

    private void CheckSchedule()
    {
        if (DateTime.UtcNow - _lastScheduleCheck < TimeSpan.FromSeconds(5)) return;
        _lastScheduleCheck = DateTime.UtcNow;
        ApplySchedule();
    }

    /// <summary>Confere a agenda agora: pausa ao sair do horário e retoma ao entrar. Também é chamada quando a agenda é editada.</summary>
    public void ApplySchedule()
    {
        var off = !_schedule.IsActive(DateTimeOffset.UtcNow);

        lock (_gate)
        {
            if (off == _scheduleOff) return;
            _scheduleOff = off;

            if (off)
            {
                _startTimer?.Cancel();

                if (_playing && _loggedOn)
                {
                    EndSessionLocked("agenda");
                    SendGamesPlayed(Array.Empty<uint>());
                    _playing = false;
                }
            }
        }

        var next = _schedule.NextChange(DateTimeOffset.UtcNow);
        var when = next is { } unix ? $" Próxima mudança: {ScheduleStore.Describe(unix, _schedule)}." : "";

        if (off)
        {
            Log.Info("Agenda: fora do horário. Farm PAUSADO.");
            if (_notify.IsEnabled("agenda"))
                _notify.Send("pause_agenda", "Farm pausado pela agenda", $"Saiu do horário permitido.{when}", Notifier.Amber);
        }
        else
        {
            Log.Info("Agenda: dentro do horário. Retomando.");
            if (_notify.IsEnabled("agenda"))
                _notify.Send("resume_agenda", "Farm retomando pela agenda", $"Entrou no horário permitido.{when}", Notifier.Green);

            ScheduleStart(TimeSpan.FromSeconds(2), "agenda");
        }
    }

    /// <summary>Troca a lista de jogos (pelo painel). Se estiver farmando, aplica na hora.</summary>
    /// <returns>null se deu certo; senão, a mensagem de erro.</returns>
    public string? SetGames(IReadOnlyList<uint> requested)
    {
        var list = requested.Where(id => id > 0).Distinct().ToArray();

        if (list.Length == 0)
            return "Escolha pelo menos um jogo.";

        if (list.Length > FarmConfig.MaxGames)
            return $"A Steam aceita no máximo {FarmConfig.MaxGames} jogos ao mesmo tempo.";

        lock (_gate)
        {
            if (list.SequenceEqual(_appIds)) return null;

            var wasPlaying = _playing && _loggedOn;

            // Fecha a contagem com a lista antiga antes de trocar.
            if (wasPlaying)
                EndSessionLocked("lista");
            else
                AccumulateStatsLocked();

            _appIds = list;

            if (wasPlaying)
            {
                SendGamesPlayed(list);
                _playingSince = DateTime.UtcNow;
                _history.Begin(DateTime.UtcNow, list);
            }
        }

        _gamesStore.Save(list);
        Log.Info($"Lista de jogos alterada pelo painel: {list.Length} jogo(s).");
        _ = Task.Run(() => _appInfo.RefreshAsync(list, _stop.Token));
        return null;
    }

    // ---- biblioteca (Web API da Steam)

    public LibraryView GetLibrary()
    {
        var games = _library.Games();

        return new LibraryView(
            Configured: !string.IsNullOrWhiteSpace(_keys.Get()),
            Available: games is not null,
            Error: _library.Error,
            Games: games ?? (IReadOnlyList<OwnedGame>)Array.Empty<OwnedGame>(),
            Selected: _appIds);
    }

    public void SetWebApiKey(string key) => _keys.Set(key);

    public async Task RefreshLibraryNowAsync(CancellationToken ct)
    {
        var key = _keys.Get();
        if (string.IsNullOrWhiteSpace(key)) return;

        ulong? steamId;
        lock (_gate) steamId = _steamId;

        if (steamId is null)
        {
            _library.SetError("Ainda não conectei à Steam. Tente de novo em alguns segundos.");
            return;
        }

        _lastLibraryRefresh = DateTime.UtcNow;
        await _library.RefreshAsync(key, steamId.Value, ct);
    }

    private void RefreshLibraryIfDue()
    {
        if (string.IsNullOrWhiteSpace(_keys.Get())) return;
        if (DateTime.UtcNow - _lastLibraryRefresh < LibraryRefreshInterval) return;

        ulong? steamId;
        lock (_gate) steamId = _steamId;
        if (steamId is null) return;

        _lastLibraryRefresh = DateTime.UtcNow;
        var key = _keys.Get()!;
        _ = Task.Run(() => _library.RefreshAsync(key, steamId.Value, _stop.Token));
    }

    // ---------------------------------------------------------------- callbacks

    private void OnConnected(SteamClient.ConnectedCallback _)
    {
        _connected = true;

        var session = _session;
        if (session is null)
        {
            _client.Disconnect();
            return;
        }

        Log.Info("Conectado à rede Steam. Fazendo login...");

        _user.LogOn(new SteamUser.LogOnDetails
        {
            Username = session.AccountName,
            AccessToken = session.RefreshToken,
            ShouldRememberPassword = true,
        });
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
    {
        if (callback.Result != EResult.OK)
        {
            HandleLogonFailure(callback.Result);
            return;
        }

        _reconnectAttempts = 0;
        lock (_gate)
        {
            _loggedOn = true;
            _playing = false;
            _steamId = _client.SteamID?.ConvertToUInt64();
        }

        Log.Info($"Login OK ({Privacy.Mask(_session?.AccountName)}).");
        _friends.SetPersonaState(_config.Persona);

        // Só começa depois de uma espera curta: se a conta já estiver jogando em outro PC,
        // a Steam manda o aviso nesse intervalo e o início é cancelado.
        ScheduleStart(TimeSpan.FromSeconds(_config.StartupWaitSeconds), "login");
    }

    private void HandleLogonFailure(EResult result)
    {
        if (RetryableLogonResults.Contains(result))
        {
            Log.Warn($"Login falhou ({result}). Vou tentar de novo.");
            if (result is EResult.RateLimitExceeded or EResult.AccountLoginDeniedThrottle)
                _nextReconnectDelay = TimeSpan.FromMinutes(5);
            return; // a Steam fecha a conexão; OnDisconnected cuida da nova tentativa
        }

        Log.Error($"Login recusado ({result}). O token provavelmente expirou ou foi revogado. " +
                  "Rode \"BdkHourFarm login\" para entrar de novo.");
        _notify.SendAndWait("fatal", "Login recusado",
            $"A Steam recusou o login ({result}). O token provavelmente expirou ou foi revogado. " +
            "Entre na VPS e rode: BdkHourFarm login. O farm está parado.", Notifier.Red);
        _fatal = true;
    }

    private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
    {
        Log.Warn($"Sessão encerrada pela Steam: {callback.Result}");

        // Se outra instância desta mesma conta entrou (ex.: o farm antigo), não insiste rápido.
        if (callback.Result == EResult.LoggedInElsewhere)
            _nextReconnectDelay = TimeSpan.FromSeconds(60);
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback callback)
    {
        _connected = false;

        lock (_gate)
        {
            EndSessionLocked("desconexao");
            _loggedOn = false;
            _playing = false;
            _blocked = false;   // o estado é reavaliado no próximo login
            _startTimer?.Cancel();
        }

        if (_stop.IsCancellationRequested || _fatal)
            return;

        _reconnects++;
        lock (_alertGate) _offlineSince ??= DateTime.UtcNow;

        var delay = _nextReconnectDelay
                    ?? TimeSpan.FromSeconds(Math.Min(5 * Math.Pow(2, _reconnectAttempts), 120));
        _nextReconnectDelay = null;
        _reconnectAttempts = Math.Min(_reconnectAttempts + 1, 10);

        Log.Warn($"Desconectado da Steam. Reconectando em {delay.TotalSeconds:0}s...");

        _ = Task.Delay(delay, _stop.Token).ContinueWith(t =>
        {
            if (t.IsCanceled || _stop.IsCancellationRequested) return;

            try
            {
                _client.Connect();
            }
            catch (Exception ex)
            {
                Log.Error($"Erro ao reconectar: {ex.Message}");
            }
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// A Steam avisa aqui quando a conta passa a jogar em outro lugar (blocked = true)
    /// e quando esse outro jogo termina (blocked = false).
    /// </summary>
    private void OnPlayingSessionState(SteamUser.PlayingSessionStateCallback callback)
    {
        bool wasBlocked;
        lock (_gate)
        {
            wasBlocked = _blocked;
            _blocked = callback.PlayingBlocked;

            if (_blocked)
            {
                _startTimer?.Cancel();
                EndSessionLocked("outro_pc");
                _playing = false; // a Steam já derrubou nossos jogos
            }
        }

        if (callback.PlayingBlocked)
        {
            if (!wasBlocked)
            {
                _pausesByOtherPc++;
                Log.Warn("Conta em uso em outro lugar. Farm PAUSADO.");
                if (_notify.IsEnabled("outro_pc"))
                    _notify.Send("pause_outro", "Farm pausado", "Você começou a jogar em outro PC. O farm volta sozinho quando você fechar o jogo.", Notifier.Amber);
            }
        }
        else if (wasBlocked)
        {
            Log.Info("O jogo no outro PC terminou.");
            if (_notify.IsEnabled("outro_pc"))
                _notify.Send("resume_outro", "Farm retomando", $"O jogo no outro PC terminou. O farm volta em {_config.ResumeDelaySeconds}s.", Notifier.Green);
            ScheduleStart(TimeSpan.FromSeconds(_config.ResumeDelaySeconds), "retomada");
        }
    }

    // ------------------------------------------------------------ iniciar/parar

    private void ScheduleStart(TimeSpan delay, string reason)
    {
        CancellationTokenSource timer;
        lock (_gate)
        {
            if (!_loggedOn || _playing || _blocked || _manualPause || _scheduleOff) return;

            _startTimer?.Cancel();
            timer = _startTimer = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        }

        Log.Info($"Farm começa em {delay.TotalSeconds:0}s ({reason})...");

        _ = Task.Delay(delay, timer.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) TryStartPlaying();
        }, TaskScheduler.Default);
    }

    private void TryStartPlaying()
    {
        uint[] games;

        lock (_gate)
        {
            // Confere de novo: o estado pode ter mudado durante a espera.
            if (!_loggedOn || _playing || _blocked || _manualPause || _scheduleOff || _stop.IsCancellationRequested) return;

            games = _appIds;
            SendGamesPlayed(games);
            _playing = true;
            _playingSince = DateTime.UtcNow;
            _lastTick = DateTime.UtcNow;
            _history.Begin(DateTime.UtcNow, games);
        }

        Log.Info($"Farmando {games.Length} jogo(s): {string.Join(", ", games)}");
        NotifyFarming(games.Length);
    }

    /// <summary>Avisa quando o farm começa pela primeira vez neste início do programa ou volta depois de uma queda longa.</summary>
    private void NotifyFarming(int count)
    {
        lock (_alertGate)
        {
            if (_offlineNotified && _offlineSince is { } since)
            {
                var away = DateTime.UtcNow - since;
                _notify.Send("back", "Farm voltou",
                    $"Voltou a farmar {count} jogo(s) depois de {FormatSpan(away)} fora.", Notifier.Green);
            }
            else if (!_startNotified)
            {
                _notify.Send("started", "Farm iniciado", $"Farmando {count} jogo(s).", Notifier.Green);
            }

            _startNotified = true;
            _offlineSince = null;
            _offlineNotified = false;
        }
    }

    private static string FormatSpan(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h{span.Minutes:D2}" : $"{Math.Max(1, (int)span.TotalMinutes)} min";

    /// <summary>Avisos que dependem de tempo: queda longa e login perto de vencer.</summary>
    private void CheckAlerts()
    {
        lock (_alertGate)
        {
            if (_offlineSince is { } since && !_offlineNotified && DateTime.UtcNow - since >= OfflineAlertAfter)
            {
                _offlineNotified = true;
                _notify.Send("offline", "Conexão com a Steam caiu",
                    $"O farm está sem conexão há {FormatSpan(DateTime.UtcNow - since)} e continua tentando reconectar.",
                    Notifier.Red, TimeSpan.FromMinutes(30));
            }
        }

        if (DateTime.UtcNow - _lastTokenCheck < TimeSpan.FromHours(6)) return;
        _lastTokenCheck = DateTime.UtcNow;

        if (_session is { } session
            && TokenInfo.GetExpiry(session.RefreshToken) is { } expiry
            && (expiry - DateTimeOffset.UtcNow).TotalDays is var days and < 14)
        {
            _notify.Send("token", "Login perto de vencer",
                $"O login da conta vence em {Math.Max(0, (int)days)} dia(s). Abra o painel ou o app e toque em Renovar login.",
                Notifier.Amber, TimeSpan.FromHours(24));
        }
    }

    private void SendGamesPlayed(IEnumerable<uint> appIds)
    {
        var message = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);

        foreach (var appId in appIds)
            message.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed { game_id = new GameID(appId) });

        _client.Send(message);
    }

    private void Shutdown()
    {
        Log.Info("Encerrando...");
        if (!_fatal && _startNotified)
            _notify.SendAndWait("stopping", "Farm parando", "O programa está sendo encerrado (reinício ou atualização).", Notifier.Amber);

        lock (_gate)
        {
            _startTimer?.Cancel();
            EndSessionLocked("encerrado");

            if (_playing && _loggedOn)
                SendGamesPlayed(Array.Empty<uint>()); // lista vazia = parar de "jogar"

            _playing = false;
        }

        _stats.Save();
        _history.Save();

        if (_loggedOn)
            _user.LogOff();

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (_connected && DateTime.UtcNow < deadline)
            _manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(200));

        _client.Disconnect();
    }

    // ------------------------------------------------------- estatísticas

    private void AccumulateStats()
    {
        lock (_gate)
        {
            AccumulateStatsLocked();
        }

        _history.Heartbeat(DateTime.UtcNow);
        _stats.SaveIfDue();
    }

    /// <summary>Soma o tempo desde a última contagem a todos os jogos, se estiver farmando. Chamar com _gate travado.</summary>
    private void AccumulateStatsLocked()
    {
        var now = DateTime.UtcNow;

        if (_playing)
        {
            var delta = now - _lastTick;

            // Ignora saltos grandes (ex.: relógio ajustado ou processo congelado).
            if (delta > TimeSpan.Zero && delta < TimeSpan.FromMinutes(5))
                _stats.Add(_appIds, delta);
        }

        _lastTick = now;
    }

    /// <summary>Fecha a contagem e a sessão do histórico, se estava farmando. Chamar com _gate travado; quem chama zera _playing.</summary>
    private void EndSessionLocked(string reason)
    {
        AccumulateStatsLocked();

        if (_playing)
            _history.End(DateTime.UtcNow, reason);
    }

    // ------------------------------------------------------------------ status

    private void LogStatusIfDue()
    {
        if (DateTime.UtcNow - _lastStatus < TimeSpan.FromMinutes(30)) return;
        _lastStatus = DateTime.UtcNow;

        var snapshot = GetSnapshot();
        var status = snapshot.State == "farming" && snapshot.SessionSeconds is { } seconds
            ? $"farmando {snapshot.Games.Count} jogo(s) há {seconds / 3600}h{seconds % 3600 / 60:D2}"
            : snapshot.StateText.ToLowerInvariant();

        Log.Info($"Status: {status}");
    }
}
