using System.Security.Cryptography;
using Bdk.Crypto;

namespace BdkHourFarm;

internal static class Vault
{
    public const string FileName = "backup-public.pem";

    public static RSA? TryLoad(string path, out string? error)
    {
        error = null;
        if (!File.Exists(path)) return null;

        try
        {
            return Hybrid.LoadPublicKey(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            error = $"{FileName}: {ex.Message} Logs e backups cifrados ficam desligados.";
            return null;
        }
    }
}
