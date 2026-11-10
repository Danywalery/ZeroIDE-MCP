using System.Text.Json;
using ZeroIDE.Mcp.ConnectionsV2;

int count = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
void Reject(Action action, string label) { try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or IOException) { Check(true, label); return; } throw new Exception("FAIL, accepted: " + label); }
ConnectionProfile External(string endpoint = "https://relay.example/agents/pc/MCP/") => new() { Id = "my-relay", Name = "My own relay", Scope = AccessScope.Public, Ownership = ConnectionOwnership.External, Adapter = "anything", Endpoint = endpoint };
var local = new ConnectionProfile { Id = "local", Name = "Local", Scope = AccessScope.Local, Ownership = ConnectionOwnership.Local, Adapter = "local", Endpoint = "http://127.0.0.1:39011/mcp" };
var dir = Path.Combine(Path.GetTempPath(), "zeroide-connections-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
try
{
    Check(External().Validate().Endpoint == "https://relay.example/agents/pc/MCP/", "arbitrary provider, full path, case and trailing slash");
    Check((External() with { Endpoint = "" }).Validate().Endpoint == "", "discovery-pending profile allowed");
    int unsafeCase = 0;
    foreach (string url in new[] { "http://public.example/mcp", "https://user:secret@host.example/mcp", "https://host.example/mcp?token=secret", "https://host.example/mcp#x", "https://127.0.0.1/mcp", "javascript:alert(1)", "https://host.example/\nmcp" }) Reject(() => External(url).Validate(), "reject unsafe endpoint " + (++unsafeCase));
    Reject(() => (local with { Endpoint = "http://192.168.1.4/mcp" }).Validate(), "local means loopback, not LAN");
    Reject(() => (External() with { Scope = (AccessScope)99 }).Validate(), "unknown enum rejected");
    Reject(() => (External() with { Launch = new() { Executable = "C:\\x.exe" } }).Validate(), "external never owns process");
    var managed = External() with { Ownership = ConnectionOwnership.Managed, Launch = new() { Executable = Path.Combine(dir, "tool.exe"), Arguments = ["--config", Path.Combine(dir, "private.yml")] } };
    managed.Validate(); Check(true, "managed launch accepts separate arguments and config reference without executing");
    Reject(() => (managed with { Launch = managed.Launch! with { Executable = "tool.exe" } }).Validate(), "relative executable rejected");
    Reject(() => (managed with { Launch = managed.Launch! with { Arguments = ["--token=secret"] } }).Validate(), "inline credentials rejected");
    Check(managed.Launch!.Fingerprint() != (managed.Launch with { Arguments = ["different"] }).Fingerprint(), "launch changes invalidate approval fingerprint");
    var store = new ConnectionStore(Path.Combine(dir, "connections.json"));
    var c = store.Initialize(new() { ActiveProfileId = "local", Profiles = [local] });
    Reject(() => store.Initialize(c), "init cannot overwrite existing registry");
    var now = DateTimeOffset.UtcNow;
    c = store.Prepare(External(), c.Revision, now);
    Check(c.ActiveProfileId == "local" && c.Revision == 1 && c.Pending != null, "prepare preserves active route and advances revision");
    Reject(() => store.Prepare(External(), 0, now), "stale revision rejected");
    Reject(() => store.Prepare(External(), 1, now), "second pending change rejected");
    var op = c.Pending!.OperationId;
    var proof = new ConnectionVerification(op, c.Pending.Profile.Fingerprint(), now, true, true, "this-pc");
    Reject(() => store.CommitSelection(op, 1, proof with { ServerIdentityMatched = false }, now), "health-only result cannot activate");
    Reject(() => store.CommitSelection(op, 1, proof with { McpInitialized = false }, now), "MCP initialize is required");
    Reject(() => store.CommitSelection(op, 1, proof with { ProfileFingerprint = "wrong" }, now), "proof bound to exact candidate");
    Reject(() => store.CommitSelection(op, 1, proof with { OperationId = Guid.NewGuid().ToString("N") }, now), "proof bound to operation");
    Reject(() => store.CommitSelection(op, 1, proof, now.AddMinutes(6)), "expired evidence rejected");
    Reject(() => store.CommitSelection(op, 1, proof with { CheckedAt = now.AddMinutes(2) }, now), "future evidence rejected");
    Check(store.Read().ActiveProfileId == "local" && store.Read().Revision == 1, "failed verification leaves store unchanged");
    c = store.CommitSelection(op, 1, proof, now);
    Check(c.ActiveProfileId == "my-relay" && c.Pending == null && c.History[0].Profile.Id == "local", "verified selection saves rollback history");
    Reject(() => store.CommitSelection(op, 2, proof, now), "replayed activation rejected");
    c = store.RestorePreviousSelection(2);
    Check(c.ActiveProfileId == "local" && c.Revision == 3, "explicit rollback preserves monotonic revision");
    Check(File.Exists(Path.Combine(dir, "connections.json.previous")), "atomic replacement leaves previous catalog");
    c = store.Prepare(External(), 3, now);
    c = store.Cancel(c.Pending!.OperationId, 4);
    Check(c.Pending == null && c.ActiveProfileId == "local", "cancel leaves active connection unchanged");
    using (var gate = new FileStream(Path.Combine(dir, "connections.json.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Reject(() => store.Read(), "interprocess lock prevents simultaneous mutation");
    using var legacy = JsonDocument.Parse("{\"Port\":39011,\"TunnelEnabled\":true,\"ConnectionMode\":\"ngrok\",\"NgrokUrl\":\"https://assigned.example\",\"ControlSecret\":\"DO-NOT-COPY\",\"SshTarget\":\"owner@vps\"}");
    var imported = LegacyConnectionImport.Read(legacy.RootElement);
    Check(imported.ActiveProfileId == "legacy-ngrok" && imported.Profiles.Any(p => p.Id == "legacy-ssh"), "legacy migration preserves active and inactive choices");
    Check(imported.Active.Ownership == ConnectionOwnership.External && !JsonSerializer.Serialize(imported).Contains("DO-NOT-COPY"), "migration never takes process ownership or copies secrets");
    using var disabled = JsonDocument.Parse("{\"TunnelEnabled\":false,\"NgrokUrl\":\"https://assigned.example\"}");
    Check(LegacyConnectionImport.Read(disabled.RootElement).ActiveProfileId == "local", "disabled tunnel stays local");
    using var invalidOrigin = JsonDocument.Parse("{\"ConnectionMode\":\"ngrok\",\"NgrokUrl\":\"not a URI\"}");
    Reject(() => LegacyConnectionImport.Read(invalidOrigin.RootElement), "bad legacy URL rejected without raw URI parse");
    using var unknownMode = JsonDocument.Parse("{\"ConnectionMode\":\"new-provider\"}");
    Reject(() => LegacyConnectionImport.Read(unknownMode.RootElement), "unknown legacy mode cannot silently become local");
    var second = new ConnectionStore(Path.Combine(dir, "connections.json"));
    var before = store.Read();
    using (var blocked = new FileStream(Path.Combine(dir, "connections.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        Reject(() => store.Prepare(External(), before.Revision, now), "failed atomic replacement cannot overwrite live file");
    Check(second.Read().Revision == before.Revision && second.Read().Pending == null, "I/O failure leaves previous state intact");
    Check(Directory.GetFiles(dir, "*.tmp-*").Length == 0, "failed write cleans only its temporary file");
    c = second.Prepare(External(), before.Revision, now);
    Reject(() => store.Prepare(External(), before.Revision, now), "second agent sees revision conflict");
    c = store.Cancel(c.Pending!.OperationId, c.Revision);
    File.WriteAllText(Path.Combine(dir, "connections.json"), "{broken");
    Reject(() => store.Read(), "corrupt catalog fails closed, no reset");
    File.WriteAllText(Path.Combine(dir, "connections.json"), JsonSerializer.Serialize(c with { SchemaVersion = 999 }));
    Reject(() => store.Read(), "future schema fails closed");
    File.WriteAllText(Path.Combine(dir, "connections.json"), JsonSerializer.Serialize(c).Insert(1, "\"unknown\":true,"));
    Reject(() => store.Read(), "unknown JSON fields rejected");
    Console.WriteLine($"TOTAL: {count} passed; no production files, processes, or connections touched.");
}
finally { Directory.Delete(dir, true); }
