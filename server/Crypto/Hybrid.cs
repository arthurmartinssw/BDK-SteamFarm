using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Bdk.Crypto;

/// <summary>
/// Criptografia híbrida: uma chave AES-256 aleatória cifra os dados (AES-GCM) e essa chave é
/// trancada com a chave PÚBLICA RSA (OAEP-SHA256). O servidor só tem a pública; só quem tem a
/// privada (no seu PC) consegue abrir. Este arquivo é compartilhado entre o servidor e o BdkTool.
/// </summary>
public static class Hybrid
{
    private static readonly byte[] BlobMagic = { (byte)'B', (byte)'D', (byte)'K', (byte)'B' };
    private static readonly byte[] LogMagic = { (byte)'B', (byte)'D', (byte)'K', (byte)'L' };
    private const byte Version = 1;
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int MaxRecordBytes = 8 * 1024 * 1024;

    // ------------------------------------------------------------------ chaves

    /// <summary>Lê uma chave pública em PEM. Recusa chave privada e chaves fracas.</summary>
    public static RSA LoadPublicKey(string pem)
    {
        if (pem.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw new CryptographicException("Este arquivo é uma chave PRIVADA. No servidor coloque só a chave pública.");

        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
        }
        catch (Exception ex)
        {
            rsa.Dispose();
            throw new CryptographicException("Chave pública inválida: " + ex.Message);
        }

        if (rsa.KeySize < 2048)
        {
            rsa.Dispose();
            throw new CryptographicException("Chave RSA pequena demais (mínimo de 2048 bits).");
        }

        return rsa;
    }

    /// <summary>Impressão curta da chave, para conferir que servidor e PC usam o mesmo par.</summary>
    public static string Fingerprint(RSA key)
    {
        var hash = SHA256.HashData(key.ExportSubjectPublicKeyInfo());
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    // ------------------------------------------------------------------ blob (backups)

    public static byte[] EncryptBlob(byte[] plaintext, RSA publicKey)
    {
        var key = RandomNumberGenerator.GetBytes(KeyBytes);
        var wrapped = publicKey.Encrypt(key, RSAEncryptionPadding.OaepSHA256);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);

        var headerLength = 4 + 1 + 2 + wrapped.Length + NonceBytes;
        var output = new byte[headerLength + plaintext.Length + TagBytes];

        BlobMagic.CopyTo(output, 0);
        output[4] = Version;
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(5, 2), (ushort)wrapped.Length);
        wrapped.CopyTo(output, 7);
        nonce.CopyTo(output, 7 + wrapped.Length);

