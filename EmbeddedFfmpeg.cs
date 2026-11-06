using System.Reflection;
using System.Security.Cryptography;

namespace RemotePortable;

// Extrai o binário incorporado para cache do usuário, verificando o hash antes de executar.
internal static class EmbeddedFfmpeg
{
    private const string ResourceName = "RemotePortable.Binaries.ffmpeg.exe";
    private static readonly Lazy<string> Executable = new(Extract, LazyThreadSafetyMode.ExecutionAndPublication);
    public static string GetExecutablePath() => Executable.Value;

    private static string Extract()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new FileNotFoundException("FFmpeg não está incorporado ao aplicativo. Recompile usando compilar.cmd.");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        byte[] payload = buffer.ToArray();
        string sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RemotePortable", "bin", sha);
        Directory.CreateDirectory(folder);
        string destination = Path.Combine(folder, "ffmpeg.exe");
        if (IsValid(destination, sha)) return destination;

        // Arquivo temporário exclusivo + substituição atômica; suporta dois processos simultâneos.
        string temp = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temp, payload);
            if (!IsValid(temp, sha)) throw new IOException("Falha na validação SHA-256 do FFmpeg extraído.");
            File.Move(temp, destination, overwrite: true);
        }
        finally { try { File.Delete(temp); } catch { } }
        return destination;
    }

    private static bool IsValid(string path, string expected)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
