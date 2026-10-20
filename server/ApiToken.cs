using System.Security.Cryptography;

namespace BdkHourFarm;

/// <summary>Token que o painel precisa mandar para ver/controlar o farm.</summary>
internal static class ApiToken
{
    public static string LoadOrCreate(string path, string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 16) return existing;
        }

        // 18 bytes aleatórios = 24 caracteres, sem símbolos difíceis de digitar no celular.
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18))
            .Replace('+', 'x').Replace('/', 'y');

        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream))
            writer.Write(token);

        Log.Info($"Token do painel criado em: {path}");
        return token;
    }

    public static bool Matches(string? provided, string expected)
    {
        if (string.IsNullOrEmpty(provided)) return false;

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(provided),
            System.Text.Encoding.UTF8.GetBytes(expected));
    }
}
