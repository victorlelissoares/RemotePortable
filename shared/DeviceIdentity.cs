using System.Security.Cryptography;
using System.Net.Security;
using System.Text;
using System.Text.Json;

namespace RemotePortable.Shared;

/// <summary>Persistent per-installation identity used only to authenticate to the Relay.</summary>
internal sealed class DeviceIdentity
{
    readonly ECDsa key;
    public string Id { get; }
    public string PublicKey => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    public string Sign(string payload) => Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256));

    DeviceIdentity(ECDsa key)
    {
        this.key = key;
        Id = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
    }

    public static DeviceIdentity LoadOrCreate()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemotePortable");
        if (string.IsNullOrWhiteSpace(folder) || folder == ".")
            folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "RemotePortable");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "device-identity.json");
        if (File.Exists(path))
        {
            var saved = JsonSerializer.Deserialize<IdentityFile>(File.ReadAllText(path)) ?? throw new InvalidDataException("Identidade do dispositivo inválida.");
            byte[] privateKey = Convert.FromBase64String(saved.PrivateKey);
            if (OperatingSystem.IsWindows()) privateKey = ProtectedData.Unprotect(privateKey, null, DataProtectionScope.CurrentUser);
            var restored = ECDsa.Create();
            restored.ImportPkcs8PrivateKey(privateKey, out _);
            CryptographicOperations.ZeroMemory(privateKey);
            return new DeviceIdentity(restored);
        }

        using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var raw = generated.ExportPkcs8PrivateKey();
        var persisted = OperatingSystem.IsWindows() ? ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser) : raw;
        File.WriteAllText(path, JsonSerializer.Serialize(new IdentityFile(Convert.ToBase64String(persisted))));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        CryptographicOperations.ZeroMemory(raw);
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(generated.ExportPkcs8PrivateKey(), out _);
        return new DeviceIdentity(key);
    }

    internal static string SignaturePayload(string action, string id, string target, string nonce) =>
        $"RemotePortable-Relay-v1\n{action}\n{id}\n{target}\n{nonce}";

    internal sealed record IdentityFile(string PrivateKey);
}

internal static class RelayClientAuth
{
    public static async Task EnsureApprovedAsync(string relay, int port, DeviceIdentity identity, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(relay, port, ct);
        using var tls = new SslStream(tcp.GetStream(), false);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = relay }, ct);
        await WriteLineAsync(tls, $"ENROLL {identity.PublicKey}", ct);
        var response = await ReadLineAsync(tls, ct);
        if (response.StartsWith("PENDING ", StringComparison.Ordinal))
            throw new UnauthorizedAccessException($"Este dispositivo ainda precisa ser aprovado no Relay. ID: {identity.Id}");
        if (response != $"APPROVED {identity.Id}") throw new IOException("Relay recusou a inscrição: " + response);
    }

    public static async Task AuthenticateAsync(SslStream tls, DeviceIdentity identity, string action, string target, CancellationToken ct)
    {
        await WriteLineAsync(tls, $"AUTH {identity.Id} {action} {target}", ct);
        var challenge = await ReadLineAsync(tls, ct);
        if (challenge.StartsWith("ERR ", StringComparison.Ordinal)) throw new UnauthorizedAccessException("Relay: " + challenge[4..]);
        if (!challenge.StartsWith("CHALLENGE ", StringComparison.Ordinal)) throw new InvalidDataException("Desafio do Relay inválido.");
        var nonce = challenge[10..];
        await WriteLineAsync(tls, "SIGN " + identity.Sign(DeviceIdentity.SignaturePayload(action, identity.Id, target, nonce)), ct);
        var result = await ReadLineAsync(tls, ct);
        if (result != "OK") throw new UnauthorizedAccessException("Relay: " + result);
    }

    internal static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        while (buffer.Length < 65536)
        {
            if (await stream.ReadAsync(one, ct) == 0) throw new EndOfStreamException();
            if (one[0] == 10) return Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r');
            buffer.WriteByte(one[0]);
        }
        throw new InvalidDataException("Resposta muito longa.");
    }

    internal static Task WriteLineAsync(Stream stream, string value, CancellationToken ct) =>
        stream.WriteAsync(Encoding.UTF8.GetBytes(value + "\n"), ct).AsTask();
}