        var cipher = output.AsSpan(headerLength, plaintext.Length);
        var tag = output.AsSpan(headerLength + plaintext.Length, TagBytes);

        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintext, cipher, tag, output.AsSpan(0, headerLength));

        CryptographicOperations.ZeroMemory(key);
        return output;
    }

    public static byte[] DecryptBlob(byte[] data, RSA privateKey)
    {
        if (data.Length < 7 || !data.AsSpan(0, 4).SequenceEqual(BlobMagic))
            throw new InvalidDataException("Não é um backup do BDK (cabeçalho BDKB não encontrado).");
        if (data[4] != Version)
            throw new InvalidDataException("Versão de backup não suportada. Atualize o BdkTool.");

        var wrappedLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(5, 2));
        var headerLength = 7 + wrappedLength + NonceBytes;
        if (wrappedLength == 0 || data.Length < headerLength + TagBytes)
            throw new InvalidDataException("Arquivo de backup incompleto ou corrompido.");

        var key = Unwrap(data.AsSpan(7, wrappedLength).ToArray(), privateKey);
        var nonce = data.AsSpan(7 + wrappedLength, NonceBytes);
        var cipherLength = data.Length - headerLength - TagBytes;
        var plaintext = new byte[cipherLength];

        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, data.AsSpan(headerLength, cipherLength), data.AsSpan(headerLength + cipherLength, TagBytes),
                plaintext, data.AsSpan(0, headerLength));
        }
        catch (CryptographicException)
        {
            throw new CryptographicException("O backup foi alterado ou está corrompido.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return plaintext;
    }

    private static byte[] Unwrap(byte[] wrapped, RSA privateKey)
    {
        try
        {
            return privateKey.Decrypt(wrapped, RSAEncryptionPadding.OaepSHA256);
        }
        catch (CryptographicException)
        {
            throw new CryptographicException("Esta chave privada não abre este arquivo (é de outro par de chaves).");
        }
    }

    // ------------------------------------------------------------------ logs (vários registros)

    /// <summary>Cifra registros de log em sequência, com uma chave por arquivo e um contador como nonce.</summary>
    public sealed class LogEncryptor
    {
        private readonly object _gate = new();
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(KeyBytes);
        private readonly byte[] _prefix = RandomNumberGenerator.GetBytes(4);
        private ulong _counter;

        public LogEncryptor(RSA publicKey)
        {
            var wrapped = publicKey.Encrypt(_key, RSAEncryptionPadding.OaepSHA256);
            var header = new byte[4 + 1 + 2 + wrapped.Length + 4];

            LogMagic.CopyTo(header, 0);
            header[4] = Version;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(5, 2), (ushort)wrapped.Length);
            wrapped.CopyTo(header, 7);
            _prefix.CopyTo(header, 7 + wrapped.Length);

            Header = header;
        }

        /// <summary>Cabeçalho do arquivo (grave uma vez, no começo).</summary>
        public byte[] Header { get; }

        /// <summary>Cifra um registro e devolve: tamanho (4 bytes) + dados cifrados + etiqueta.</summary>
        public byte[] Seal(byte[] plaintext)
        {
            lock (_gate)
            {
                var nonce = new byte[NonceBytes];
                _prefix.CopyTo(nonce, 0);
                BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4, 8), _counter++);

                var body = plaintext.Length + TagBytes;
                var output = new byte[4 + body];
                BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0, 4), (uint)body);

                using var aes = new AesGcm(_key, TagBytes);
                aes.Encrypt(nonce, plaintext, output.AsSpan(4, plaintext.Length), output.AsSpan(4 + plaintext.Length, TagBytes));
                return output;
            }
        }
    }

    public sealed record LogResult(int Records, bool TruncatedTail);

    /// <summary>Abre um arquivo de log cifrado e escreve o texto puro em <paramref name="output"/>.</summary>
    public static LogResult DecryptLog(Stream input, RSA privateKey, Stream output)
    {
        var magic = new byte[4];
        if (ReadFull(input, magic, 4) != 4 || !magic.AsSpan().SequenceEqual(LogMagic))
            throw new InvalidDataException("Não é um arquivo de log do BDK (cabeçalho BDKL não encontrado).");

        var small = new byte[3];
        if (ReadFull(input, small, 3) != 3 || small[0] != Version)
            throw new InvalidDataException("Versão de log não suportada. Atualize o BdkTool.");

        var wrappedLength = BinaryPrimitives.ReadUInt16BigEndian(small.AsSpan(1, 2));
        var wrapped = new byte[wrappedLength];
        var prefix = new byte[4];
        if (wrappedLength == 0 || ReadFull(input, wrapped, wrappedLength) != wrappedLength || ReadFull(input, prefix, 4) != 4)
            throw new InvalidDataException("Cabeçalho do log incompleto.");

        var key = Unwrap(wrapped, privateKey);
        var records = 0;
        var truncated = false;

        try
        {
            var lengthBytes = new byte[4];
            var nonce = new byte[NonceBytes];
            prefix.CopyTo(nonce, 0);
            ulong counter = 0;

            while (true)
            {
                var got = ReadFull(input, lengthBytes, 4);
                if (got == 0) break;
                if (got != 4) { truncated = true; break; }

                var body = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
                if (body < TagBytes || body > MaxRecordBytes)
                    throw new InvalidDataException("Registro de log inválido (arquivo corrompido).");

                var buffer = new byte[body];
                if (ReadFull(input, buffer, (int)body) != (int)body) { truncated = true; break; }

                BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4, 8), counter++);
                var plain = new byte[body - TagBytes];

                try
                {
                    using var aes = new AesGcm(key, TagBytes);
                    aes.Decrypt(nonce, buffer.AsSpan(0, plain.Length), buffer.AsSpan(plain.Length, TagBytes), plain);
                }
                catch (CryptographicException)
                {
                    throw new CryptographicException($"O registro {records + 1} foi alterado ou está corrompido.");
                }

                output.Write(plain, 0, plain.Length);
                records++;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return new LogResult(records, truncated);
    }

    private static int ReadFull(Stream stream, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(buffer, total, count - total);
            if (read <= 0) break;
            total += read;
        }
        return total;
    }
}
