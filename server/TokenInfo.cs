using System.Text.Json;

namespace BdkHourFarm;

internal static class TokenInfo
{
    /// <summary>O refresh token é um JWT; lê a data de expiração ("exp") só para avisar o usuário.</summary>
    public static DateTimeOffset? GetExpiry(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds))
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch
        {
            // Formato inesperado: só não mostra a data.
        }

        return null;
    }
}
