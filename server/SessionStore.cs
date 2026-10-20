using System.Text.Json;

namespace BdkHourFarm;

/// <summary>Sessão salva: só o usuário e o refresh token. A senha nunca é gravada.</summary>
internal sealed record SavedSession(string AccountName, string RefreshToken);

internal sealed record SessionLoad(Protection Mode, SavedSession? Session, string? Error);

internal static class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <returns>null se o arquivo não existe.</returns>
    public static SessionLoad? Load(string path)
    {
        if (!File.Exists(path)) return null;

        var read = SecureFile.Read(path);

        if (read.Error is not null)
            return new SessionLoad(read.Mode, null, read.Error);

        // Protegida por senha: a sessão só aparece depois do desbloqueio.
        if (read.Mode == Protection.Passphrase)
            return new SessionLoad(Protection.Passphrase, null, null);

        var session = Parse(read.Plaintext);
        return session is null
            ? new SessionLoad(read.Mode, null, "Arquivo de sessão inválido. Rode \"login\" de novo.")
            : new SessionLoad(read.Mode, session, null);
    }

    public static SavedSession? Unlock(string path, string passphrase) =>
        Parse(SecureFile.Unlock(path, passphrase));

    /// <returns>false se pediu criptografia e só deu para gravar em texto puro.</returns>
    public static bool Save(string path, SavedSession session, Protection mode, string? passphrase = null) =>
        SecureFile.Write(path, JsonSerializer.Serialize(session), mode, passphrase);

    private static SavedSession? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            var session = JsonSerializer.Deserialize<SavedSession>(json, JsonOptions);
            return session is { AccountName.Length: > 0, RefreshToken.Length: > 0 } ? session : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
