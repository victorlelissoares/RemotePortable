using System.Collections.Concurrent;

internal sealed class DiscoveryRegistry
{
    readonly ConcurrentDictionary<string, Entry> entries = new();
    readonly TimeSpan ttl = TimeSpan.FromSeconds(45);
    internal sealed record Entry(string Id, string Name, string Platform, string Fingerprint, string Owner, DateTimeOffset Seen);
    public bool Register(string id, string name, string platform, string fingerprint, string owner)
    {
        if (!ValidId(id) || !ValidName(name) || !ValidName(platform) || fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit)) return false;
        var now = DateTimeOffset.UtcNow;
        entries.AddOrUpdate(id, _ => new Entry(id, name, platform, fingerprint.ToUpperInvariant(), owner, now),
            (_, previous) => previous.Owner == owner || now - previous.Seen > ttl
                ? new Entry(id, name, platform, fingerprint.ToUpperInvariant(), owner, now) : previous);
        return entries.TryGetValue(id, out var current) && current.Owner == owner && current.Name == name;
    }
    public bool Heartbeat(string id, string owner)
    {
        if (!entries.TryGetValue(id, out var current) || current.Owner != owner) return false;
        entries.TryUpdate(id, current with { Seen = DateTimeOffset.UtcNow }, current);
        return true;
    }
    public void Remove(string id, string owner)
    {
        if (entries.TryGetValue(id, out var current) && current.Owner == owner)
            entries.TryRemove(new KeyValuePair<string, Entry>(id, current));
    }
    public Entry[] List()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, entry) in entries)
            if (now - entry.Seen > ttl) entries.TryRemove(new KeyValuePair<string, Entry>(id, entry));
        return entries.Values.OrderBy(x => x.Name).Take(200).ToArray();
    }
    public static bool ValidId(string id) => id.Length is >= 6 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    static bool ValidName(string name) => name.Length is >= 1 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
