using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Bdk.Crypto;

// BdkTool: roda no SEU PC. Gera o par de chaves e abre backups e logs cifrados do BdkHourFarm.

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Usage();
    return 0;
}

try
{
    return args[0].ToLowerInvariant() switch
    {
        "keygen" => KeyGen(args),
        "backup" => OpenBackup(args),
        "logs" => OpenLogs(args),
        "fingerprint" => Fingerprint(args),
        _ => Fail($"Comando desconhecido: {args[0]}"),
    };
}
catch (Exception ex) when (ex is CryptographicException or InvalidDataException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("Erro: " + ex.Message);
    return 1;
}

static void Usage()
{
    Console.WriteLine("""
        BdkTool - abre backups e logs cifrados do BdkHourFarm (rode no seu PC)

          BdkTool keygen [--out pasta]
              Gera o par de chaves. Cria bdk-private.pem (protegida por senha: guarde!)
              e bdk-public.pem (é esta que vai para o servidor, em data/backup-public.pem).

          BdkTool backup <arquivo.bdkb> [--out pasta] [--key bdk-private.pem]
              Abre um backup e extrai os arquivos para a pasta.

          BdkTool logs <arquivo.bdklog | pasta> [--out arquivo.txt] [--key bdk-private.pem]
              Descriptografa os logs (um arquivo ou todos de uma pasta, em ordem).

          BdkTool fingerprint [bdk-public.pem]
              Mostra a impressão da chave, para conferir com a que aparece no painel.
        """);
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    Console.Error.WriteLine("Use: BdkTool --help");
    return 1;
}

static string? Option(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
    return null;
}

static string ReadSecret(string prompt)
{
    Console.Error.Write(prompt);

    if (Console.IsInputRedirected)
        return Console.ReadLine() ?? "";

    var text = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;

        if (key.Key == ConsoleKey.Backspace)
        {
            if (text.Length > 0) text.Length--;
            continue;
        }

        if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
    }

    Console.Error.WriteLine();
    return text.ToString();
}

static RSA LoadPrivateKey(string[] args)
{
    var path = Option(args, "--key") ?? "bdk-private.pem";
    if (!File.Exists(path))
        throw new IOException($"Não achei a chave privada em \"{path}\". Use --key para apontar o arquivo.");

    var pem = File.ReadAllText(path);
    var password = ReadSecret("Senha da chave privada: ");

    var rsa = RSA.Create();
    try
    {
        rsa.ImportFromEncryptedPem(pem, password);
    }
    catch (CryptographicException)
    {
        rsa.Dispose();
        throw new CryptographicException("Senha incorreta (ou arquivo de chave inválido).");
    }

    return rsa;
}

static int KeyGen(string[] args)
{
    var dir = Option(args, "--out") ?? ".";
    Directory.CreateDirectory(dir);

    var privatePath = Path.Combine(dir, "bdk-private.pem");
    var publicPath = Path.Combine(dir, "bdk-public.pem");

    if (File.Exists(privatePath) || File.Exists(publicPath))
    {
        Console.Error.WriteLine("Já existe um par de chaves nessa pasta. Não vou sobrescrever (você perderia o acesso aos backups antigos).");
        return 1;
    }

    var first = ReadSecret("Crie uma senha para a chave privada (mínimo 12 caracteres): ");
    if (first.Length < 12)
    {
        Console.Error.WriteLine("Senha curta demais.");
        return 1;
    }

    if (first != ReadSecret("Repita a senha: "))
    {
        Console.Error.WriteLine("As senhas não conferem. Nada foi criado.");
        return 1;
    }

    Console.Error.WriteLine("Gerando chave RSA de 4096 bits (alguns segundos)...");
    using var rsa = RSA.Create(4096);

    var privatePem = rsa.ExportEncryptedPkcs8PrivateKeyPem(
        first.AsSpan(),
        new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000));

    File.WriteAllText(privatePath, privatePem);
    File.WriteAllText(publicPath, rsa.ExportSubjectPublicKeyInfoPem());

    if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(privatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

    Console.WriteLine($"Chave privada: {Path.GetFullPath(privatePath)}");
    Console.WriteLine($"Chave pública: {Path.GetFullPath(publicPath)}");
    Console.WriteLine($"Impressão:     {Hybrid.Fingerprint(rsa)}");
    Console.WriteLine();
    Console.WriteLine("IMPORTANTE:");
    Console.WriteLine(" - Guarde a chave privada E a senha em lugar seguro (gerenciador de senhas + cópia offline).");
    Console.WriteLine("   Se perder qualquer uma, os backups e logs ficam ILEGÍVEIS para sempre.");
    Console.WriteLine(" - Envie SÓ a pública ao servidor:  scp bdk-public.pem root@SEU_IP:/opt/bdkhourfarm/data/backup-public.pem");
    Console.WriteLine("   Depois:  chown bdk:bdk /opt/bdkhourfarm/data/backup-public.pem && systemctl restart bdkhourfarm");
    return 0;
}

