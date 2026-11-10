using System.Text.Json;
using ZeroIDE.Mcp.ConnectionsV2;

// Offline preparation utility. Deliberately no activate/start/stop command in phase 1.
// Do not point --store at a live registry until the runtime coordinator owns handover.
internal static class ConnectionCtl
{
    static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new ArgumentException("Usage: connectionctl init|show|validate|prepare|cancel --store FILE [--legacy FILE] [--profile FILE] [--revision N] [--operation ID]");
            if ((args.Length - 1) % 2 != 0) throw new ArgumentException("Options require values.");
            var options = new Dictionary<string, string>();
            for (int i = 1; i < args.Length; i += 2) if (!options.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Duplicate option.");
            if (options.Keys.Any(k => k is not ("--store" or "--legacy" or "--profile" or "--revision" or "--operation"))) throw new ArgumentException("Unknown option.");
            string Get(string key) => options.TryGetValue(key, out var value) && value.Length > 0 ? value : throw new ArgumentException("Missing " + key);
            var jsonOptions = new JsonSerializerOptions { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
            string Input(string key) { string path = Get(key); if (new FileInfo(path).Length > 1024 * 1024) throw new ArgumentException("Input file is too large."); return File.ReadAllText(path); }
            ConnectionProfile Profile() => (JsonSerializer.Deserialize<ConnectionProfile>(Input("--profile"), jsonOptions) ?? throw new ArgumentException("Missing profile.")).Validate();
            object result;
            if (args[0] == "validate") { var profile = Profile(); result = new { valid = true, id = profile.Id, fingerprint = profile.Fingerprint(), launchesAnything = false }; }
            else
            {
                var store = new ConnectionStore(Get("--store"));
                long Revision() => long.TryParse(Get("--revision"), out var value) && value >= 0 ? value : throw new ArgumentException("Invalid revision.");
                var catalog = args[0] switch
                {
                    "init" => Init(),
                    "show" => store.Read(),
                    "prepare" => store.Prepare(Profile(), Revision(), DateTimeOffset.UtcNow),
                    "cancel" => store.Cancel(Get("--operation"), Revision()),
                    _ => throw new ArgumentException("Unsupported command. Activation is not exposed until runtime handover is integrated.")
                };
                ConnectionCatalog Init() { using var legacy = JsonDocument.Parse(Input("--legacy")); return store.Initialize(LegacyConnectionImport.Read(legacy.RootElement)); }
                object Summary(ConnectionProfile p) => new { p.Id, p.Name, p.Scope, p.Ownership, p.Adapter, p.Endpoint, p.AddressKind, launchFingerprint = p.Launch?.Fingerprint() };
                result = new { catalog.SchemaVersion, catalog.Revision, catalog.ActiveProfileId, profiles = catalog.Profiles.Select(Summary), pending = catalog.Pending == null ? null : new { catalog.Pending.OperationId, profile = Summary(catalog.Pending.Profile) }, runtimeChanged = false };
            }
            Console.WriteLine(JsonSerializer.Serialize(result)); return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or IOException or JsonException or UnauthorizedAccessException or OverflowException)
        {
            // Never echo parsed JSON, CLI argument values, executable arguments or exception file contents.
            Console.Error.WriteLine(JsonSerializer.Serialize(new { error = ex is JsonException ? "Invalid JSON input." : ex is IOException or UnauthorizedAccessException ? "Store or input file unavailable; no runtime changes were made." : ex.Message })); return 2;
        }
    }
}
