using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace RemotePortable.Shared;

/// <summary>Persistent P-256 identity. Never puts a private key in the executable or Relay request.</summary>
internal sealed class DeviceIdentity : IDisposable
{
    const string Label = "RemotePortable Device Identity";
    readonly ECDsa key;
    DeviceIdentity(ECDsa key) => this.key = key;
    public string PublicKey => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    public string Id => Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
    public string Sign(ReadOnlySpan<byte> challenge) =>
        Convert.ToBase64String(key.SignData(challenge, HashAlgorithmName.SHA256));

    public static DeviceIdentity LoadOrCreate()
    {
        var encoded = LoadKey();
        if (encoded is not null)
        {
            var existing = ECDsa.Create();
            try { existing.ImportPkcs8PrivateKey(Convert.FromBase64String(encoded), out _); return new DeviceIdentity(existing); }
            catch { existing.Dispose(); throw new CryptographicException("Identidade local corrompida. Não gere outra automaticamente: o dispositivo precisará de nova aprovação."); }
        }
        using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var exported = Convert.ToBase64String(generated.ExportPkcs8PrivateKey());
        if (!StoreKey(exported)) throw new CryptographicException("Não foi possível armazenar a identidade no cofre do sistema.");
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(exported), out _);
        return new DeviceIdentity(key);
    }

    static string WindowsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemotePortable", "device-identity.dpapi");
    static string? LoadKey()
    {
        if (OperatingSystem.IsWindows())
        {
            if (!File.Exists(WindowsFile)) return null;
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(WindowsFile), null, DataProtectionScope.CurrentUser));
        }
        if (OperatingSystem.IsLinux()) return SecretTool(false, null);
        throw new PlatformNotSupportedException();
    }
    static bool StoreKey(string value)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WindowsFile)!);
            File.WriteAllBytes(WindowsFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
            return true;
        }
        if (OperatingSystem.IsLinux()) return SecretTool(true, value) is not null;
        throw new PlatformNotSupportedException();
    }
    static string? SecretTool(bool store, string? value)
    {
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo("secret-tool")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = store,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        p.StartInfo.ArgumentList.Add(store ? "store" : "lookup");
        if (store) p.StartInfo.ArgumentList.Add("--label=" + Label);
        p.StartInfo.ArgumentList.Add("application");
        p.StartInfo.ArgumentList.Add("RemotePortable-device-identity");
        p.Start();
        if (store) { p.StandardInput.Write(value); p.StandardInput.Close(); }
        var output = p.StandardOutput.ReadToEnd().Trim();
        if (!p.WaitForExit(5000)) { p.Kill(); throw new IOException("Tempo esgotado ao acessar chaveiro."); }
        if (p.ExitCode != 0) { if (store) throw new IOException("Falha ao gravar identidade no chaveiro."); return null; }
        return store ? "OK" : (string.IsNullOrEmpty(output) ? null : output);
    }
    public void Dispose() => key.Dispose();
}
