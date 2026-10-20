using System.Text;
using SteamKit2;
using SteamKit2.Authentication;

namespace BdkHourFarm;

/// <summary>
/// Login interativo (usuário, senha e Steam Guard). Roda uma vez; guarda só o refresh token.
/// </summary>
internal static class LoginFlow
{
    public static async Task<int> RunAsync(string sessionPath)
    {
        Console.Write("Usuário Steam: ");
        var username = Console.ReadLine()?.Trim();

        Console.Write("Senha (não aparece enquanto digita): ");
        var password = ReadSecret();

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            Log.Error("Usuário e senha são obrigatórios.");
            return 1;
        }

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
            Log.Info("Conectando à Steam...");
            client.Connect();
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Log.Info("Autenticando...");
            var authSession = await client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
            {
                Username = username,
                Password = password,
                IsPersistentSession = true,
                Authenticator = new ConsoleAuthenticator(),
            });

            var result = await authSession.PollingWaitForResultAsync();

            var protectedOk = SessionStore.Save(
                sessionPath, new SavedSession(result.AccountName, result.RefreshToken), Protection.Machine);

            Log.Info($"Login concluído para \"{Privacy.Mask(result.AccountName)}\". Sessão salva em: {sessionPath}");
            Log.Info(protectedOk
                ? "A sessão foi gravada criptografada (só abre neste servidor)."
                : "ATENÇÃO: não foi possível criptografar; a sessão ficou em texto puro.");
            Log.Info("Para exigir uma senha ao iniciar (mais seguro): BdkHourFarm protect passphrase");

            if (TokenInfo.GetExpiry(result.RefreshToken) is { } expiry)
                Log.Info($"O token vale até {expiry.ToLocalTime():yyyy-MM-dd}; depois disso, rode \"login\" de novo.");

            return 0;
        }
        catch (AuthenticationException ex)
        {
            Log.Error($"Falha na autenticação: {ex.Result}");
            return 1;
        }
        catch (Exception ex)
        {
            Log.Error($"Falha no login: {ex.Message}");
            return 1;
        }
        finally
        {
            client.Disconnect();
            stopPump.Cancel();
            await pump;
        }
    }

    internal static string ReadSecret()
    {
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? string.Empty;

        var secret = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
                break;

            if (key.Key == ConsoleKey.Backspace)
            {
                if (secret.Length > 0) secret.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                secret.Append(key.KeyChar);
            }
        }

        Console.WriteLine();
        return secret.ToString();
    }
}
