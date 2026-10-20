using System.Text;

namespace BdkHourFarm;

/// <summary>Grava arquivos de forma atômica (arquivo temporário + troca) e, no Linux, só o dono consegue ler (600).</summary>
internal static class AtomicFile
{
    public static void WriteText(string path, string contents)
    {
        var temp = path + ".tmp";
        if (File.Exists(temp)) File.Delete(temp);

        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using (var stream = new FileStream(temp, options))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(contents);
        }

        File.Move(temp, path, overwrite: true);
    }

    public static void WriteBytes(string path, byte[] contents)
    {
        var temp = path + ".tmp";
        if (File.Exists(temp)) File.Delete(temp);

        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using (var stream = new FileStream(temp, options))
        {
            stream.Write(contents, 0, contents.Length);
        }

        File.Move(temp, path, overwrite: true);
    }
}
