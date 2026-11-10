using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace RemotePortable.Shared;

/// <summary>Stores the local password in the OS credential facility, never plaintext files.</summary>
internal static class PasswordVault
{
    const string Service = "RemotePortable";
    static string WindowsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemotePortable", "password.dpapi");

    public static string? Load()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!File.Exists(WindowsPath)) return null;
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(WindowsPath), null, DataProtectionScope.CurrentUser));
            }
            if (OperatingSystem.IsLinux())
                return RunSecretTool(false, null);
        }
        catch { }
        return null;
    }

    public static bool Save(string password)
    {
        if (password.Length < 12) return false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(Path.GetDirectoryName(WindowsPath)!);
                File.WriteAllBytes(WindowsPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));
                return true;
            }
            if (OperatingSystem.IsLinux())
                return RunSecretTool(true, password) != null;
        }
        catch { }
        return false;
    }

    static string? RunSecretTool(bool store, string? password)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("secret-tool")
        {
            UseShellExecute = false,
            RedirectStandardInput = store,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (store)
        {
            process.StartInfo.ArgumentList.Add("store");
            process.StartInfo.ArgumentList.Add("--label=RemotePortable password");
        }
        else process.StartInfo.ArgumentList.Add("lookup");
        process.StartInfo.ArgumentList.Add("application");
        process.StartInfo.ArgumentList.Add(Service);
        process.Start();
        if (store)
        {
            process.StandardInput.Write(password);
            process.StandardInput.Close();
        }
        var result = process.StandardOutput.ReadToEnd().TrimEnd('\r', '\n');
        if (!process.WaitForExit(5000))
        {
            process.Kill();
            return null;
        }
        return process.ExitCode == 0 ? (store ? "OK" : result) : null;
    }
}
