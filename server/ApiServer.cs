using System.Reflection;

namespace BdkHourFarm;

internal sealed record UnlockRequest(string? Passphrase);

internal sealed record GamesRequest(List<uint>? AppIds);

internal sealed record ApiKeyRequest(string? Key);

internal sealed record WebhookRequest(string? Url);

internal sealed record ReloginStartRequest(string? Password, string? Passphrase);

internal sealed record ReloginCodeRequest(string? Code);

internal sealed record NotifyEventRequest(string? Key, bool Enabled);

/// <summary>Painel web + API de monitoramento, rodando junto com o farm.</summary>
internal static class ApiServer
{
    private const string CorsPolicy = "painel";

    // O painel é um arquivo só (scripts e estilos inline); as capas dos jogos vêm do CDN da Steam.
    private const string PanelCsp =
        "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
        "img-src 'self' data: https://*.steamstatic.com; connect-src 'self'; " +
        "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    /// <summary>O token atual (pode ser trocado pelo painel sem reiniciar).</summary>
    private sealed class TokenBox
    {
        public volatile string Value = "";
    }

    /// <summary>Conta tokens inválidos recentes para avisar no Discord quando parecer um ataque.</summary>
    private sealed class AuthWatch
    {
        private readonly object _gate = new();
        private readonly Queue<DateTime> _failures = new();

