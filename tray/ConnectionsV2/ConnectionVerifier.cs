using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZeroIDE.Mcp.ConnectionsV2;

internal static class ConnectionVerifier
{
    // No redirects. A token is transmitted only after same-boot server possession is proven.
    public static async Task<ConnectionVerification> CheckAsync(ConnectionProfile profile, string operation,
        Uri localServer, string controlSecret, string bearerToken, CancellationToken cancellation = default)
    {
        profile.Validate(requireEndpoint: true);
        if (string.IsNullOrEmpty(controlSecret) || string.IsNullOrEmpty(bearerToken))
            throw new InvalidOperationException("Local control secret and MCP credential are required.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Add("ngrok-skip-browser-warning", "1");
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        using var local = new HttpRequestMessage(HttpMethod.Get, new Uri(localServer, "/local/connection-proof?nonce=" + nonce));
        local.Headers.Add("X-ZeroIDE-Control", controlSecret);
        string expected = ReadProof(await SendBounded(client, local, ct));
        using var remote = new HttpRequestMessage(HttpMethod.Get, profile.Endpoint + "?zeroide-route-proof=" + nonce);
        string received = ReadProof(await SendBounded(client, remote, ct));
        if (expected.Length != 64 || received.Length != 64 || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(received)))
            throw new InvalidOperationException("The URL does not point to this MCP server. No credential was sent.");
        using var initialize = new HttpRequestMessage(HttpMethod.Post, profile.Endpoint);
        initialize.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        initialize.Headers.Accept.ParseAdd("application/json, text/event-stream");
        initialize.Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":\"connection-check\",\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"ZeroIDE connection verifier\",\"version\":\"1.0\"}}}", Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(initialize, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        string session = response.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.First() : "";
        try
        {
            string body = await ReadBounded(response, ct);
            string json = body.TrimStart().StartsWith('{') ? body : body.Split('\n').FirstOrDefault(x => x.StartsWith("data: ", StringComparison.Ordinal))?[6..] ?? throw new InvalidDataException("Invalid MCP initialization response.");
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id) || id.GetString() != "connection-check" || !root.TryGetProperty("result", out var result) || !result.TryGetProperty("protocolVersion", out _) || !result.TryGetProperty("serverInfo", out _))
                throw new InvalidDataException("MCP initialization did not succeed.");
        }
        finally
        {
            if (session.Length > 0)
            {
                using var close = new HttpRequestMessage(HttpMethod.Delete, profile.Endpoint);
                close.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
                close.Headers.Add("Mcp-Session-Id", session);
                close.Headers.Add("MCP-Protocol-Version", "2025-03-26");
                try { using var ignored = await client.SendAsync(close, ct); } catch { /* Expired sessions are cleaned up by the server. */ }
            }
        }
        return new(operation, profile.Fingerprint(), DateTimeOffset.UtcNow, true, true, "this-pc");
    }
    private static string ReadProof(string body)
    {
        using var d = JsonDocument.Parse(body);
        return d.RootElement.GetProperty("proof").GetString() ?? "";
    }
    private static async Task<string> SendBounded(HttpClient client, HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await ReadBounded(response, ct);
    }
    private static async Task<string> ReadBounded(HttpResponseMessage response, CancellationToken ct)
    {
        const int limit = 65536;
        using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int n = await input.ReadAsync(buffer, ct);
            if (n == 0) break;
            if (output.Length + n > limit) throw new InvalidDataException("Connection response exceeds the size limit.");
            output.Write(buffer, 0, n);
            // MCP SSE initialize is a single complete event, not an endless stream.
            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream" && Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length).Contains("\n\n")) break;
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
