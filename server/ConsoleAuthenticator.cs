using SteamKit2.Authentication;

namespace BdkHourFarm;

/// <summary>Pede o código do Steam Guard no console durante o "login" interativo.</summary>
internal sealed class ConsoleAuthenticator : IAuthenticator
{
    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
    {
        if (previousCodeWasIncorrect)
            Console.WriteLine("Código incorreto. Tente de novo.");

        Console.Write("Código do Steam Guard (app do celular): ");
        return Task.FromResult(Console.ReadLine()?.Trim() ?? string.Empty);
    }

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
    {
        if (previousCodeWasIncorrect)
            Console.WriteLine("Código incorreto. Tente de novo.");

        Console.Write($"Código enviado para o e-mail {email}: ");
        return Task.FromResult(Console.ReadLine()?.Trim() ?? string.Empty);
    }

    public Task<bool> AcceptDeviceConfirmationAsync()
    {
        Console.WriteLine("Abra o app Steam no celular e confirme este login.");
        return Task.FromResult(true);
    }
}