        public int Record()
        {
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                _failures.Enqueue(now);
                while (_failures.Count > 0 && now - _failures.Peek() > TimeSpan.FromMinutes(10))
                    _failures.Dequeue();
                return _failures.Count;
            }
        }
    }

    /// <summary>Limita tentativas de senha no desbloqueio: 5 erros seguidos travam por 5 minutos.</summary>
    private sealed class AttemptLimiter
    {
        private readonly object _gate = new();
        private int _failures;
        private DateTime _blockedUntil = DateTime.MinValue;

        public bool IsBlocked(out int secondsLeft)
        {
            lock (_gate)
            {
                var left = (_blockedUntil - DateTime.UtcNow).TotalSeconds;
                secondsLeft = (int)Math.Ceiling(Math.Max(0, left));
                return left > 0;
            }
        }

        public void Fail()
        {
            lock (_gate)
            {
                if (++_failures < 5) return;

                _failures = 0;
                _blockedUntil = DateTime.UtcNow.AddMinutes(5);
            }
        }

        public void Reset()
        {
            lock (_gate) _failures = 0;
        }
    }

    public static WebApplication Build(FarmConfig config, FarmService farm, string token, Func<string, bool> tryUnlock, Notifier notifier, ScheduleStore schedule, BackupService backups, RelogService relog, string tokenPath)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });

        // Sem o log detalhado do ASP.NET; só avisos e erros.
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.WebHost.UseUrls($"http://{config.ApiBindAddress}:{config.ApiPort}");

        var origin = config.ApiAllowedOrigin.Trim().TrimEnd('/');
        if (origin.Length > 0)
        {
            builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p => p
                .WithOrigins(origin)
                .WithHeaders("Authorization", "Content-Type")
                .WithMethods("GET", "POST")));
        }

        var app = builder.Build();

        // Se o host web receber o sinal de parada, para o farm também.
        app.Lifetime.ApplicationStopping.Register(farm.RequestStop);

        if (origin.Length > 0)
            app.UseCors(CorsPolicy);

        var limiter = new AttemptLimiter();
        var tokenBox = new TokenBox { Value = token };
        var authWatch = new AuthWatch();

        // Exige o token em tudo que começa com /api/, menos /api/health.
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;

            if (path.StartsWithSegments("/api")
                && !path.StartsWithSegments("/api/health")
                && !HttpMethods.IsOptions(context.Request.Method))
            {
                var header = context.Request.Headers.Authorization.ToString();
                var provided = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? header["Bearer ".Length..].Trim()
                    : null;

                if (!ApiToken.Matches(provided, tokenBox.Value))
                {
                    var recent = authWatch.Record();
                    if (recent >= 10)
                    {
                        var ip = context.Request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim();
                        if (ip.Length == 0) ip = context.Connection.RemoteIpAddress?.ToString() ?? "?";
                        notifier.Send("auth_fail", "Tentativas recusadas no painel",
                            $"{recent} acessos com token inválido nos últimos 10 min (último IP: {ip}). Se não foi você, troque o token no painel.",
                            Notifier.Red, TimeSpan.FromHours(1));
                    }

                    await Task.Delay(500); // atrasa tentativas de adivinhar o token
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { error = "Token inválido." });
                    return;
                }
            }

            var headers = context.Response.Headers;
            headers.CacheControl = "no-store";
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers["X-Frame-Options"] = "DENY";

            await next();
        });

        var html = LoadPanelHtml();
        app.MapGet("/", (HttpContext context) =>
        {
            context.Response.Headers.ContentSecurityPolicy = PanelCsp;
            return Results.Content(html, "text/html; charset=utf-8");
        });

        app.MapGet("/api/health", () => Results.Ok(new { ok = true }));
        app.MapGet("/api/status", () => Results.Ok(farm.GetSnapshot()));
        app.MapGet("/api/logs", (int? lines) => Results.Ok(new { lines = Log.Recent(lines ?? 100) }));

        app.MapGet("/api/history", (int? days) => Results.Ok(new
        {
            now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            sessions = farm.GetHistory(days ?? 30),
        }));

        app.MapPost("/api/pause", () =>
        {
            farm.PauseManually();
            return Results.Ok(farm.GetSnapshot());
        });

        app.MapPost("/api/resume", () =>
        {
            farm.ResumeManually();
            return Results.Ok(farm.GetSnapshot());
        });

        // ---- jogos e biblioteca

        app.MapGet("/api/library", () => Results.Ok(farm.GetLibrary()));

        app.MapPost("/api/library/refresh", RefreshLibraryAsync);
        app.MapPost("/api/webapikey", SetKeyAsync);
        app.MapPost("/api/games", SetGames);

        // ---- desbloqueio (sessão protegida por senha)

        app.MapPost("/api/unlock", UnlockAsync);

        // ---- agenda de horários

        app.MapGet("/api/schedule", () => Results.Ok(schedule.View(DateTimeOffset.UtcNow)));

        app.MapPost("/api/schedule", (ScheduleConfig body) =>
        {
            var error = schedule.Save(body);
            if (error is not null) return Results.BadRequest(new { error });

            farm.ApplySchedule();
            return Results.Ok(schedule.View(DateTimeOffset.UtcNow));
        });

        // ---- avisos no Discord

        app.MapGet("/api/notify", () => Results.Ok(new { configured = notifier.Configured, events = notifier.Events() }));

        app.MapPost("/api/notify/events", (NotifyEventRequest body) =>
            notifier.SetEnabled(body.Key ?? "", body.Enabled)
                ? Results.Ok(new { configured = notifier.Configured, events = notifier.Events() })
                : Results.BadRequest(new { error = "Tipo de aviso desconhecido." }));

        app.MapPost("/api/notify/webhook", (WebhookRequest body) =>
        {
            var url = body.Url?.Trim() ?? "";

            if (url.Length == 0)
            {
                notifier.Clear();
                return Results.Ok(new { configured = false });
            }

            if (!Notifier.IsValidUrl(url))
                return Results.BadRequest(new { error = "Isso não parece um webhook do Discord (https://discord.com/api/webhooks/…)." });

            notifier.Set(url);
            return Results.Ok(new { configured = true });
        });

        app.MapPost("/api/notify/test", async () =>
        {
            var error = await notifier.TestAsync();
            return error is null
                ? Results.Ok(new { ok = true })
                : Results.Json(new { error }, statusCode: 502);
        });

        // ---- trocar o token do painel (o antigo para de valer na hora)

        app.MapPost("/api/token/rotate", () =>
        {
            if (!string.IsNullOrWhiteSpace(config.ApiToken))
                return Results.BadRequest(new { error = "O token está fixo no config.json (ApiToken). Apague-o de lá para poder trocar pelo painel." });

            var fresh = ApiToken.Generate();
            try
            {
                ApiToken.Save(tokenPath, fresh);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Results.BadRequest(new { error = "Não consegui gravar o novo token: " + ex.Message });
            }

            tokenBox.Value = fresh;
            Log.Info("Token do painel trocado.");
            notifier.Send("token_rotated", "Token do painel trocado",
                "O token de acesso foi trocado pelo painel ou app. Os outros aparelhos precisam do novo token.", Notifier.Amber);
            return Results.Ok(new { token = fresh });
        });

        // ---- renovar o login da Steam (a senha só passa pela memória, nunca é gravada)

        app.MapGet("/api/relogin", () => Results.Ok(relog.View()));

        app.MapPost("/api/relogin/start", (ReloginStartRequest body) =>
        {
            var error = relog.Start(body.Password, body.Passphrase);
            return error is null ? Results.Ok(relog.View()) : Results.BadRequest(new { error });
        });

        app.MapPost("/api/relogin/code", (ReloginCodeRequest body) =>
        {
            var error = relog.SubmitCode(body.Code);
            return error is null ? Results.Ok(relog.View()) : Results.BadRequest(new { error });
        });

        app.MapPost("/api/relogin/cancel", () =>
        {
            relog.Cancel();
            return Results.Ok(relog.View());
        });

        // ---- backup cifrado (a chave pública só pode ser trocada por arquivo, via SSH: nunca pela API)

        app.MapGet("/api/backup", () => Results.Ok(backups.Status()));

        app.MapPost("/api/backup/run", () =>
        {
            var result = backups.Create();
            return result.Error is null
                ? Results.Ok(backups.Status())
                : Results.BadRequest(new { error = result.Error });
        });

        app.MapPost("/api/backup/prune", () =>
        {
            var removed = backups.Prune();
            return Results.Ok(new { removed, status = backups.Status() });
        });

        app.MapGet("/api/backup/download", () =>
        {
            var path = backups.LatestPath();
            if (path is null || !File.Exists(path))
                return Results.NotFound(new { error = "Ainda não há backup." });

            return Results.File(File.ReadAllBytes(path), "application/octet-stream", Path.GetFileName(path));
        });

        return app;

        async Task<IResult> RefreshLibraryAsync(CancellationToken requestAborted)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            await farm.RefreshLibraryNowAsync(cts.Token);
            return Results.Ok(farm.GetLibrary());
        }

        async Task<IResult> SetKeyAsync(ApiKeyRequest body)
        {
            var key = body.Key?.Trim() ?? "";

            if (!ApiKeyStore.IsValidFormat(key))
                return Results.BadRequest(new { error = "A chave da Steam tem 32 letras e números. Copie de novo." });

            farm.SetWebApiKey(key);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await farm.RefreshLibraryNowAsync(cts.Token);
            return Results.Ok(farm.GetLibrary());
        }

        IResult SetGames(GamesRequest body)
        {
            var error = farm.SetGames(body.AppIds ?? new List<uint>());

            return error is null
                ? Results.Ok(farm.GetSnapshot())
                : Results.BadRequest(new { error });
        }

        async Task<IResult> UnlockAsync(UnlockRequest body)
        {
            if (!farm.IsLocked)
                return Results.Ok(farm.GetSnapshot());

            if (limiter.IsBlocked(out var secondsLeft))
                return Results.Json(new { error = $"Muitas tentativas. Espere {secondsLeft} s." }, statusCode: 429);

            var passphrase = body.Passphrase ?? "";
            var ok = passphrase.Length > 0 && await Task.Run(() => tryUnlock(passphrase));

            if (!ok)
            {
                limiter.Fail();
                await Task.Delay(1500);
                return Results.Json(new { error = "Senha incorreta." }, statusCode: 403);
            }

            limiter.Reset();
            return Results.Ok(farm.GetSnapshot());
        }
    }

    private static string LoadPanelHtml()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html");
        if (stream is null) return "<h1>Painel não encontrado no executável.</h1>";

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
