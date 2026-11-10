using System.Text.Json;
namespace ZeroIDE.Mcp.ConnectionsV2;

internal static class LegacyConnectionImport
{
    // Read legacy values, but copy only an explicit allow-list. Never serialize its credentials.
    public static ConnectionCatalog Read(JsonElement legacy)
    {
        string Text(string key, string fallback = "") => legacy.TryGetProperty(key, out var v) ? v.GetString() ?? fallback : fallback;
        bool tunnel = !legacy.TryGetProperty("TunnelEnabled", out var enabled) || enabled.GetBoolean();
        int port = legacy.TryGetProperty("Port", out var p) ? p.GetInt32() : 39011;
        if (port is < 1 or > 65535) throw new ArgumentException("Invalid local server port.");
        string mode = tunnel ? Text("ConnectionMode", "ssh") : "local";
        if (mode is not ("local" or "ngrok" or "cloudflare" or "ssh")) throw new InvalidDataException("Unknown legacy mode; no automatic fallback.");
        var profiles = new List<ConnectionProfile> { new() { Id = "local", Name = "Local MCP", Adapter = "local", Scope = AccessScope.Local, Ownership = ConnectionOwnership.Local, Endpoint = $"http://127.0.0.1:{port}/mcp" } };
        foreach (string adapter in new[] { "ngrok", "cloudflare", "ssh" })
        {
            string origin = Text(adapter == "ngrok" ? "NgrokUrl" : adapter == "cloudflare" ? "CloudflareUrl" : "SshPublicUrl");
            if (origin.Length > 0) origin = EndpointRules.Normalize(origin, AccessScope.Public);
            bool configured = origin.Length > 0 || mode == adapter || adapter == "ssh" && Text("SshTarget").Length > 0;
            if (!configured) continue;
            // Existing built-in launch settings remain in the original config in phase 1.
            // External ownership is intentional: import alone must not spawn/adopt/kill a process.
            profiles.Add(new ConnectionProfile { Id = "legacy-" + adapter, Name = adapter == "ssh" ? "Existing connection" : adapter, Adapter = "legacy-" + adapter, Scope = AccessScope.Public, Ownership = ConnectionOwnership.External, Endpoint = origin.Length == 0 ? "" : (new Uri(origin).AbsolutePath == "/" ? origin.TrimEnd('/') + "/mcp" : origin), AddressKind = "unknown", HelpText = "Imported as inventory only. Existing runtime and credentials are unchanged; handover requires explicit configuration and verification." }.Validate());
        }
        return new ConnectionCatalog { ActiveProfileId = mode == "local" ? "local" : "legacy-" + mode, Profiles = profiles.ToArray() }.Validate();
    }
}