static int Fingerprint(string[] args)
{
    var path = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "bdk-public.pem";
    using var key = Hybrid.LoadPublicKey(File.ReadAllText(path));
    Console.WriteLine(Hybrid.Fingerprint(key));
    return 0;
}

static int OpenBackup(string[] args)
{
    if (args.Length < 2 || args[1].StartsWith("--"))
        return Fail("Informe o arquivo de backup: BdkTool backup <arquivo.bdkb>");

    var file = args[1];
    if (!File.Exists(file)) throw new IOException($"Arquivo não encontrado: {file}");

    var outDir = Option(args, "--out") ?? Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(file))!, Path.GetFileNameWithoutExtension(file) + "-aberto");

    if (Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any())
        throw new IOException($"A pasta \"{outDir}\" já tem arquivos. Use --out com outra pasta.");

    using var key = LoadPrivateKey(args);
    var zipBytes = Hybrid.DecryptBlob(File.ReadAllBytes(file), key);

    Directory.CreateDirectory(outDir);
    var root = Path.GetFullPath(outDir);

    using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
    foreach (var entry in zip.Entries)
    {
        // O backup só tem arquivos soltos; qualquer caminho estranho é recusado.
        if (entry.Name.Length == 0 || entry.FullName != entry.Name || entry.Name.Contains("..")
            || entry.Name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
            throw new InvalidDataException($"Nome de arquivo suspeito no backup: {entry.FullName}");

        var target = Path.GetFullPath(Path.Combine(root, entry.Name));
        if (!target.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidDataException($"Nome de arquivo suspeito no backup: {entry.FullName}");

        entry.ExtractToFile(target, overwrite: false);
        Console.WriteLine($"  {entry.Name}  ({entry.Length} bytes)");
    }

    Console.WriteLine();
    Console.WriteLine($"Extraído em: {root}");
    Console.WriteLine("ATENÇÃO: esta pasta contém segredos em texto (sessão da Steam, webhook, token).");
    Console.WriteLine("Para restaurar, copie os arquivos para a pasta data/ do servidor e apague esta pasta.");
    return 0;
}

static int OpenLogs(string[] args)
{
    if (args.Length < 2 || args[1].StartsWith("--"))
        return Fail("Informe o arquivo ou a pasta: BdkTool logs <arquivo.bdklog | pasta>");

    var source = args[1];
    var files = Directory.Exists(source)
        ? Directory.GetFiles(source, "*.bdklog").OrderBy(f => f, StringComparer.Ordinal).ToArray()
        : File.Exists(source) ? new[] { source } : throw new IOException($"Não encontrado: {source}");

    if (files.Length == 0) throw new IOException("Nenhum arquivo .bdklog nessa pasta.");

    using var key = LoadPrivateKey(args);

    var outPath = Option(args, "--out");
    using var output = outPath is null
        ? Console.OpenStandardOutput()
        : new FileStream(outPath, FileMode.Create, FileAccess.Write);

    var total = 0;
    foreach (var file in files)
    {
        if (files.Length > 1)
        {
            var title = Encoding.UTF8.GetBytes($"===== {Path.GetFileName(file)} =====\n");
            output.Write(title, 0, title.Length);
        }

        using var input = File.OpenRead(file);
        var result = Hybrid.DecryptLog(input, key, output);
        total += result.Records;

        if (result.TruncatedTail)
            Console.Error.WriteLine($"Aviso: {Path.GetFileName(file)} termina no meio de um registro (o programa foi encerrado de repente). O resto foi lido.");
    }

    output.Flush();
    Console.Error.WriteLine($"{files.Length} arquivo(s), {total} bloco(s) de log abertos." + (outPath is null ? "" : $" Salvo em {Path.GetFullPath(outPath)}."));
    return 0;
}
