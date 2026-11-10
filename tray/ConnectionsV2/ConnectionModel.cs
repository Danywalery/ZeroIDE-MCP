using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZeroIDE.Mcp.ConnectionsV2;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum AccessScope { Local, Private, Public }
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum ConnectionOwnership { Local, Managed, External }

// Data only. No approval flag, inline credentials, network access, or process launch.
internal sealed record ConnectionLaunch
{
    public string Executable { get; init; } = "";
    public string[] Arguments { get; init; } = [];
    public string WorkingDirectory { get; init; } = "";
    public string ConfigurationFile { get; init; } = "";
    public string Fingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this))));
}
internal sealed record ConnectionProfile
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public AccessScope Scope { get; init; }
    public ConnectionOwnership Ownership { get; init; }
    // Informational for external profiles; adapter is NOT a provider allow-list.
    public string Adapter { get; init; } = "external";
    public string Endpoint { get; init; } = "";
    public string AddressKind { get; init; } = "stable"; // stable | temporary | unknown
    public ConnectionLaunch? Launch { get; init; }
    public string HelpText { get; init; } = "";
    public ConnectionProfile Validate(bool requireEndpoint = false)
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 64 || Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw new ArgumentException("Invalid profile ID.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 120 || Name.Any(char.IsControl)) throw new ArgumentException("Invalid display name.");
        if (!Enum.IsDefined(Scope) || !Enum.IsDefined(Ownership)) throw new ArgumentException("Unknown scope or ownership.");
        if (string.IsNullOrWhiteSpace(Adapter) || Adapter.Length > 80 || Adapter.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw new ArgumentException("Invalid adapter ID.");
        if (AddressKind is not ("stable" or "temporary" or "unknown")) throw new ArgumentException("Unknown address lifetime.");
        if (HelpText == null || HelpText.Length > 8000) throw new ArgumentException("Invalid help text.");
        if (Ownership == ConnectionOwnership.Local && Scope != AccessScope.Local || Scope == AccessScope.Local && Ownership != ConnectionOwnership.Local) throw new ArgumentException("Local scope requires local ownership.");
        string endpoint = EndpointRules.Normalize(Endpoint, Scope, requireEndpoint);
        if (Ownership != ConnectionOwnership.Managed && Launch != null) throw new ArgumentException("External and local profiles cannot own a launch command.");
        if (Ownership == ConnectionOwnership.Managed)
        {
            if (Launch == null || string.IsNullOrWhiteSpace(Launch.Executable) || !Path.IsPathFullyQualified(Launch.Executable) || Launch.Executable.Any(char.IsControl)) throw new ArgumentException("Managed connections require an absolute executable path.");
            if (Launch.Arguments == null || Launch.Arguments.Length > 128 || Launch.Arguments.Any(a => a == null || a.Length > 8192 || a.Any(char.IsControl))) throw new ArgumentException("Invalid argument list.");
            foreach (var arg in Launch.Arguments)
                if (new[] { "--token", "--authtoken", "--password", "--secret" }.Any(flag => arg.Equals(flag, StringComparison.OrdinalIgnoreCase) || arg.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Inline credentials are not allowed; use a protected configuration file.");
            foreach (var path in new[] { Launch.WorkingDirectory, Launch.ConfigurationFile })
                if (path == null || path.Any(char.IsControl) || path.Length > 0 && !Path.IsPathFullyQualified(path)) throw new ArgumentException("Launch paths must be absolute.");
        }
        return this with { Endpoint = endpoint };
    }
    public string Fingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Validate()))));
}
internal static class EndpointRules
{
    public static string Normalize(string? input, AccessScope scope, bool required = true)
    {
        if (string.IsNullOrWhiteSpace(input)) return !required ? "" : throw new ArgumentException("MCP endpoint is required.");
        if (input.Length > 2048 || input.Any(char.IsControl) || input.Contains('\\') || !Uri.TryCreate(input.Trim(), UriKind.Absolute, out var u) || !string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment)) throw new ArgumentException("Use an absolute MCP URL without credentials, query, or fragment.");
        if (scope == AccessScope.Local)
        {
            if (!u.IsLoopback || u.Scheme is not ("http" or "https")) throw new ArgumentException("Local endpoint must use loopback HTTP or HTTPS.");
        }
        else if (u.Scheme != "https" || u.IsLoopback) throw new ArgumentException("Remote endpoint requires non-loopback HTTPS.");
        return u.AbsoluteUri; // Preserve the entire route, case and trailing slash. Never append /mcp.
    }
}
internal sealed record PreparedConnection
{
    public string OperationId { get; init; } = "";
    public ConnectionProfile Profile { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
}
internal sealed record ConnectionHistory
{
    public ConnectionProfile Profile { get; init; } = new();
    public DateTimeOffset ReplacedAt { get; init; }
    public bool RuntimeEnabled { get; init; }
}
internal sealed record ConnectionCatalog
{
    public int SchemaVersion { get; init; } = 1;
    public long Revision { get; init; }
    public bool RuntimeEnabled { get; init; }
    public string ActiveProfileId { get; init; } = "";
    public ConnectionProfile[] Profiles { get; init; } = [];
    public PreparedConnection? Pending { get; init; }
    public ConnectionHistory[] History { get; init; } = [];
    [JsonIgnore] public ConnectionProfile Active => Profiles.Single(p => p.Id == ActiveProfileId);
    public ConnectionCatalog Validate()
    {
        if (SchemaVersion != 1 || Revision < 0 || Profiles == null || Profiles.Length is < 1 or > 64 || Profiles.Any(p => p == null) || Profiles.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Profiles.Length || !Profiles.Any(p => p.Id == ActiveProfileId) || History == null || History.Length > 10) throw new InvalidDataException("Invalid connection catalog or unsupported schema.");
        foreach (var p in Profiles) p.Validate();
        if (Pending != null) { if (!Guid.TryParseExact(Pending.OperationId, "N", out _)) throw new InvalidDataException("Invalid operation ID."); if (Pending.Profile == null) throw new InvalidDataException("Missing pending profile."); Pending.Profile.Validate(); }
        foreach (var h in History) { if (h == null) throw new InvalidDataException("Invalid history."); h.Profile.Validate(); }
        return this;
    }
}
// Runtime-only evidence; never deserialized from agent JSON or persisted as an authorization flag.
// The future verifier issues this after binding a challenge and MCP initialize to the candidate.
internal sealed record ConnectionVerification(string OperationId, string ProfileFingerprint, DateTimeOffset CheckedAt, bool ServerIdentityMatched, bool McpInitialized, string VantagePoint);
