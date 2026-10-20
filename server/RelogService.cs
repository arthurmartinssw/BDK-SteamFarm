using SteamKit2;
using SteamKit2.Authentication;

namespace BdkHourFarm;

internal sealed record RelogView(
    string State, string? Email, bool WrongCode, string? Error, bool NeedsPassphrase, string? Account, DateTimeOffset? NewExpiry);

/// <summary>
/// Renova o login da Steam pelo painel/app (sem SSH). Usuário e Steam Guard seguem o mesmo fluxo do comando "login".
/// A senha da Steam só existe na memória durante o login e nunca é gravada.
/// Estados: idle, connecting, need_device_code, need_email_code, need_confirmation, done, error.
/// </summary>
internal sealed class RelogService
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private const int MaxStartsPerWindow = 5;

    private readonly object _gate = new();
    private readonly List<DateTime> _starts = new();
    private readonly string _sessionPath;
    private readonly FarmService _farm;
    private readonly Notifier _notify;

    private string _state = "idle";
    private string? _email;
    private string? _error;
    private bool _wrongCode;
    private DateTimeOffset? _newExpiry;
    private TaskCompletionSource<string>? _codeWait;
    private CancellationTokenSource? _cts;
    private bool _cancelled;

    public RelogService(string sessionPath, FarmService farm, Notifier notify)
    {
        _sessionPath = sessionPath;
        _farm = farm;
        _notify = notify;
    }

    public RelogView View()
    {
        var mode = ReadMode();

        lock (_gate)
        {
            return new RelogView(_state, _email, _wrongCode, _error, mode == Protection.Passphrase,
                Privacy.Mask(_farm.AccountName), _newExpiry);
        }
    }

    /// <returns>Mensagem de erro, ou null se o login começou.</returns>
    public string? Start(string? password, string? passphrase)
    {
        if (string.IsNullOrEmpty(password)) return "Digite a senha da Steam.";
        if (password.Length > 256) return "Senha inválida.";

        var account = _farm.AccountName;
        if (account is null) return "Desbloqueie o farm primeiro (ele está bloqueado por senha).";

        var mode = ReadMode();
        if (mode == Protection.Passphrase)
        {
            if (string.IsNullOrEmpty(passphrase) || SessionStore.Unlock(_sessionPath, passphrase) is null)
                return "Senha de desbloqueio incorreta.";
        }

        lock (_gate)
        {
            if (_state is "connecting" or "need_device_code" or "need_email_code" or "need_confirmation")
                return "Já existe um login em andamento.";

            _starts.RemoveAll(t => DateTime.UtcNow - t > Window);
            if (_starts.Count >= MaxStartsPerWindow)
                return "Muitas tentativas. Espere alguns minutos.";
            _starts.Add(DateTime.UtcNow);

            _state = "connecting";
            _email = null;
            _error = null;
            _wrongCode = false;
            _newExpiry = null;
            _cancelled = false;
            _cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        }

        var token = _cts!.Token;
        var protection = mode == Protection.Passphrase ? Protection.Passphrase : Protection.Machine;
        _ = Task.Run(() => RunAsync(account, password, protection, passphrase, token));
        return null;
    }

    public string? SubmitCode(string? code)
    {
        code = code?.Trim().ToUpperInvariant() ?? "";
        if (code.Length is < 4 or > 12 || !code.All(char.IsLetterOrDigit))
            return "Código inválido.";

        lock (_gate)
        {
            if (_codeWait is null) return "O login não está esperando um código agora.";

            var wait = _codeWait;
            _codeWait = null;
            _state = "connecting";
            _wrongCode = false;
            wait.TrySetResult(code);
        }

        return null;
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (_cts is null || _state is "idle" or "done" or "error") return;
            _cancelled = true;
        }

        _cts?.Cancel();
    }

    // ------------------------------------------------------------------ internos

    private Protection ReadMode()
    {
        try
        {
            return File.Exists(_sessionPath) ? SecureFile.Read(_sessionPath).Mode : Protection.None;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Protection.None;
        }
    }

    private void SetState(string state, string? email = null, bool wrong = false)
    {
        lock (_gate)
        {
            _state = state;
            _email = email;
            _wrongCode = wrong;
        }
    }

    private Task<string> WaitCode(string state, string? email, bool wrong, CancellationToken token)
    {
        var wait = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(() => wait.TrySetCanceled(token));

        lock (_gate)
        {
            _state = state;
            _email = email;
            _wrongCode = wrong;
            _codeWait = wait;
        }

        return wait.Task;
    }

    private async Task RunAsync(string account, string password, Protection protection, string? passphrase, CancellationToken token)
    {
        var client = new SteamClient();
        var manager = new CallbackManager(client);
        var connected = new TaskCompletionSource();

        manager.Subscribe<SteamClient.ConnectedCallback>(_ => connected.TrySetResult());
        manager.Subscribe<SteamClient.DisconnectedCallback>(_ =>
            connected.TrySetException(new IOException("Não foi possível conectar à Steam.")));

        using var stopPump = new CancellationTokenSource();
        var pump = Task.Run(() =>
        {
            while (!stopPump.IsCancellationRequested)
                manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(250));
        });

        try
        {
            client.Connect();
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(30), token);

            var authSession = await client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
            {
                Username = account,
                Password = password,
                IsPersistentSession = true,
                Authenticator = new WebAuthenticator(this, token),
            });

            var result = await authSession.PollingWaitForResultAsync(token);

            if (!string.Equals(result.AccountName, account, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O login feito é de outra conta. Nada foi alterado.");

            var session = new SavedSession(result.AccountName, result.RefreshToken);
            SessionStore.Save(_sessionPath, session, protection, passphrase);
            _farm.ReplaceSession(session);

            var expiry = TokenInfo.GetExpiry(result.RefreshToken);
            lock (_gate)
            {
                _state = "done";
                _newExpiry = expiry;
            }

            Log.Info("Login da Steam renovado pelo painel/app.");
            _notify.Send("relogin", "Login da Steam renovado",
                expiry is { } e ? $"O novo login vale até {e.ToLocalTime():dd/MM/yyyy}." : "O novo login foi salvo.",
                Notifier.Green);
        }
        catch (OperationCanceledException)
        {
            Fail(_cancelled ? null : "Tempo esgotado. Comece de novo.", reset: _cancelled);
        }
        catch (TimeoutException)
        {
            Fail("Não consegui conectar à Steam. Tente de novo.");
        }
        catch (AuthenticationException ex)
        {
            Fail($"A Steam recusou o login ({ex.Result}). Confira a senha e tente de novo.");
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
        finally
        {
            client.Disconnect();
            stopPump.Cancel();
            await pump;
        }
    }

    private void Fail(string? message, bool reset = false)
    {
        lock (_gate)
        {
            _codeWait = null;
            _state = reset ? "idle" : "error";
            _error = reset ? null : message;
        }

        if (!reset) Log.Warn("Falha ao renovar o login: " + message);
    }

    private sealed class WebAuthenticator : IAuthenticator
    {
        private readonly RelogService _owner;
        private readonly CancellationToken _token;

        public WebAuthenticator(RelogService owner, CancellationToken token)
        {
            _owner = owner;
            _token = token;
        }

        public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) =>
            _owner.WaitCode("need_device_code", null, previousCodeWasIncorrect, _token);

        public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) =>
            _owner.WaitCode("need_email_code", email, previousCodeWasIncorrect, _token);

        public Task<bool> AcceptDeviceConfirmationAsync()
        {
            _owner.SetState("need_confirmation");
            return Task.FromResult(true);
        }
    }
}
