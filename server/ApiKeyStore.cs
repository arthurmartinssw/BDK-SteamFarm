namespace BdkHourFarm;

/// <summary>Chave da Web API da Steam (usada só para listar a sua biblioteca). Guardada criptografada.</summary>
internal sealed class ApiKeyStore
{
    private readonly string _path;
    private volatile string? _key;

    public ApiKeyStore(string path, string? configKey)
    {
        _path = path;

        if (File.Exists(path))
        {
            var read = SecureFile.Read(path);
            if (read.Plaintext is { Length: >= 16 } text)
                _key = text.Trim();
        }

        // Compatibilidade: chave escrita à mão no config.json.
        if (string.IsNullOrWhiteSpace(_key) && !string.IsNullOrWhiteSpace(configKey))
            _key = configKey.Trim();
    }

    public string? Get() => _key;

    /// <summary>As chaves da Steam têm 32 caracteres hexadecimais.</summary>
    public static bool IsValidFormat(string key) => key.Length == 32 && key.All(Uri.IsHexDigit);

    public void Set(string key)
    {
        SecureFile.Write(_path, key, Protection.Machine);
        _key = key;
    }
}
