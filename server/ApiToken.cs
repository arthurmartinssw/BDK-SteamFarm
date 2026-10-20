using System.Security.Cryptography;

namespace BdkHourFarm;

/// <summary>Token que o painel precisa mandar para ver/controlar o farm.</summary>
internal static class ApiToken
{
    public static string LoadOrCreate(string path, string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (configured.Trim().Length < 16)
                Log.Warn("O ApiToken do config.json é curto (menos de 16 caracteres) e fácil de adivinhar. Apague-o para gerar um forte.");
            return configured.Trim();
        }

        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 16) return existing;
        }

        var token = Generate();
        Save(path, token);
        Log.Info($"Token do painel criado em: {path}");
        return token;
    }

    /// <summary>18 bytes aleatórios = 24 caracteres, sem símbolos difíceis de digitar no celular.</summary>
    public static string Generate() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', 'x').Replace('/', 'y');

    public static void Save(string path, string token)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream);
        writer.Write(token);
    }

    public static bool Matches(string? provided, string expected)
    {
        if (string.IsNullOrEmpty(provided)) return false;

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(provided),
            System.Text.Encoding.UTF8.GetBytes(expected));
    }
}
