using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZeroIDE.Mcp.ConnectionsV2;

// This is configuration selection only. It NEVER starts/stops a transport.
// Runtime handover must stage/probe before CommitSelection, and retire the old route afterwards.
internal sealed class ConnectionStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32 };
    public ConnectionStore(string path) { _path = Path.GetFullPath(path); }
    private FileStream Lock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        try { return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("Connection store is busy; retry after reloading."); }
    }
    public ConnectionCatalog Read() { using var gate = Lock(); return ReadCore(); }
    private ConnectionCatalog ReadCore()
    {
        if (!File.Exists(_path)) throw new FileNotFoundException("Connection store is not initialized.");
        if (new FileInfo(_path).Length > 1024 * 1024) throw new InvalidDataException("Connection store is too large.");
        try { return (JsonSerializer.Deserialize<ConnectionCatalog>(File.ReadAllText(_path), Json) ?? throw new InvalidDataException("Empty connection catalog.")).Validate(); }
        catch (JsonException) { throw new InvalidDataException("Invalid connection catalog. Restore the explicit backup; do not reset automatically."); }
    }
    public ConnectionCatalog Initialize(ConnectionCatalog catalog)
    {
        using var gate = Lock();
        if (File.Exists(_path)) throw new InvalidOperationException("Connection store already exists; migration cannot overwrite it.");
        catalog.Validate(); Write(catalog); return catalog;
    }
    private ConnectionCatalog Change(long expectedRevision, Func<ConnectionCatalog, ConnectionCatalog> update)
    {
        using var gate = Lock();
        var current = ReadCore();
        if (current.Revision != expectedRevision) throw new InvalidOperationException("Revision conflict; reload before changing connections.");
        var next = (update(current) with { Revision = checked(current.Revision + 1) }).Validate();
        Write(next); return next;
    }
    private void Write(ConnectionCatalog value)
    {
        string temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json));
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); }
            if (File.Exists(_path)) File.Replace(temp, _path, _path + ".previous", true);
            else File.Move(temp, _path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public ConnectionCatalog RefreshLegacyInventory(ConnectionCatalog inventory, long revision) => Change(revision, c =>
    {
        if (c.RuntimeEnabled || c.Pending != null) throw new InvalidOperationException("Cannot refresh active or pending universal selection.");
        return c with { ActiveProfileId = inventory.ActiveProfileId, Profiles = c.Profiles.Where(p => !inventory.Profiles.Any(q => q.Id == p.Id)).Concat(inventory.Profiles).ToArray() };
    });
    public ConnectionCatalog Prepare(ConnectionProfile profile, long revision, DateTimeOffset now)
    {
        // Immutable detached copy: caller cannot mutate its argument array after validation.
        var detached = JsonSerializer.Deserialize<ConnectionProfile>(JsonSerializer.Serialize(profile, Json), Json)!.Validate();
        return Change(revision, c => c.Pending != null ? throw new InvalidOperationException("A connection change is already pending; cancel it explicitly first.") : c with { Pending = new PreparedConnection { OperationId = Guid.NewGuid().ToString("N"), Profile = detached, CreatedAt = now } });
    }
    public ConnectionCatalog Cancel(string operationId, long revision) => Change(revision, c =>
        c.Pending?.OperationId != operationId ? throw new InvalidOperationException("Pending operation does not match.") : c with { Pending = null });
    public ConnectionCatalog UpdatePendingEndpoint(string operationId, long revision, string endpoint) => Change(revision, c =>
    {
        if (c.Pending?.OperationId != operationId) throw new InvalidOperationException("Pending operation does not match.");
        return c with { Pending = c.Pending with { Profile = (c.Pending.Profile with { Endpoint = endpoint }).Validate(requireEndpoint: true), CreatedAt = DateTimeOffset.UtcNow } };
    });
    public ConnectionCatalog CommitSelection(string operationId, long revision, ConnectionVerification proof, DateTimeOffset now)
    {
        return Change(revision, c =>
        {
            var pending = c.Pending;
            if (pending == null || pending.OperationId != operationId) throw new InvalidOperationException("Pending operation does not match.");
            var profile = pending.Profile.Validate(requireEndpoint: true);
            if (proof.OperationId != operationId || proof.ProfileFingerprint != profile.Fingerprint() || !proof.ServerIdentityMatched || !proof.McpInitialized || string.IsNullOrWhiteSpace(proof.VantagePoint) || now - proof.CheckedAt > TimeSpan.FromMinutes(5) || proof.CheckedAt > now.AddSeconds(30) || proof.CheckedAt < pending.CreatedAt) throw new InvalidOperationException("Fresh identity-bound MCP verification is required.");
            var profiles = c.Profiles.Where(p => !p.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase)).Append(profile).ToArray();
            return c with { RuntimeEnabled = true, Profiles = profiles, ActiveProfileId = profile.Id, Pending = null, History = new[] { new ConnectionHistory { Profile = c.Active, ReplacedAt = now, RuntimeEnabled = c.RuntimeEnabled } }.Concat(c.History).Take(10).ToArray() };
        });
    }
    public ConnectionCatalog RestorePreviousSelection(long revision) => Change(revision, c =>
    {
        if (c.Pending != null || c.History.Length == 0) throw new InvalidOperationException("Cancel pending change first; history is required for rollback.");
        var old = c.History[0].Profile;
        return c with { RuntimeEnabled = c.History[0].RuntimeEnabled, ActiveProfileId = old.Id, Profiles = c.Profiles.Where(p => !p.Id.Equals(old.Id, StringComparison.OrdinalIgnoreCase)).Append(old).ToArray(), History = c.History.Skip(1).ToArray() };
    });
}
