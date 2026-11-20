using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Media;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZeroIDE.Mcp;

public sealed class ApprovalSummaryDto
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("target")]
    public string Target { get; set; } = "";

    [JsonPropertyName("detail")]
    public string Detail { get; set; } = "";
}

public sealed class ApprovalRequestDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = ""; // "" (tool) | "oauth_consent"

    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "";

    [JsonPropertyName("note")]
    public string Note { get; set; } = "";

    [JsonPropertyName("summary")]
    public ApprovalSummaryDto Summary { get; set; } = new();

    [JsonPropertyName("argsHash")]
    public string ArgsHash { get; set; } = "";

    [JsonPropertyName("timeoutSec")]
    public int TimeoutSec { get; set; } = 60;

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";

    // OAuth 2.1 consent fields (when kind == "oauth_consent")
    [JsonPropertyName("clientName")]
    public string ClientName { get; set; } = "";

    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = "";

    [JsonPropertyName("redirectHost")]
    public string RedirectHost { get; set; } = "";

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "";

    [JsonPropertyName("scopeHuman")]
    public string ScopeHuman { get; set; } = "";

    [JsonPropertyName("risk")]
    public string Risk { get; set; } = "known"; // "known" | "unknown_site"

    [JsonPropertyName("warning")]
    public string? Warning { get; set; }

    // Screenshot preview (when tool == "take_screenshot"): base64 JPEG of exactly
    // the image that will be sent to the model if the owner approves.
    [JsonPropertyName("previewImage")]
    public string? PreviewImage { get; set; }

    [JsonPropertyName("previewHint")]
    public string? PreviewHint { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    // v0.5: which agent slot asks, the server's risk reading and a preview of the change.
    [JsonPropertyName("agent")]
    public string? Agent { get; set; }

    [JsonPropertyName("assessment")]
    public ApprovalAssessmentDto? Assessment { get; set; }

    [JsonPropertyName("preview")]
    public ApprovalPreviewDto? Preview { get; set; }
}

/// <summary>Server-side risk reading shown on the card (v0.5).</summary>
public sealed class ApprovalAssessmentDto
{
    [JsonPropertyName("level")]
    public string Level { get; set; } = "low"; // low | medium | high | critical

    [JsonPropertyName("headline")]
    public string Headline { get; set; } = "";

    [JsonPropertyName("reasons")]
    public List<string> Reasons { get; set; } = new();

    // "rule" = an allow rule was escalated, "grant" = a temporary grant did not cover it,
    // "redflag" = a dangerous-command pattern from permissions.json matched.
    [JsonPropertyName("escalated")]
    public string? Escalated { get; set; }

    // false for critical calls: the card must not offer a temporary grant.
    [JsonPropertyName("grantable")]
    public bool Grantable { get; set; } = true;
}

/// <summary>What a write or an edit changes: line counts and a compact diff (v0.5).</summary>
public sealed class ApprovalPreviewDto
{
    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    [JsonPropertyName("added")]
    public int Added { get; set; }

    [JsonPropertyName("removed")]
    public int Removed { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

public sealed class ApprovalResponseDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("decision")]
    public string Decision { get; set; } = "deny";

    [JsonPropertyName("grantMinutes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? GrantMinutes { get; set; }

    [JsonPropertyName("grantScope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GrantScope { get; set; }

    // IMPORTANT: For OAuth consent, server/server.mjs checks `resp.lifetimeDays === undefined ? 30 : resp.lifetimeDays`.
    // Therefore lifetimeDays MUST be serialized even when null ("never expires").
    [JsonPropertyName("lifetimeDays")]
    public int? LifetimeDays { get; set; }

    [JsonPropertyName("scope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scope { get; set; }

    [JsonPropertyName("by")]
    public string By { get; set; } = "user";
}

/// <summary>
/// One-click link attached to an agent message (v0.5 user_message.open). The tray re-checks
/// everything itself: only http(s) links, existing local files and folders; programs,
/// scripts and shortcuts are only ever revealed in Explorer, never launched.
/// </summary>
public sealed record MessageLink(string Kind, string Target, bool Reveal)
{
    private static readonly System.Text.RegularExpressions.Regex RevealOnly = new(
        @"\.(exe|com|bat|cmd|ps1|psm1|vbs|vbe|js|jse|wsf|wsh|hta|msi|msp|scr|lnk|reg|cpl|jar|appref-ms|url|pif|application|gadget|msc|dll|sys)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static MessageLink? TryCreate(string kind, string target, bool reveal)
    {
        target = (target ?? "").Trim();
        if (target.Length == 0 || target.Length > 2048) return null;
        if (kind == "url")
            return Uri.TryCreate(target, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? new MessageLink("url", u.AbsoluteUri, false) : null;
        if (kind != "file" && kind != "folder") return null;
        if (target.StartsWith(@"\\", StringComparison.Ordinal) || target.StartsWith("//", StringComparison.Ordinal) || !Path.IsPathFullyQualified(target)) return null;
        return new MessageLink(kind, target, kind == "file" && (reveal || RevealOnly.IsMatch(target)));
    }

    public string ButtonText => Kind == "url" ? I18n.T("Open link", "Открыть ссылку")
        : Kind == "folder" ? I18n.T("Open folder", "Открыть папку")
        : Reveal ? I18n.T("Show in folder", "Показать в папке") : I18n.T("Open", "Открыть");

    /// <summary>Opens the link; returns false when the target no longer exists or cannot be opened.</summary>
    public bool Open()
    {
        try
        {
            if (Kind == "url") return DesktopLauncher.OpenUrl(Target);
            if (Kind == "folder") return DesktopLauncher.OpenExistingFolder(Target);
            if (!File.Exists(Target)) return false;
            if (Reveal || RevealOnly.IsMatch(Target)) return DesktopLauncher.RevealInFolder(Target);
            return DesktopLauncher.OpenDocument(Target);
        }
        catch { return false; }
    }
}

public sealed class SimulateClickDto
{
    [JsonPropertyName("decision")]
    public string Decision { get; set; } = "allow_once";

    [JsonPropertyName("grantMinutes")]
    public int? GrantMinutes { get; set; }

    [JsonPropertyName("grantScope")]
    public string? GrantScope { get; set; }

    [JsonPropertyName("lifetimeDays")]
    public int? LifetimeDays { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }
}

internal static class ApprovalSoundPlayer
{
    public static void Play(string soundMode)
    {
        if (string.IsNullOrWhiteSpace(soundMode) || soundMode.Equals("none", StringComparison.OrdinalIgnoreCase))
            return;

        Task.Run(() =>
        {
            try
            {
                using var ms = BuildWavStream(soundMode.ToLowerInvariant());
                using var player = new SoundPlayer(ms);
                player.PlaySync();
            }
            catch { /* audio must never crash tray */ }
        });
    }

    private static MemoryStream BuildWavStream(string kind)
    {
        const int sampleRate = 44100;
        double durationSec = kind switch
        {
            "ping" => 0.18,
            "bell" => 0.34,
            _ => 0.28, // chime
        };
        int numSamples = (int)(sampleRate * durationSec);
        short[] samples = new short[numSamples];

        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;
            double env = Math.Exp(-t * (kind == "ping" ? 18.0 : 9.5)) * Math.Min(1.0, t / 0.008);
            double wave = kind switch
            {
                "ping" => Math.Sin(2 * Math.PI * 1046.50 * t), // C6 crisp ping
                "bell" => 0.6 * Math.Sin(2 * Math.PI * 783.99 * t) + 0.4 * Math.Sin(2 * Math.PI * 1567.98 * t), // G5+G6 bell
                _ => t < 0.11
                    ? 0.55 * Math.Sin(2 * Math.PI * 587.33 * t) + 0.45 * Math.Sin(2 * Math.PI * 880.0 * t) // D5+A5
                    : 0.55 * Math.Sin(2 * Math.PI * 740.00 * t) + 0.45 * Math.Sin(2 * Math.PI * 1174.66 * t), // F#5+D6 warm chord
            };
            samples[i] = (short)Math.Clamp(wave * env * 12000, short.MinValue, short.MaxValue);
        }

        var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        int dataSize = numSamples * 2;
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataSize);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)1); // Mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);
        foreach (short s in samples) bw.Write(s);
        bw.Flush();
        ms.Position = 0;
        return ms;
    }
}

internal sealed class ApprovalControlServer : IDisposable
{
    private readonly int _port;
    private readonly Func<string> _getSecret;
    private readonly SynchronizationContext _uiCtx;
    private readonly Func<ApprovalRequestDto, Action<string>, Task<ApprovalResponseDto>> _onRequest;
    private readonly Action<string> _log;
    private readonly Action<string, string, bool, string>? _onMessage;
    private readonly Action<string, string, string[], string[], string, string>? _onTaskStatus;
    private readonly Func<string>? _getState;
    private readonly Func<string, string, string, Task<(int, string)>>? _onConnection;
    private readonly Action<string, string, MessageLink?>? _onMessageDetails;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public ApprovalControlServer(
        int port,
        Func<string> getSecret,
        SynchronizationContext uiCtx,
        Func<ApprovalRequestDto, Action<string>, Task<ApprovalResponseDto>> onRequest,
        Action<string> log,
        Action<string, string, bool, string>? onMessage = null,
        Func<string>? getState = null,
        Action<string, string, string[], string[], string, string>? onTaskStatus = null,
        Func<string, string, string, Task<(int, string)>>? onConnection = null,
        Action<string, string, MessageLink?>? onMessageDetails = null)
    {
        _onMessageDetails = onMessageDetails;
        _port = port;
        _getSecret = getSecret;
        _uiCtx = uiCtx;
        _onRequest = onRequest;
        _log = log;
        _onMessage = onMessage;
        _getState = getState;
        _onTaskStatus = onTaskStatus;
        _onConnection = onConnection;
    }

    public void Start()
    {
        try
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            _ = AcceptLoopAsync(_cts.Token);
            _log($"approval control listener started on 127.0.0.1:{_port}");
        }
        catch (Exception ex)
        {
            _log($"approval control listener failed on port {_port}: {ex.Message}");
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                // Fully asynchronous & concurrent: never await HandleClientAsync inside the accept loop!
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    _log("approval accept error: " + ex.Message);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string tAcceptIso = DateTime.Now.ToString("HH:mm:ss.fff");
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                using var stream = client.GetStream();

                // Read raw bytes until \r\n\r\n (never use StreamReader char count for UTF-8 Content-Length!)
                byte[] scratch = new byte[8192];
                using var headerMs = new MemoryStream();
                int headerEndIndex = -1;

                while (headerEndIndex < 0)
                {
                    int n = await stream.ReadAsync(scratch, 0, scratch.Length, readDeadline.Token);
                    if (n <= 0) return;
                    headerMs.Write(scratch, 0, n);
                    byte[] cur = headerMs.GetBuffer();
                    int len = (int)headerMs.Length;
                    for (int i = 0; i <= len - 4; i++)
                    {
                        if (cur[i] == '\r' && cur[i + 1] == '\n' && cur[i + 2] == '\r' && cur[i + 3] == '\n')
                        {
                            headerEndIndex = i;
                            break;
                        }
                    }
                    if (len > 32768) return; // safety cap
                }

                byte[] allRead = headerMs.ToArray();
                string headerText = Encoding.ASCII.GetString(allRead, 0, headerEndIndex);
                string[] headerLines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
                if (headerLines.Length == 0) return;

                string[] reqParts = headerLines[0].Split(' ');
                string method = reqParts.Length > 0 ? reqParts[0] : "";
                string path = reqParts.Length > 1 ? reqParts[1] : "";

                int contentLength = 0;
                string providedSecret = "";
                bool browserOrigin = false, invalidLength = false, sawLength = false;
                for (int i = 1; i < headerLines.Length; i++)
                {
                    string line = headerLines[i];
                    int colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    string hName = line[..colon].Trim();
                    string hVal = line[(colon + 1)..].Trim();
                    if (hName.Equals("Origin", StringComparison.OrdinalIgnoreCase) || hName.Equals("Sec-Fetch-Site", StringComparison.OrdinalIgnoreCase)) browserOrigin = true;
                    if (hName.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) invalidLength = true;
                    if (hName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        if (sawLength || !int.TryParse(hVal, out contentLength) || contentLength < 0 || contentLength > 8 * 1024 * 1024) invalidLength = true;
                        sawLength = true;
                    }
                    else if (hName.Equals("X-ZeroIDE-Control", StringComparison.OrdinalIgnoreCase))
                        providedSecret = hVal;
                }

                string expectedSecret = _getSecret();
                if (string.IsNullOrEmpty(expectedSecret) || browserOrigin || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(providedSecret), Encoding.UTF8.GetBytes(expectedSecret)))
                {
                    await WriteJsonResponseAsync(stream, 403, "{\"error\":\"forbidden\"}", ct);
                    return;
                }

                if (invalidLength) { await WriteJsonResponseAsync(stream, 400, "{\"error\":\"invalid request length\"}", ct); return; }
                int bodyStart = headerEndIndex + 4;
                int alreadyReadBodyBytes = allRead.Length - bodyStart;
                byte[] bodyBytes = new byte[Math.Max(0, contentLength)];
                if (alreadyReadBodyBytes > 0 && contentLength > 0)
                {
                    Array.Copy(allRead, bodyStart, bodyBytes, 0, Math.Min(alreadyReadBodyBytes, contentLength));
                }

                int totalBodyRead = Math.Min(alreadyReadBodyBytes, contentLength);
                while (totalBodyRead < contentLength)
                {
                    int n = await stream.ReadAsync(bodyBytes, totalBodyRead, contentLength - totalBodyRead, readDeadline.Token);
                    if (n <= 0) break;
                    totalBodyRead += n;
                }

                if (totalBodyRead != contentLength) return;
                string bodyJson = Encoding.UTF8.GetString(bodyBytes, 0, totalBodyRead);
                long bodyReadMs = sw.ElapsedMilliseconds;
                string tBodyReadIso = DateTime.Now.ToString("HH:mm:ss.fff");

                if (path.StartsWith("/connections/", StringComparison.Ordinal) && _onConnection != null)
                {
                    if (contentLength > 65536) { await WriteJsonResponseAsync(stream, 400, "{\"error\":\"profile too large\"}", ct); return; }
                    var (code, result) = await _onConnection(method, path, bodyJson);
                    await WriteJsonResponseAsync(stream, code, result, ct); return;
                }
                if (method == "GET" && path == "/debug/state")
                {
                    string st = "{}";
                    try { st = _getState?.Invoke() ?? "{}"; } catch (Exception ex) { st = JsonSerializer.Serialize(new { error = ex.Message }); }
                    await WriteJsonResponseAsync(stream, 200, st, ct);
                    return;
                }

                if (method == "POST" && path == "/approval/simulate-click")
                {
                    // v0.5: an approval must come from a human click. This debug hook let
                    // anything that knew the control secret approve its own card, so it is
                    // now off unless a test harness explicitly opts in for this process.
                    if (Environment.GetEnvironmentVariable("ZEROIDE_TEST_SIMULATE_CLICK") != "1")
                    {
                        await WriteJsonResponseAsync(stream, 404, "{\"error\":\"not_found\"}", ct);
                        return;
                    }
                    bool hasLifetimeProp = false;
                    try
                    {
                        using var simDoc = JsonDocument.Parse(bodyJson);
                        hasLifetimeProp = simDoc.RootElement.TryGetProperty("lifetimeDays", out _);
                    }
                    catch { }

                    var sim = JsonSerializer.Deserialize<SimulateClickDto>(bodyJson) ?? new SimulateClickDto();
                    var simTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _uiCtx.Post(_ =>
                    {
                        bool clicked = ApprovalToastManager.SimulateDecisionOnTopCard(
                            sim.Decision,
                            sim.GrantMinutes,
                            sim.GrantScope,
                            sim.LifetimeDays,
                            hasLifetimeProp);
                        simTcs.TrySetResult(clicked);
                    }, null);
                    bool ok = await simTcs.Task;
                    await WriteJsonResponseAsync(stream, 200, JsonSerializer.Serialize(new { ok }), ct);
                    return;
                }

                if (method == "POST" && path == "/message/show")
                {
                    try
                    {
                        using var msgDoc = JsonDocument.Parse(bodyJson);
                        var msgRoot = msgDoc.RootElement;
                        string mText = msgRoot.TryGetProperty("text", out var mt) ? (mt.GetString() ?? "") : "";
                        string mKind = msgRoot.TryGetProperty("kind", out var mk) ? (mk.GetString() ?? "status") : "status";
                        bool mPersistent = !msgRoot.TryGetProperty("persistent", out var mp) || mp.GetBoolean();
                        string mId = msgRoot.TryGetProperty("id", out var mi) ? (mi.GetString() ?? "") : "";
                        string mAgent = msgRoot.TryGetProperty("agent", out var ma) && ma.ValueKind == JsonValueKind.String ? (ma.GetString() ?? "") : "";
                        MessageLink? mLink = null;
                        if (msgRoot.TryGetProperty("open", out var mo) && mo.ValueKind == JsonValueKind.Object)
                        {
                            string lKind = mo.TryGetProperty("kind", out var lk) && lk.ValueKind == JsonValueKind.String ? (lk.GetString() ?? "") : "";
                            string lTarget = mo.TryGetProperty("target", out var lt) && lt.ValueKind == JsonValueKind.String ? (lt.GetString() ?? "") : "";
                            bool lReveal = mo.TryGetProperty("reveal", out var lr) && lr.ValueKind == JsonValueKind.True;
                            mLink = MessageLink.TryCreate(lKind, lTarget, lReveal);
                        }
                        _onMessage?.Invoke(mText, mKind, mPersistent, mId);
                        _onMessageDetails?.Invoke(mId, mAgent, mLink);
                        await WriteJsonResponseAsync(stream, 200, "{\"ok\":true}", ct);
                    }
                    catch
                    {
                        await WriteJsonResponseAsync(stream, 400, "{\"error\":\"bad_json\"}", ct);
                    }
                    return;
                }

                if (method == "POST" && path == "/task/show")
                {
                    try
                    {
                        using var taskDoc = JsonDocument.Parse(bodyJson);
                        var taskRoot = taskDoc.RootElement;
                        string tTitle = taskRoot.TryGetProperty("title", out var tt) ? (tt.GetString() ?? "") : "";
                        string tCurrent = taskRoot.TryGetProperty("current", out var tc) ? (tc.GetString() ?? "") : "";
                        string[] tDone = taskRoot.TryGetProperty("done", out var td) && td.ValueKind == JsonValueKind.Array
                            ? td.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() : Array.Empty<string>();
                        string[] tNext = taskRoot.TryGetProperty("next", out var tn) && tn.ValueKind == JsonValueKind.Array
                            ? tn.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() : Array.Empty<string>();
                        string tStartedAt = taskRoot.TryGetProperty("startedAt", out var ts) ? (ts.GetString() ?? "") : "";
                        string tAgent = taskRoot.TryGetProperty("agent", out var ta) ? (ta.GetString() ?? "") : "";
                        _onTaskStatus?.Invoke(tTitle, tCurrent, tDone, tNext, tStartedAt, tAgent);
                        await WriteJsonResponseAsync(stream, 200, "{\"ok\":true}", ct);
                    }
                    catch
                    {
                        await WriteJsonResponseAsync(stream, 400, "{\"error\":\"bad_json\"}", ct);
                    }
                    return;
                }

                if (method == "POST" && path == "/screenshot/save")
                {
                    try
                    {
                        using var shotDoc = JsonDocument.Parse(bodyJson);
                        var shotRoot = shotDoc.RootElement;
                        string sB64 = shotRoot.TryGetProperty("imageBase64", out var sb) ? (sb.GetString() ?? "") : "";
                        string sDesc = shotRoot.TryGetProperty("description", out var sd) ? (sd.GetString() ?? "") : "";
                        string sNote = shotRoot.TryGetProperty("note", out var sn) ? (sn.GetString() ?? "") : "";
                        string sToken = shotRoot.TryGetProperty("tokenLabel", out var st) ? (st.GetString() ?? "legacy") : "legacy";
                        string? savedFile = ScreenshotArchive.TrySave(sB64, sDesc, sNote, sToken, _log);
                        await WriteJsonResponseAsync(stream, 200, JsonSerializer.Serialize(new { ok = true, saved = savedFile != null, file = savedFile }), ct);
                        if (savedFile != null) _log("screenshot saved: " + savedFile);
                    }
                    catch (Exception ex)
                    {
                        _log("screenshot save failed: " + ex.Message);
                        await WriteJsonResponseAsync(stream, 200, "{\"ok\":true,\"saved\":false}", ct);
                    }
                    return;
                }

                if (method != "POST" && path != "/approval/request")
                {
                    await WriteJsonResponseAsync(stream, 404, "{\"error\":\"not_found\"}", ct);
                    return;
                }

                var reqDto = JsonSerializer.Deserialize<ApprovalRequestDto>(bodyJson) ?? new ApprovalRequestDto();
                string tCardShownIso = "";
                long cardShownMs = -1;

                var tcs = new TaskCompletionSource<ApprovalResponseDto>(TaskCreationOptions.RunContinuationsAsynchronously);
                _uiCtx.Post(async _ =>
                {
                    try
                    {
                        var res = await _onRequest(reqDto, shownAtIso =>
                        {
                            tCardShownIso = shownAtIso;
                            cardShownMs = sw.ElapsedMilliseconds;
                            _log($"[TIMING id={reqDto.Id}] (a) accept={tAcceptIso} (0ms) | (b) body_read={tBodyReadIso} (+{bodyReadMs}ms, {totalBodyRead}B) | (c) card_shown={tCardShownIso} (+{cardShownMs}ms)");
                        });
                        tcs.TrySetResult(res);
                    }
                    catch (Exception ex)
                    {
                        _log("approval handler error: " + ex.Message);
                        tcs.TrySetResult(new ApprovalResponseDto { Id = reqDto.Id, Decision = "deny", By = "user" });
                    }
                }, null);

                var responseDto = await tcs.Task;
                string respJson = JsonSerializer.Serialize(responseDto);
                await WriteJsonResponseAsync(stream, 200, respJson, ct);
                long decisionSentMs = sw.ElapsedMilliseconds;
                string tDecisionSentIso = DateTime.Now.ToString("HH:mm:ss.fff");
                _log($"[TIMING id={reqDto.Id}] (d) decision_sent={tDecisionSentIso} (+{decisionSentMs}ms) decision={responseDto.Decision} by={responseDto.By}");
            }
            catch (Exception ex)
            {
                _log("approval client error: " + ex.Message);
            }
        }
    }

    private static async Task WriteJsonResponseAsync(NetworkStream stream, int statusCode, string json, CancellationToken ct)
    {
        string statusText = statusCode switch
        {
            200 => "OK",
            403 => "Forbidden",
            404 => "Not Found",
            _ => "Error",
        };
        byte[] bodyBytes = Encoding.UTF8.GetBytes(json);
        string header =
            $"HTTP/1.1 {statusCode} {statusText}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Connection: close\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, ct);
        await stream.WriteAsync(bodyBytes, ct);
        await stream.FlushAsync(ct);
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
    }
}

internal static class ApprovalToastManager
{
    private static readonly List<ApprovalToastForm> ActiveCards = new();
    private static readonly Dictionary<string, Task<ApprovalResponseDto>> PendingTasks = new();

    public const int CardWidth = 576;
    public const int ToolCardHeight = 382;
    public const int OAuthCardHeightKnown = 484;
    public const int OAuthCardHeightWarning = 556;
    public const int RightMargin = 14;
    public const int BottomMargin = 14;
    public const int Gap = 10;

    public static int PendingCount => ActiveCards.Count;

    public static event Action? PendingChanged;

    public const int ScreenshotCardHeight = 468;

    public static int ComputeCardHeight(ApprovalRequestDto req)
    {
        if (string.Equals(req.Kind, "oauth_consent", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(req.Warning) ? OAuthCardHeightKnown : OAuthCardHeightWarning;
        }
        if (string.Equals(req.Tool, "take_screenshot", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(req.PreviewImage))
        {
            return ScreenshotCardHeight;
        }
        return ToolCardHeight + ExtraBodyHeight(req, false);
    }

    internal static bool ShowsRisk(ApprovalRequestDto req) =>
        !string.Equals(req.Kind, "oauth_consent", StringComparison.OrdinalIgnoreCase) && req.Assessment != null
        && req.Assessment.Level is "medium" or "high" or "critical";

    internal static int PreviewLineCount(ApprovalRequestDto req) =>
        req.Preview == null || string.IsNullOrEmpty(req.Preview.Text) ? 0 : req.Preview.Text.Replace("\r", "").TrimEnd('\n').Split('\n').Length;

    /// <summary>Room for the v0.5 risk block and change preview, so they are visible without scrolling.</summary>
    internal static int ExtraBodyHeight(ApprovalRequestDto req, bool expanded)
    {
        if (string.Equals(req.Kind, "oauth_consent", StringComparison.OrdinalIgnoreCase) || req.Tool == "take_screenshot") return 0;
        int extra = 0;
        if (ShowsRisk(req))
        {
            var a = req.Assessment!;
            int reasons = Math.Min(a.Reasons?.Count ?? 0, expanded ? 6 : 3);
            extra += 56 + reasons * 21 + (string.IsNullOrWhiteSpace(a.Escalated) ? 0 : 24);
        }
        if (req.Preview != null) extra += 52 + Math.Max(1, Math.Min(PreviewLineCount(req), expanded ? 18 : 6)) * 17;
        return Math.Min(extra, expanded ? 440 : 320);
    }

    /// <summary>
    /// Height reserved at the bottom-right of the screen by active approval cards so
    /// informational notifications never overlap them (BUG-2).
    /// </summary>
    public static int GetReservedBottomHeight()
    {
        var visible = ActiveCards.Where(c => !c.IsLayoutHidden).ToArray();
        if (visible.Length == 0) return 0;
        var wa = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
        return Math.Max(0, wa.Bottom - visible.Min(c => c.Top) + Gap);
    }

    public static Task<ApprovalResponseDto> ShowApprovalAsync(
        ApprovalRequestDto req,
        int defaultGrantMinutes,
        string defaultGrantScope,
        string cardStyle,
        string approvalSound,
        Action<int, string> onDefaultGrantUpdated,
        Action<string>? onCardShown = null,
        bool isDemo = false)
    {
        string idKey = !string.IsNullOrWhiteSpace(req.Id) ? "id:" + req.Id : "";
        string hashKey = !string.IsNullOrWhiteSpace(req.ArgsHash) ? $"hash:{req.Tool}:{req.ArgsHash}" : "";

        // Deduplicate on tray side if an identical id or tool+argsHash is already pending (BUG-1)
        if (!isDemo)
        {
            if (!string.IsNullOrEmpty(idKey) && PendingTasks.TryGetValue(idKey, out var existingById))
            {
                onCardShown?.Invoke(DateTime.Now.ToString("HH:mm:ss.fff") + " (dedup-id)");
                return existingById;
            }
            if (!string.IsNullOrEmpty(hashKey) && PendingTasks.TryGetValue(hashKey, out var existingByHash))
            {
                onCardShown?.Invoke(DateTime.Now.ToString("HH:mm:ss.fff") + " (dedup-hash)");
                return existingByHash;
            }
        }

        var tcs = new TaskCompletionSource<ApprovalResponseDto>(TaskCreationOptions.RunContinuationsAsynchronously);

        int cardHeight = ComputeCardHeight(req);
        var wa = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
        int width = string.Equals(req.Kind, "oauth_consent", StringComparison.OrdinalIgnoreCase) ? 904 : CardWidth;
        int targetX = wa.Right - width - RightMargin;
        int bottomY = wa.Bottom - cardHeight - BottomMargin;

        var form = new ApprovalToastForm(
            req,
            defaultGrantMinutes,
            defaultGrantScope,
            cardStyle,
            onDefaultGrantUpdated,
            width,
            cardHeight,
            targetX,
            bottomY + 18,
            bottomY,
            res =>
            {
                if (!string.IsNullOrEmpty(idKey)) PendingTasks.Remove(idKey);
                if (!string.IsNullOrEmpty(hashKey)) PendingTasks.Remove(hashKey);
                tcs.TrySetResult(res);
            });

        if (!isDemo)
        {
            if (!string.IsNullOrEmpty(idKey)) PendingTasks[idKey] = tcs.Task;
            if (!string.IsNullOrEmpty(hashKey)) PendingTasks[hashKey] = tcs.Task;
        }

        form.FormClosed += (_, _) =>
        {
            if (!string.IsNullOrEmpty(idKey)) PendingTasks.Remove(idKey);
            if (!string.IsNullOrEmpty(hashKey)) PendingTasks.Remove(hashKey);
            ActiveCards.Remove(form);
            Reflow();
            PendingChanged?.Invoke();
        };

        ActiveCards.Add(form);
        Reflow();
        form.ShowCard();
        onCardShown?.Invoke(DateTime.Now.ToString("HH:mm:ss.fff"));

        // REQ-4: Sound plays ONCE when an approval or OAuth consent card is shown
        ApprovalSoundPlayer.Play(approvalSound);

        PendingChanged?.Invoke();
        return tcs.Task;
    }

    public static bool SimulateDecisionOnTopCard(
        string decision,
        int? grantMinutes,
        string? grantScope,
        int? lifetimeDays = null,
        bool hasLifetimeDays = false)
    {
        var card = ActiveCards.FirstOrDefault();
        if (card == null || card.IsDisposed) return false;
        if (hasLifetimeDays)
        {
            card.SetSelectedLifetimeDays(lifetimeDays);
        }
        card.ForceResolve(decision, grantMinutes, grantScope, "simulate");
        return true;
    }

    public static void UpdateAllCardsStyle(string newStyle)
    {
        foreach (var card in ActiveCards.ToArray())
        {
            try { card.SetCardStyle(newStyle); } catch { }
        }
    }

    public static void DismissAll(string byReason = "user")
    {
        foreach (var card in ActiveCards.ToArray())
        {
            try { card.ForceResolve("deny", null, null, byReason); } catch { }
        }
        ActiveCards.Clear();
        PendingTasks.Clear();
        PendingChanged?.Invoke();
    }

    public static void Reflow() => ReflowIn((Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea);
    internal static void ReflowIn(Rectangle wa)
    {
        int total = ActiveCards.Count, cursorBottom = wa.Bottom - BottomMargin, shown = 0;
        foreach (var card in ActiveCards) card.ConstrainTo(wa);
        // Oldest pending request stays reachable; overflow remains pending and still times out safely.
        for (int i = 0; i < total; i++)
        {
            var card = ActiveCards[i];
            bool visible = shown < 3 && cursorBottom - card.Height >= wa.Top + 8;
            if (visible) {
                int targetY = cursorBottom - card.Height;
                card.SetQueuePosition(i + 1, total, targetY);
                card.SetLayoutVisible(true);
                cursorBottom = targetY - Gap; shown++;
            } else { card.SetQueuePosition(i + 1, total, wa.Bottom); card.SetLayoutVisible(false); }
        }
        TrayToastManager.Reflow();
    }

}

internal sealed class ApprovalToastForm : Form
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize, IntPtr hdcSrc, ref Point pptSrc, uint crKey, [In] ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint ULW_ALPHA = 0x00000002;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;

    private readonly ApprovalRequestDto _req;
    private readonly bool _isOAuthConsent, _isScreenshot;
    private readonly Action<int, string> _onDefaultGrantUpdated;
    private readonly Action<ApprovalResponseDto> _onResolved;
    private readonly UiFrameTimer _animTimer;
    private readonly ToolTip _toolTip = new();
    private readonly UiAnimationClock _motion = new();
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private readonly int _timeoutSec;
    private int _grantMinutes, _selectedScopeIdx = 2, _selectedLifetimeIdx = 1;
    private string _grantScope, _cardStyle;
    private Image? _previewImage;
    private int _queueIndex = 1, _queueTotal = 1, _targetY, _rightEdge;
    private double _currentY, _alpha;
    private bool _resolved, _fadingOut, _expanded, _layoutVisible = true, _shown;
    private int _scrollY, _maxScroll, _focusIndex = -1, _lastSecond = -1;
    private PointF _mouse = new(-1, -1);
    private RectangleF _bodyViewport, _previewRect, _targetBoxRect, _btnDismissRect, _btnAllowRect, _btnGrantMainRect, _btnGrantArrowRect;
    private readonly RectangleF[] _scopeSegRects = new RectangleF[3], _lifetimeSegRects = new RectangleF[3];
    private readonly List<(RectangleF Rect, Action Action, string Label, string Tip)> _hits = new();
    private string _copyFeedback = "";
    private DateTime _copyUntil;
    private int _widthLimit = int.MaxValue, _heightLimit = int.MaxValue;
    private float ScaleFactor => Math.Max(1f, DeviceDpi / 96f);
    private float W => Width / ScaleFactor;
    private float H => Height / ScaleFactor;
    private RectangleF Card => new(8, 8, W - 16, H - 16);
    private bool Light => _cardStyle == "light";
    private bool Neon => _cardStyle == "neon";
    private Color Primary => Light ? Color.FromArgb(29, 36, 48) : Theme.TextPrimary;
    private Color Secondary => Light ? Color.FromArgb(83, 94, 112) : Theme.TextSecondary;
    private Color BorderColor => Light ? Color.FromArgb(32, 34, 45, 65) : Theme.BorderStrong;
    private Color BoxColor => Light ? Color.FromArgb(238, 241, 247) : Color.FromArgb(11, 14, 18);
    private Color AccentText => Light ? Color.FromArgb(66, 77, 206) : Color.FromArgb(183, 190, 255);
    private bool HasWarning => !string.IsNullOrWhiteSpace(_req.Warning) || (_isOAuthConsent && _req.Risk == "unknown_site");
    private string RiskLevel => _isOAuthConsent ? "low" : (_req.Assessment?.Level ?? "low");
    private bool RiskShown => ApprovalToastManager.ShowsRisk(_req);
    internal bool Grantable => _isOAuthConsent || _req.Assessment?.Grantable != false;
    private Color RiskColor => RiskLevel == "critical" ? Theme.Danger : RiskLevel == "high" ? Theme.Warning : Theme.Info;
    private Color RiskInk => RiskLevel == "critical" ? (Light ? Theme.LightDangerText : Theme.DangerText)
        : RiskLevel == "high" ? (Light ? Color.FromArgb(133, 82, 0) : Theme.WarningText)
        : (Light ? Theme.LightInfoText : Theme.InfoText);
    private static Font Ui(float px, bool strong = false) => PanelTypography.Get(px, strong);
    private double Remaining => Math.Max(0, _timeoutSec - (DateTime.UtcNow - _startedAt).TotalSeconds);
    internal bool IsExpanded => _expanded;
    internal bool IsLayoutHidden => !_layoutVisible;
    internal string CopyPayload => _req.Summary?.Target ?? "";
    internal int LayeredFrameVersion { get; private set; }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x8 | 0x80000; return cp; }
    }

    public ApprovalToastForm(ApprovalRequestDto req, int defaultGrantMinutes, string defaultGrantScope,
        string cardStyle, Action<int, string> onDefaultGrantUpdated, int width, int height, int x,
        int startY, int targetY, Action<ApprovalResponseDto> onResolved)
    {
        _animTimer = new UiFrameTimer(this);
        _req = req; _isOAuthConsent = string.Equals(req.Kind, "oauth_consent", StringComparison.OrdinalIgnoreCase);
        _isScreenshot = !_isOAuthConsent && req.Tool == "take_screenshot";
        _grantMinutes = defaultGrantMinutes > 0 ? defaultGrantMinutes : 10;
        _grantScope = _isScreenshot ? "tool" : string.IsNullOrWhiteSpace(defaultGrantScope) ? "tool" : defaultGrantScope;
        _cardStyle = string.IsNullOrWhiteSpace(cardStyle) ? "glass" : cardStyle.ToLowerInvariant();
        _onDefaultGrantUpdated = onDefaultGrantUpdated; _onResolved = onResolved;
        _timeoutSec = req.TimeoutSec > 0 ? req.TimeoutSec : _isOAuthConsent ? 120 : 60;
        // Preserve existing default scope and lifetime; visual redesign must not grant new rights.
        _selectedScopeIdx = (req.Scope ?? "").ToLowerInvariant() switch { "read" => 0, "readwrite" => 1, _ => 2 };
        _targetY = targetY; _currentY = startY; _rightEdge = x + width;
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None; ShowInTaskbar = false; TopMost = true; KeyPreview = true;
        Size = new Size(width, height); Location = new Point(x, startY);
        AccessibleName = _isOAuthConsent ? I18n.T("OAuth connection approval", "Подтверждение подключения OAuth") : Heading;
        if (_isScreenshot && !string.IsNullOrWhiteSpace(req.PreviewImage))
        {
            try { using var ms = new MemoryStream(Convert.FromBase64String(req.PreviewImage)); using var im = Image.FromStream(ms); _previewImage = new Bitmap(im); } catch { _previewImage = null; }
        }
        MouseMove += (_, e) => { _mouse = new(e.X / ScaleFactor, e.Y / ScaleFactor); UpdateTip(); RenderLayered(); };
        MouseLeave += (_, _) => { _mouse = new(-1, -1); _toolTip.SetToolTip(this, null); RenderLayered(); };
        MouseClick += (_, e) => { if (e.Button != MouseButtons.Left || _resolved) return; EnsureLayout(); var pt = new PointF(e.X / ScaleFactor, e.Y / ScaleFactor); var hit = _hits.FirstOrDefault(h => h.Rect.Contains(pt)); hit.Action?.Invoke(); };
        MouseWheel += (_, e) => { if (_maxScroll > 0) { _scrollY = Math.Clamp(_scrollY - Math.Sign(e.Delta) * 48, 0, _maxScroll); RenderLayered(); } };
        KeyDown += OnCardKeyDown;
        FormClosing += (_, _) => { if (!_resolved) ForceResolve("deny", null, null, "user"); };
        _animTimer.Tick += (_, _) => OnAnimTick();
    }
    private string Heading => _isScreenshot ? I18n.T("Confirm screenshot sharing", "Подтвердите отправку скриншота") : _req.Tool switch
    {
        "run_command" => I18n.T("Confirm command", "Подтвердите команду"),
        "read_file" => I18n.T("Confirm file reading", "Подтвердите чтение файла"),
        "write_file" => I18n.T("Confirm file writing", "Подтвердите запись файла"),
        "edit_file" => I18n.T("Confirm file changes", "Подтвердите изменение файла"),
        "file_ops" => I18n.T("Confirm file operation", "Подтвердите операцию с файлами"),
        "read_image" => I18n.T("Confirm image viewing", "Подтвердите просмотр изображения"),
        _ => I18n.T("Confirm action", "Подтвердите действие"),
    };
    private string Purpose => !string.IsNullOrWhiteSpace(_req.Note) ? _req.Note.Trim() : I18n.T("An AI agent requests permission to perform this action.", "AI-агент запрашивает разрешение на это действие.");
    public static string MiddleTruncate(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string s = text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (s.Length <= maxChars || maxChars < 3) return s;
        int n = (maxChars - 1) / 2; return s[..n] + "…" + s[^(maxChars - 1 - n)..];
    }
    public void SetSelectedLifetimeDays(int? days) { _selectedLifetimeIdx = days == 1 ? 0 : days == 30 ? 1 : days == null ? 2 : 1; RenderLayered(); }
    private int? GetSelectedLifetimeDays() => _selectedLifetimeIdx == 0 ? 1 : _selectedLifetimeIdx == 1 ? 30 : null;
    private string GetSelectedOAuthScope() => _selectedScopeIdx == 0 ? "read" : _selectedScopeIdx == 1 ? "readwrite" : "full";
    public void SetSelectedOAuthScope(string? scope) { _selectedScopeIdx = scope == "read" ? 0 : scope == "readwrite" ? 1 : 2; RenderLayered(); }
    public void SetCardStyle(string style) { _cardStyle = string.IsNullOrWhiteSpace(style) ? "glass" : style.ToLowerInvariant(); RenderLayered(); }
    internal void SetExpanded(bool value) { if (_isOAuthConsent) return; _expanded = value; _scrollY = 0; _focusIndex = -1; ResizeForWorkArea(); ApprovalToastManager.Reflow(); RenderLayered(); }
    internal void ConstrainTo(Rectangle wa)
    {
        _widthLimit = Math.Max(240, wa.Width - 28); _heightLimit = Math.Max(240, wa.Height - 28);
        ResizeForWorkArea(); _rightEdge = wa.Right - ApprovalToastManager.RightMargin;
    }
    private void ResizeForWorkArea()
    {
        int width = _isOAuthConsent ? 904 : _expanded ? 736 : 576;
        int height = _isOAuthConsent ? (HasWarning ? 556 : 484) : _expanded ? (_isScreenshot ? 604 : 566) : _isScreenshot ? 468 : 382;
        height += ApprovalToastManager.ExtraBodyHeight(_req, _expanded);
        width = Math.Min((int)(width * ScaleFactor), _widthLimit);
        if (_isOAuthConsent && width / ScaleFactor < 740) height += 248;
        else if (!_isOAuthConsent && width / ScaleFactor < 440) height += 100;
        Size = new Size(width, Math.Min((int)(height * ScaleFactor), _heightLimit));
    }
    public void ShowCard() { _shown = true; _motion.Step(); _motion.SetActive(true); if (_layoutVisible) { Show(); RenderLayered(); } _animTimer.Start(); }
    public void SetQueuePosition(int idx, int total, int targetY)
    {
        _queueIndex = idx; _queueTotal = total; _targetY = targetY;
        _currentY = targetY; Location = new Point(_rightEdge - Width, targetY);
        if (_shown) RenderLayered();
    }
    internal void SetLayoutVisible(bool visible)
    {
        if (_layoutVisible == visible) return; _layoutVisible = visible;
        if (!visible) Hide(); else if (_shown && !_fadingOut) { _currentY = _targetY; Location = new Point(_rightEdge - Width, _targetY); Show(); RenderLayered(); }
    }
    private void OnCardKeyDown(object? sender, KeyEventArgs e)
    {
        EnsureLayout();
        if (e.KeyCode == Keys.Escape) { ForceResolve("deny", null, null, "user"); e.Handled = true; }
        else if (e.KeyCode == Keys.Tab && _hits.Count > 0) { _focusIndex = (_focusIndex + (e.Shift ? _hits.Count - 1 : 1)) % _hits.Count; RenderLayered(); e.Handled = true; e.SuppressKeyPress = true; }
        else if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && _focusIndex >= 0 && _focusIndex < _hits.Count) { _hits[_focusIndex].Action(); e.Handled = true; }
        else if (e.KeyCode == Keys.PageDown || e.KeyCode == Keys.PageUp) { _scrollY = Math.Clamp(_scrollY + (e.KeyCode == Keys.PageDown ? 160 : -160), 0, _maxScroll); RenderLayered(); e.Handled = true; }
    }
    private void UpdateTip()
    {
        var hit = _hits.FirstOrDefault(h => h.Rect.Contains(_mouse)); Cursor = hit.Action != null ? Cursors.Hand : Cursors.Default;
        _toolTip.SetToolTip(this, hit.Tip ?? (_targetBoxRect.Contains(_mouse) ? CopyPayload : null));
    }
    private void EnsureLayout() { using var b = new Bitmap(1, 1); using var g = Graphics.FromImage(b); RenderToGraphics(g); }
    private void OnAnimTick()
    {
        if (IsDisposed) return;
        if (!_resolved && Remaining <= 0) ForceResolve("deny", null, null, "timeout");
        double dt = _motion.Step();
        _motion.SetActive(_layoutVisible && (_alpha < 1 || _fadingOut || Math.Abs(_targetY - _currentY) > .2));
        _alpha = _fadingOut ? _alpha - dt / .20 : Math.Min(1, _alpha + dt / .18);
        if (_fadingOut && _alpha <= .02) { _animTimer.Stop(); Close(); Dispose(); return; }
        double oldY = _currentY; _currentY = UiAnimationClock.Follow(_currentY, _targetY, dt);
        if (Math.Abs(_targetY - _currentY) < .4) _currentY = _targetY;
        int second = (int)Math.Ceiling(Remaining);
        if (Math.Abs(oldY - _currentY) > .1 || second != _lastSecond || _alpha < 1 || _fadingOut || (_copyFeedback.Length > 0 && DateTime.UtcNow > _copyUntil))
        {
            if (DateTime.UtcNow > _copyUntil) _copyFeedback = "";
            _lastSecond = second; Location = new Point(_rightEdge - Width, (int)_currentY); RenderLayered();
        }
    }
    private void AddHit(RectangleF r, Action a, string label, string? tip = null, bool body = false)
    {
        if (body) r.Intersect(_bodyViewport);
        if (r.Width > 1 && r.Height > 1) _hits.Add((r, a, label, tip ?? label));
    }
    private void UiText(Graphics g, string s, Font f, Color c, RectangleF r, bool center = false) => Theme.DrawText(g, s, f, c, r, center ? StringAlignment.Center : StringAlignment.Near, StringAlignment.Center);
    private void Line(Graphics g, float x1, float y, float x2) { using var p = new Pen(BorderColor); g.DrawLine(p, x1, y, x2, y); }
    private void IconButton(Graphics g, RectangleF r, IconKind icon, string label, Action action, bool body = false)
    {
        if (r.Contains(_mouse)) Theme.FillRound(g, r, 8, Theme.WithAlpha(Theme.Accent, 24));
        Theme.DrawIcon(g, icon, RectangleF.Inflate(r, -8, -8), Secondary, 1.7f); AddHit(r, action, label, body: body);
    }
    private void Button(Graphics g, RectangleF r, string label, ButtonKind kind, Action action, IconKind? icon = null)
    {
        var st = r.Contains(_mouse) ? ButtonState.Hover : ButtonState.Normal;
        if (!Light) Theme.DrawButton(g, r, label, kind, st, font: Ui(13, true), radius: 11, icon: icon);
        else {
            Color bg = kind == ButtonKind.Primary ? Theme.Accent : kind == ButtonKind.Danger ? Theme.LightDangerText : kind == ButtonKind.Outline ? Color.FromArgb(241, 241, 255) : Color.FromArgb(234, 237, 244);
            Theme.FillRound(g, r, 11, bg); Theme.StrokeRound(g, r, 11, kind == ButtonKind.Outline ? Theme.Accent : BorderColor);
            UiText(g, label, Ui(13, true), kind is ButtonKind.Primary or ButtonKind.Danger ? Color.White : kind == ButtonKind.Outline ? AccentText : Primary, RectangleF.Inflate(r, -4, 0), true);
        }
        AddHit(r, action, label);
    }
    private static List<string> Wrap(Graphics g, string raw, Font font, float width)
    {
        var lines = new List<string>();
        foreach (string part in raw.Replace("\r", "").Split('\n'))
        {
            string line = ""; var en = System.Globalization.StringInfo.GetTextElementEnumerator(part);
            while (en.MoveNext())
            {
                string ch = en.GetTextElement();
                if (line.Length > 0 && Theme.MeasureText(g, line + ch, font).Width > Math.Max(12, width)) {
                    int space = line.LastIndexOf(' ');
                    if (space > 0) { lines.Add(line[..space]); line = line[(space + 1)..]; }
                    else { lines.Add(line); line = ""; }
                }
                line += ch;
            }
            lines.Add(line);
        }
        return lines;
    }
    private float Paragraph(Graphics g, string s, float x, float y, float width, Font font, Color color, float lineHeight = 20)
    {
        var lines = Wrap(g, s, font, width);
        foreach (var line in lines) { UiText(g, line, font, color, new(x, y, width, lineHeight)); y += lineHeight; }
        return y;
    }
    private float LabelValue(Graphics g, string label, string value, float x, float y, float width, bool mono = false)
    {
        if (width < 410) {
            UiText(g, label, Ui(12), Secondary, new(x, y, width, 20));
            return Paragraph(g, value, x, y + 22, width, mono ? Theme.FontMono(10) : Ui(13), Primary) + 12;
        }
        float labelWidth = Math.Min(186, width * .29f);
        UiText(g, label, Ui(13), Secondary, new(x, y, labelWidth - 8, 24));
        return Math.Max(y + 24, Paragraph(g, value, x + labelWidth, y, width - labelWidth, mono ? Theme.FontMono(10) : Ui(13), Primary)) + 8;
    }
    private float Warning(Graphics g, string message, float x, float y, float width)
    {
        var lines = Wrap(g, message, Ui(12.5f), width - 48); float height = Math.Max(44, 16 + lines.Count * 19);
        Color ink = Light ? Color.FromArgb(133, 82, 0) : Color.FromArgb(255, 203, 119);
        Theme.FillRound(g, new(x, y, width, height), 11, Theme.WithAlpha(Theme.Warning, Light ? 25 : 22));
        Theme.StrokeRound(g, new(x, y, width, height), 11, Theme.WithAlpha(Theme.Warning, 90));
        Theme.DrawIcon(g, IconKind.Warning, new(x + 12, y + 13, 18, 18), ink, 1.6f);
        Paragraph(g, message, x + 40, y + 8, width - 48, Ui(12.5f), ink, 19);
        return y + height + 14;
    }
    // v0.5: the server's risk reading in plain words: headline, up to six reasons and why the card appeared.
    private float RiskBlock(Graphics g, float x, float y, float width)
    {
        var a = _req.Assessment!; Color c = RiskColor, ink = RiskInk;
        string head = !string.IsNullOrWhiteSpace(a.Headline) ? a.Headline.Trim() : RiskLevel switch
        {
            "critical" => I18n.T("Critical risk: check every word", "Критический риск: проверьте каждое слово"),
            "high" => I18n.T("High risk: read before allowing", "Высокий риск: прочитайте перед разрешением"),
            _ => I18n.T("Worth a look", "Стоит проверить"),
        };
        var font = Ui(12.5f);
        var rows = new List<(string Text, bool Bullet)>();
        foreach (string reason in (a.Reasons ?? new()).Where(s => !string.IsNullOrWhiteSpace(s)).Take(_expanded ? 6 : 3))
        {
            var wrapped = Wrap(g, reason.Trim(), font, width - 66);
            for (int i = 0; i < wrapped.Count; i++) rows.Add((wrapped[i], i == 0));
        }
        string why = a.Escalated switch
        {
            "rule" => I18n.T("This tool normally runs without asking; the card appeared because of the risk.", "Обычно этот инструмент работает без вопроса; карточка появилась из-за риска."),
            "grant" => I18n.T("Your temporary grant does not cover critical actions.", "Временный доступ не распространяется на критичные действия."),
            "redflag" => I18n.T("Matches a dangerous-command rule from your settings.", "Совпадает с правилом опасных команд из ваших настроек."),
            _ => "",
        };
        var whyLines = why.Length > 0 ? Wrap(g, why, Ui(11.5f), width - 52) : new List<string>();
        float height = 40 + rows.Count * 19 + whyLines.Count * 18 + (rows.Count > 0 || whyLines.Count > 0 ? 8 : -6);
        var box = new RectangleF(x, y, width, height);
        Theme.FillRound(g, box, 11, Theme.WithAlpha(c, Light ? 26 : RiskLevel == "critical" ? 30 : 22));
        Theme.StrokeRound(g, box, 11, Theme.WithAlpha(c, RiskLevel == "critical" ? 160 : 110), RiskLevel == "critical" ? 1.4f : 1f);
        Theme.DrawIcon(g, RiskLevel == "medium" ? IconKind.Info : IconKind.Warning, new(x + 12, y + 11, 18, 18), ink, 1.6f);
        UiText(g, head, Ui(13, true), ink, new(x + 40, y + 8, width - 52, 22));
        float ly = y + 34;
        foreach (var (text, bullet) in rows)
        {
            if (bullet) Theme.FillRound(g, new(x + 44, ly + 8, 4, 4), 2, ink);
            UiText(g, text, font, Primary, new(x + 56, ly, width - 64, 19)); ly += 19;
        }
        if (whyLines.Count > 0) { ly += 2; foreach (string line in whyLines) { UiText(g, line, Ui(11.5f), Secondary, new(x + 40, ly, width - 52, 18)); ly += 18; } }
        return y + height + 12;
    }
    // v0.5: what the write/edit changes, with +/- counts and coloured lines of the compact diff.
    private float DiffBlock(Graphics g, float x, float y, float width)
    {
        var p = _req.Preview!;
        var all = (p.Text ?? "").Replace("\r", "").TrimEnd('\n').Split('\n').Where(l => l.Length > 0).ToList();
        int limit = _expanded ? 18 : 6;
        var shown = all.Take(limit).ToList();
        float lineH = 17, height = 38 + Math.Max(1, shown.Count) * lineH + (all.Count > limit ? 22 : 0) + 8;
        var box = new RectangleF(x, y, width, height);
        Theme.FillRound(g, box, 10, BoxColor); Theme.StrokeRound(g, box, 10, BorderColor);
        string title = p.Exists ? I18n.T("Changes", "Изменения") : I18n.T("New file", "Новый файл");
        UiText(g, title, Ui(12.5f, true), Primary, new(x + 12, y + 7, width * .5f, 22));
        Color addInk = Light ? Theme.LightSuccessText : Theme.SuccessText, delInk = Light ? Theme.LightDangerText : Theme.DangerText;
        string plus = "+" + p.Added, minus = "−" + p.Removed;
        float pw = Theme.MeasureText(g, plus, Ui(12, true)).Width + 16, mw = Theme.MeasureText(g, minus, Ui(12, true)).Width + 16;
        var mr = new RectangleF(x + width - 12 - mw, y + 8, mw, 20); var pr = new RectangleF(mr.X - 6 - pw, y + 8, pw, 20);
        Theme.FillRound(g, pr, 10, Theme.WithAlpha(Theme.Success, 34)); UiText(g, plus, Ui(12, true), addInk, pr, true);
        if (p.Exists) { Theme.FillRound(g, mr, 10, Theme.WithAlpha(Theme.Danger, 34)); UiText(g, minus, Ui(12, true), delInk, mr, true); }
        var mono = Theme.FontMono(9.5f);
        float ly = y + 34;
        if (shown.Count == 0) UiText(g, I18n.T("No visible text changes", "Видимых изменений текста нет"), Ui(12), Secondary, new(x + 12, ly, width - 24, lineH));
        foreach (string raw in shown)
        {
            char k = raw[0];
            Color ink = k == '+' ? addInk : k == '-' ? delInk : Secondary;
            if (k == '+' || k == '-') Theme.FillRound(g, new(x + 6, ly, width - 12, lineH), 3, Theme.WithAlpha(k == '+' ? Theme.Success : Theme.Danger, Light ? 22 : 18));
            string text = raw.Replace("\t", "    ");
            float max = width - 28;
            if (Theme.MeasureText(g, text, mono).Width > max) { while (text.Length > 1 && Theme.MeasureText(g, text + "…", mono).Width > max) text = text[..^1]; text += "…"; }
            UiText(g, text, mono, ink, new(x + 12, ly, width - 24, lineH)); ly += lineH;
        }
        if (all.Count > limit)
        {
            var more = new RectangleF(x + 12, ly + 2, width - 24, 20);
            string label = _expanded ? I18n.Format("…and {0} more lines in the full diff", "…и ещё строк: {0}", all.Count - limit) : I18n.Format("Show {0} more lines", "Показать ещё строк: {0}", all.Count - limit);
            UiText(g, label, Ui(12, true), AccentText, more);
            if (!_expanded) AddHit(more, () => SetExpanded(true), label, body: true);
        }
        return y + height + 12;
    }
    private float Code(Graphics g, float x, float y, float width)
    {
        string payload = CopyPayload; if (payload.Length == 0) payload = I18n.T("Target not provided", "Цель не указана");
        var font = _req.Tool == "run_command" ? Theme.FontMono(10) : Ui(13);
        var lines = Wrap(g, payload, font, width - 58);
        int visible = _expanded ? lines.Count : Math.Min(2, lines.Count);
        float height = 20 + visible * 20;
        _targetBoxRect = new(x, y, width, height);
        Theme.FillRound(g, _targetBoxRect, 10, BoxColor); Theme.StrokeRound(g, _targetBoxRect, 10, BorderColor);
        for (int i = 0; i < visible; i++) {
            string line = lines[i]; if (!_expanded && i == visible - 1 && lines.Count > visible) line += "…";
            UiText(g, line, font, Primary, new(x + 13, y + 10 + i * 20, width - 58, 20));
        }
        IconButton(g, new(x + width - 38, y + 6, 32, 32), IconKind.Copy, I18n.T("Copy command or path", "Копировать команду или путь"), CopyTarget, body: true);
        if (!_expanded && lines.Count > 2) {
            var r = new RectangleF(x, y + height + 4, width, 24); UiText(g, I18n.T("Show full request", "Показать запрос целиком"), Ui(12, true), AccentText, r);
            AddHit(r, () => SetExpanded(true), I18n.T("Show full request", "Показать запрос целиком"), body: true); height += 28;
        }
        return y + height + 12;
    }
    private void CopyTarget()
    {
        try { Clipboard.SetText(CopyPayload); _copyFeedback = I18n.T("Copied", "Скопировано"); }
        catch { _copyFeedback = I18n.T("Could not copy", "Не удалось скопировать"); }
        _copyUntil = DateTime.UtcNow.AddSeconds(2); RenderLayered();
    }
    public void RenderToGraphics(Graphics g)
    {
        var state = g.Save(); g.ScaleTransform(ScaleFactor, ScaleFactor);
        g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        _hits.Clear(); _previewRect = RectangleF.Empty;
        var r = Card;
        Theme.DrawShadow(g, r, 16, 6, 80, 2);
        if (Neon) Theme.DrawGlow(g, r, 16, Theme.WithAlpha(Theme.Accent, 110), 5, 1);
        Theme.FillRoundGradient(g, r, 16, Light ? Color.White : Theme.BgGradTop, Light ? Color.FromArgb(246, 248, 252) : Theme.BgGradBottom);
        Theme.StrokeRound(g, r, 16, HasWarning ? Theme.WithAlpha(Theme.Warning, 130) : RiskLevel == "critical" ? Theme.WithAlpha(Theme.Danger, 180) : RiskLevel == "high" ? Theme.WithAlpha(Theme.Warning, 140) : Neon ? Theme.Accent : BorderColor, RiskLevel == "critical" ? 1.6f : 1f);
        var clipState = g.Save(); using (var path = Theme.RoundRect(r, 16)) g.SetClip(path, CombineMode.Intersect);
        float headerBottom = DrawHeader(g, r);
        float footerHeight = W < 440 ? (_isOAuthConsent ? 130 : 114) : 74;
        float footerTop = r.Bottom - footerHeight;
        _bodyViewport = new RectangleF(r.X + 1, headerBottom, r.Width - 2, Math.Max(20, footerTop - headerBottom));
        _scrollY = Math.Clamp(_scrollY, 0, _maxScroll);
        var bodyState = g.Save(); g.SetClip(_bodyViewport, CombineMode.Intersect);
        float contentEnd = _isOAuthConsent ? DrawOAuth(g, r.X + 20, headerBottom + 20 - _scrollY, r.Width - 40) : DrawTool(g, r.X + 24, headerBottom + 18 - _scrollY, r.Width - 48);
        g.Restore(bodyState);
        _maxScroll = Math.Max(0, (int)Math.Ceiling(contentEnd + _scrollY - footerTop + 8));
        if (_maxScroll > 0) {
            float sh = Math.Max(24, _bodyViewport.Height * _bodyViewport.Height / (_bodyViewport.Height + _maxScroll));
            float sy = _bodyViewport.Y + (_bodyViewport.Height - sh) * Math.Clamp(_scrollY / (float)_maxScroll, 0, 1);
            Theme.FillRound(g, new(r.Right - 5, sy, 2, sh), 1, Theme.WithAlpha(Secondary, 160));
        }
        DrawFooter(g, r, footerTop);
        if (_copyFeedback.Length > 0) UiText(g, _copyFeedback, Ui(11, true), AccentText, new(r.X + 24, footerTop - 22, r.Width - 48, 20));
        float progress = (float)(Remaining / _timeoutSec);
        using(var p = Theme.RoundRect(new RectangleF(r.X, r.Bottom - 3, r.Width, 3), 0)) {
            using var b = new SolidBrush(Theme.WithAlpha(Remaining <= 10 ? Theme.Danger : Theme.Accent, 180));
            g.FillRectangle(b, r.X, r.Bottom - 3, r.Width * progress, 3);
        }
        if (_focusIndex >= 0 && _focusIndex < _hits.Count) { var h = _hits[_focusIndex].Rect; Theme.StrokeRound(g, RectangleF.Inflate(h, 2, 2), 10, Theme.Accent, 2); }
        g.Restore(clipState); g.Restore(state);
    }
    private float DrawHeader(Graphics g, RectangleF r)
    {
        bool brand = _expanded && !_isOAuthConsent;
        float top = r.Y;
        if (brand) {
            Theme.DrawLogo(g, new RectangleF(r.X + 18, r.Y + 14, 28, 28));
            UiText(g, Theme.ProductName, Ui(15, true), Primary, new(r.X + 54, r.Y + 10, 230, 36));
            top += 54; Line(g, r.X, top, r.Right);
        }
        float timerW = 70;
        var close = new RectangleF(r.Right - 42, r.Y + 10, 32, 32);
        IconButton(g, close, IconKind.Close, I18n.T("Deny and close", "Отклонить и закрыть"), () => ForceResolve("deny", null, null, "user"));
        {
            var timer = new RectangleF(close.X - timerW - 10, r.Y + 12, timerW, 28);
            Theme.FillRound(g, timer, 14, Theme.WithAlpha(Theme.Warning, 25)); Theme.StrokeRound(g, timer, 14, Theme.WithAlpha(Theme.Warning, 90));
            Theme.DrawIcon(g, IconKind.Clock, new(timer.X + 8, timer.Y + 7, 14, 14), Light ? Color.FromArgb(133, 82, 0) : Theme.Warning, 1.5f);
            UiText(g, ((int)Math.Ceiling(Remaining)) + I18n.T(" s", " с"), Ui(12, true), Light ? Color.FromArgb(133, 82, 0) : Theme.Warning, new(timer.X + 24, timer.Y, timerW - 26, 28), true);
        }
        float hx = r.X + 24, hy = top + (W < 440 ? 54 : 20), tile = _isOAuthConsent ? 34 : 46;
        IconKind ik = _isOAuthConsent ? IconKind.Link : _isScreenshot ? IconKind.Eye : _req.Tool == "run_command" ? IconKind.Terminal : _req.Tool == "file_ops" ? IconKind.Folder : _req.Tool == "read_image" ? IconKind.Image : IconKind.File;
        Color tint = _isOAuthConsent || _isScreenshot ? Theme.Teal : RiskLevel == "critical" ? Theme.Danger : RiskLevel == "high" ? Theme.Warning : Theme.Violet;
        Theme.DrawIconTile(g, new(hx, hy, tile, tile), ik, tint);
        float tx = hx + tile + 16, tw = r.Right - tx - (W < 440 ? 24 : brand ? 48 : 158);
        string title = _isOAuthConsent ? I18n.T("OAuth 2.1 connection", "Подключение OAuth 2.1") : Heading;
        float titleHeight = 30;
        if (W < 440 || (!_isOAuthConsent && !brand && Theme.MeasureText(g, title, Ui(20, true)).Width > tw)) {
            var titleFont = Ui(W < 440 ? 16 : 18, true);
            var titleLines = Wrap(g, title, titleFont, Math.Max(60, tw));
            int limit = W < 440 ? 3 : 2;
            for (int i = 0; i < Math.Min(limit, titleLines.Count); i++) UiText(g, titleLines[i], titleFont, Primary, new(tx, hy - 3 + i * 23, Math.Max(60, tw), 23));
            titleHeight = Math.Min(limit, titleLines.Count) * 23;
        } else UiText(g, title, Ui(_isOAuthConsent ? 17 : 20, true), Primary, new(tx, hy - 3, Math.Max(60, tw), 30));
        string subtitle = _isOAuthConsent ? I18n.T("An application requests access to this PC.", "Приложение запрашивает доступ к этому ПК.") : _isScreenshot ? I18n.T("The image will leave this PC and be shared with the model.", "Изображение покинет ПК и будет передано модели.") : Purpose;
        var subtitleLines = Wrap(g, subtitle, Ui(13), Math.Max(60, r.Right - tx - 24));
        int maxLines = _expanded ? 3 : 2;
        int count = Math.Min(maxLines, subtitleLines.Count);
        for (int i = 0; i < count; i++) UiText(g, subtitleLines[i] + (i == count - 1 && subtitleLines.Count > count ? "…" : ""), Ui(13), Secondary, new(tx, hy + titleHeight + i * 20, Math.Max(60, r.Right - tx - 24), 20));
        float bottom = hy + titleHeight + count * 20 + 16;
        if (!_expanded && !_isOAuthConsent) {
            // Keep the compact header finite; the complete purpose is repeated in the scrollable body when long.
            bottom = Math.Min(bottom, top + (W < 440 ? 190 : 144));
        }
        if (_queueTotal > 1) { UiText(g, I18n.Format("Request {0} of {1}", "Запрос {0} из {1}", _queueIndex, _queueTotal), Ui(11), AccentText, new(hx, bottom - 3, r.Width - 48, 20)); bottom += 22; }
        Line(g, r.X, bottom, r.Right); return bottom;
    }
    private float DrawTool(Graphics g, float x, float y, float width)
    {
        if (_isScreenshot) {
            float pw = _expanded && width > 560 ? width * .72f : width;
            float ph = _expanded ? 248 : 166;
            _previewRect = new(x, y, pw, ph);
            Theme.FillRound(g, _previewRect, 13, BoxColor); Theme.StrokeRound(g, _previewRect, 13, BorderColor);
            if (_previewImage != null) {
                var st = g.Save(); using var path = Theme.RoundRect(RectangleF.Inflate(_previewRect, -2, -2), 12); g.SetClip(path, CombineMode.Intersect);
                float scale = Math.Min((pw - 20) / _previewImage.Width, (ph - 20) / _previewImage.Height);
                float iw = _previewImage.Width * scale, ih = _previewImage.Height * scale;
                g.DrawImage(_previewImage, x + (pw - iw) / 2, y + (ph - ih) / 2, iw, ih); g.Restore(st);
                AddHit(_previewRect, OpenScreenshotViewer, I18n.T("Open exact screenshot preview", "Открыть предпросмотр отправляемого скриншота"), body: true);
            } else UiText(g, I18n.T("Preview unavailable", "Предпросмотр недоступен"), Ui(14), Secondary, _previewRect, true);
            if (pw < width) {
                var side = new RectangleF(x + pw + 14, y, width - pw - 14, ph); Theme.FillRound(g, side, 13, Light ? Color.FromArgb(237, 240, 246) : Theme.Surface);
                Theme.DrawIconTile(g, new(side.X + 16, side.Y + 16, 32, 32), IconKind.Eye, Theme.Teal);
                Paragraph(g, I18n.T("Will be shared with the model", "Будет отправлено модели"), side.X + 16, side.Y + 64, side.Width - 32, Ui(13), Secondary);
            }
            y += ph + 12;
            string previewHint = _previewImage == null
                ? I18n.T("The image to be sent cannot be inspected in this card.", "Отправляемое изображение нельзя проверить в этой карточке.")
                : string.IsNullOrWhiteSpace(_req.PreviewHint) ? I18n.T("This is the image that will be sent. Click to inspect before allowing.", "Это изображение будет отправлено. Нажмите, чтобы проверить его перед разрешением.") : _req.PreviewHint!;
            y = Paragraph(g, previewHint, x, y, width, Ui(12), Secondary, 19) + 12;
            if (_previewImage == null) y = Warning(g, I18n.T("The screenshot preview could not be loaded. Verify the source before allowing.", "Не удалось загрузить предпросмотр скриншота. Проверьте источник перед разрешением."), x, y, width);
            if (_expanded) y = LabelValue(g, I18n.T("Description", "Описание"), !string.IsNullOrWhiteSpace(_req.Description) ? _req.Description! : Purpose, x, y, width);
        } else {
            if (RiskShown) y = RiskBlock(g, x, y, width);
            string client = !string.IsNullOrWhiteSpace(_req.Agent) ? _req.Agent!.Trim() : string.IsNullOrWhiteSpace(_req.ClientName) ? I18n.T("AI agent · source not specified", "AI-агент · источник не указан") : _req.ClientName;
            y = LabelValue(g, I18n.T("Requested by", "Запрашивает"), client, x, y, width);
            string targetLabel = _req.Tool == "run_command" ? I18n.T("Command", "Команда") : I18n.T("Target", "Цель");
            if (width >= 410) {
                float labelWidth = Math.Min(186, width * .29f);
                UiText(g, targetLabel, Ui(13), Secondary, new(x, y + 8, labelWidth - 8, 24));
                y = Code(g, x + labelWidth, y, width - labelWidth);
            } else {
                UiText(g, targetLabel, Ui(12), Secondary, new(x, y, width, 20));
                y = Code(g, x, y + 25, width);
            }
            if (_req.Preview != null) y = DiffBlock(g, x, y, width);
            if (_expanded || Purpose.Length > 120) y = LabelValue(g, I18n.T("Purpose", "Цель запроса"), Purpose, x, y, width);
            if (!string.IsNullOrWhiteSpace(_req.Summary?.Detail)) y = LabelValue(g, I18n.T("Details", "Подробности"), _req.Summary.Detail, x, y, width);
        }
        if (_expanded || !_isScreenshot) {
            string grantText = !Grantable
                ? I18n.T("Not offered: critical actions are approved one at a time", "Не предлагается: критичные действия подтверждаются по одному")
                : _grantScope == "exact" ? I18n.T("Only this command or file", "Только эта команда или файл") : I18n.Format("For this tool: {0}", "Для инструмента: {0}", _req.Tool);
            y = LabelValue(g, I18n.T("Temporary grant", "Временный доступ"), grantText, x, y, width);
            y = LabelValue(g, I18n.T("Time remaining", "Осталось времени"), TimeSpan.FromSeconds(Math.Ceiling(Remaining)).ToString(@"m\:ss"), x, y, width);
        }
        if (!string.IsNullOrWhiteSpace(_req.Warning)) y = Warning(g, _req.Warning!, x, y, width);
        else if (_expanded && !_isScreenshot) y = Warning(g, _req.Tool == "read_file" ? I18n.T("The file contents may be sent to the model. Only allow requests from a trusted source.", "Содержимое файла может быть передано модели. Разрешайте запросы только доверенному источнику.") : I18n.T("This action may change files or settings. Only allow requests from a trusted source.", "Действие может изменить файлы или настройки. Разрешайте запросы только доверенному источнику."), x, y, width);
        return y;
    }
    private float DrawOAuth(Graphics g, float x, float y, float width)
    {
        bool columns = width >= 700; float leftW = columns ? 268 : width;
        float startY = y;
        Theme.DrawIconTile(g, new(x, y, 44, 44), IconKind.Link, Theme.Teal);
        string name = string.IsNullOrWhiteSpace(_req.ClientName) ? I18n.T("OAuth client", "OAuth-клиент") : _req.ClientName.Trim();
        UiText(g, name, Ui(18, true), Primary, new(x + 58, y, leftW - 64, 26));
        string host = string.IsNullOrWhiteSpace(_req.RedirectHost) ? I18n.T("Domain not specified", "Домен не указан") : _req.RedirectHost;
        float ly = Paragraph(g, host, x + 58, y + 30, leftW - 64, Ui(12), Secondary, 18) + 26;
        Line(g, x, ly - 10, x + leftW - (columns ? 20 : 0));
        UiText(g, I18n.T("Domain", "Домен"), Ui(12), Secondary, new(x, ly, leftW - 20, 20));
        ly = Paragraph(g, host, x, ly + 24, leftW - 20, Ui(13), Primary) + 18;
        UiText(g, I18n.T("Requested rights", "Запрашиваемые права"), Ui(12), Secondary, new(x, ly, leftW - 20, 20));
        string rights = !string.IsNullOrWhiteSpace(_req.ScopeHuman) ? _req.ScopeHuman : AccessStore.FormatScope(_req.Scope);
        ly = Paragraph(g, rights, x, ly + 25, leftW - 20, Ui(13), Primary) + 20;
        float rx = columns ? x + leftW + 20 : x, rw = columns ? width - leftW - 20 : width;
        float ry = columns ? startY : ly + 8;
        if (columns) { using var pen = new Pen(BorderColor); g.DrawLine(pen, rx - 20, startY - 20, rx - 20, Math.Max(ly, startY + 240)); }
        UiText(g, I18n.T("Access level", "Уровень доступа"), Ui(14, true), Primary, new(rx, ry, rw, 24)); ry += 36;
        string[] scopeLabels = { I18n.T("Read", "Чтение"), I18n.T("Read and write", "Чтение и запись"), I18n.T("Full access", "Полный доступ") };
        string[] scopeDetails = { I18n.T("View files", "Просмотр файлов"), I18n.T("Create and edit", "Создание и изменение"), I18n.T("Including commands", "Включая команды") };
        bool verticalScope = rw < 470;
        float sw = verticalScope ? rw : (rw - 16) / 3;
        for (int i = 0; i < 3; i++) {
            int index = i; var sr = new RectangleF(rx + (verticalScope ? 0 : i * (sw + 8)), ry + (verticalScope ? i * 72 : 0), sw, 64); _scopeSegRects[i] = sr;
            bool sel = i == _selectedScopeIdx;
            Theme.FillRound(g, sr, 12, sel ? Theme.WithAlpha(Theme.Accent, Light ? 22 : 32) : Light ? Color.FromArgb(247, 248, 252) : Theme.Surface);
            Theme.StrokeRound(g, sr, 12, sel ? Theme.Accent : BorderColor, sel ? 1.5f : 1);
            Theme.FillRound(g, new(sr.X + 10, sr.Y + 23, 14, 14), 7, sel ? Theme.Accent : Color.Transparent); Theme.StrokeRound(g, new(sr.X + 10, sr.Y + 23, 14, 14), 7, sel ? Theme.Accent : Secondary);
            if (sel) Theme.FillRound(g, new(sr.X + 14, sr.Y + 27, 6, 6), 3, Color.White);
            UiText(g, scopeLabels[i], Ui(12, true), Primary, new(sr.X + 32, sr.Y + 9, sr.Width - 38, 22));
            UiText(g, scopeDetails[i], Ui(11), Secondary, new(sr.X + 32, sr.Y + 33, sr.Width - 38, 20));
            AddHit(sr, () => { _selectedScopeIdx = index; RenderLayered(); }, scopeLabels[i], body: true);
        }
        ry += verticalScope ? 232 : 84; UiText(g, I18n.T("Access duration", "Срок доступа"), Ui(14, true), Primary, new(rx, ry, rw, 24)); ry += 36;
        Theme.FillRound(g, new(rx, ry, rw, 38), 11, BoxColor); Theme.StrokeRound(g, new(rx, ry, rw, 38), 11, BorderColor);
        string[] lifeLabels = { I18n.T("1 day", "1 день"), I18n.T("30 days", "30 дней"), I18n.T("Never expires", "Бессрочно") };
        for (int i = 0; i < 3; i++) {
            int index = i; var sr = new RectangleF(rx + 4 + i * (rw - 8) / 3, ry + 4, (rw - 8) / 3, 30); _lifetimeSegRects[i] = sr;
            if (_selectedLifetimeIdx == i) Theme.FillRound(g, sr, 8, Theme.Accent);
            UiText(g, lifeLabels[i], Ui(12, true), _selectedLifetimeIdx == i ? Color.White : Secondary, sr, true);
            AddHit(sr, () => { _selectedLifetimeIdx = index; RenderLayered(); }, lifeLabels[i], body: true);
        }
        ry += 54;
        string caution = GetSelectedOAuthScope() == "full" ? I18n.T("Full access includes commands and changes to this PC. Connect only applications you trust.", "Полный доступ включает команды и изменения на ПК. Подключайте только доверенные приложения.") : I18n.T("The selected rights apply to this PC, not to an account on the client's website.", "Выбранные права относятся к этому ПК, а не к аккаунту на сайте клиента.");
        ry = Warning(g, caution, rx, ry, rw);
        if (HasWarning) ry = Warning(g, !string.IsNullOrWhiteSpace(_req.Warning) ? _req.Warning! : I18n.T("Unknown website. Verify the domain and application before connecting.", "Неизвестный сайт. Проверьте домен и приложение перед подключением."), rx, ry, rw);
        return Math.Max(ly, ry);
    }
    private void DrawFooter(Graphics g, RectangleF r, float top)
    {
        using(var brush = new SolidBrush(Light ? Color.FromArgb(246, 248, 252) : Color.FromArgb(16, 19, 25))) g.FillRectangle(brush, r.X, top, r.Width, r.Bottom - top);
        Line(g, r.X, top, r.Right);
        float x = r.X + 20, y = top + 20, width = r.Width - 40;
        if (_isOAuthConsent) {
            if (W < 440) {
                string scope = _selectedScopeIdx == 0 ? I18n.T("Read", "Чтение") : _selectedScopeIdx == 1 ? I18n.T("Read and write", "Чтение и запись") : I18n.T("Full access", "Полный доступ");
                string life = _selectedLifetimeIdx == 0 ? I18n.T("1 day", "1 день") : _selectedLifetimeIdx == 1 ? I18n.T("30 days", "30 дней") : I18n.T("Never expires", "Бессрочно");
                UiText(g, scope + " · " + life, Ui(11.5f, true), AccentText, new(x, top + 4, width, 22), true);
                y = top + 30;
            }
            float deny = Math.Min(166, width * .30f);
            _btnDismissRect = W < 440 ? new(x, y, width, 38) : new(x, y, deny, 38);
            _btnAllowRect = W < 440 ? new(x, y + 46, width, 38) : new(x + deny + 10, y, width - deny - 10, 38);
            Button(g, _btnDismissRect, I18n.T("Deny", "Отклонить"), ButtonKind.Secondary, () => ForceResolve("deny", null, null, "user"));
            Button(g, _btnAllowRect, I18n.T("Allow connection", "Разрешить подключение"), ButtonKind.Primary, () => ForceResolve("allow", null, null, "user"), IconKind.Link);
        } else {
            float detailsW = 30; width -= detailsW + 10;
            if (!Grantable) {
                // Critical: no temporary grant on offer, only a decision about this exact call.
                float dW = W < 440 ? width * .42f : Math.Min(150, width * .32f);
                _btnDismissRect = new(x, y, dW, 38);
                _btnGrantMainRect = RectangleF.Empty; _btnGrantArrowRect = RectangleF.Empty;
                _btnAllowRect = new(x + dW + 10, y, width - dW - 10, 38);
                Button(g, _btnDismissRect, I18n.T("Deny", "Отклонить"), ButtonKind.Secondary, () => ForceResolve("deny", null, null, "user"));
                Button(g, _btnAllowRect, I18n.T("Allow this once", "Разрешить только этот раз"), ButtonKind.Danger, () => ForceResolve("allow_once", null, null, "user"), IconKind.Warning);
                IconButton(g, new(r.Right - 48, y, 32, 38), _expanded ? IconKind.ChevronUp : IconKind.ChevronDown, _expanded ? I18n.T("Collapse", "Свернуть") : I18n.T("More details", "Подробнее"), () => SetExpanded(!_expanded));
                return;
            }
            float denyW = Math.Min(116, width * .24f), grantW = (width - denyW - 20) * .51f, allowW = width - denyW - grantW - 20;
            if (W < 440) { denyW = width * .42f; grantW = width - denyW - 10; allowW = width; }
            _btnDismissRect = new(x, y, denyW, 38);
            _btnGrantMainRect = new(x + denyW + 10, y, grantW - (_isScreenshot ? 0 : 26), 38);
            _btnGrantArrowRect = _isScreenshot ? RectangleF.Empty : new(_btnGrantMainRect.Right, y, 26, 38);
            _btnAllowRect = W < 440 ? new(x, y + 46, allowW, 38) : new(_btnGrantArrowRect.IsEmpty ? _btnGrantMainRect.Right + 10 : _btnGrantArrowRect.Right + 10, y, allowW, 38);
            Button(g, _btnDismissRect, I18n.T("Deny", "Отклонить"), ButtonKind.Secondary, () => ForceResolve("deny", null, null, "user"));
            string grant = _grantMinutes >= 60 ? I18n.Format("For {0} h", "На {0} ч", _grantMinutes / 60) : I18n.Format("For {0} min", "На {0} мин", _grantMinutes);
            Button(g, _btnGrantMainRect, grant, ButtonKind.Outline, () => ForceResolve("allow_grant", _grantMinutes, _grantScope, "user"));
            if (!_btnGrantArrowRect.IsEmpty) {
                Theme.FillRound(g, _btnGrantArrowRect, 8, Theme.WithAlpha(Theme.Accent, 14)); Theme.StrokeRound(g, _btnGrantArrowRect, 8, Theme.WithAlpha(Theme.Accent, 130));
                Theme.DrawIcon(g, IconKind.ChevronDown, RectangleF.Inflate(_btnGrantArrowRect, -6, -12), AccentText, 1.6f);
                AddHit(_btnGrantArrowRect, ShowGrantDropdownMenu, I18n.T("Choose grant duration and scope", "Выбрать срок и область доступа"));
            }
            Button(g, _btnAllowRect, I18n.T("Allow once", "Разрешить один раз"), ButtonKind.Primary, () => ForceResolve("allow_once", null, null, "user"));
            IconButton(g, new(r.Right - 48, y, 32, 38), _expanded ? IconKind.ChevronUp : IconKind.ChevronDown, _expanded ? I18n.T("Collapse", "Свернуть") : I18n.T("More details", "Подробнее"), () => SetExpanded(!_expanded));
        }
    }
    private void OpenScreenshotViewer()
    {
        if (_previewImage == null) return;
        try { new ScreenshotViewerForm(new Bitmap(_previewImage), !string.IsNullOrWhiteSpace(_req.Description) ? _req.Description : _req.Note, _req.CreatedAt).Show(); } catch { }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _animTimer.Stop(); _animTimer.Dispose(); _motion.Dispose(); _toolTip.Dispose(); _previewImage?.Dispose(); _previewImage = null; }
        base.Dispose(disposing);
    }
    private void ShowGrantDropdownMenu()
    {
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = true,
        };

        var durations = new (int Mins, string Label)[]
        {
            (10, I18n.T("For 10 minutes", "На 10 минут")),
            (30, I18n.T("For 30 minutes", "На 30 минут")),
            (60, I18n.T("For 1 hour", "На 1 час")),
            (180, I18n.T("For 3 hours", "На 3 часа")),
        };

        foreach (var (mins, label) in durations)
        {
            var item = new ToolStripMenuItem(label)
            {
                Checked = _grantMinutes == mins,
            };
            int capturedMins = mins;
            item.Click += (_, _) =>
            {
                _grantMinutes = capturedMins;
                _onDefaultGrantUpdated(_grantMinutes, _grantScope);
                ForceResolve("allow_grant", _grantMinutes, _grantScope, "user");
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new ToolStripSeparator());

        var scopeToolItem = new ToolStripMenuItem(I18n.Format("For this tool ({0})", "Для этого инструмента ({0})", _req.Tool))
        {
            Checked = _grantScope == "tool",
        };
        scopeToolItem.Click += (_, _) =>
        {
            _grantScope = "tool";
            _onDefaultGrantUpdated(_grantMinutes, _grantScope);
            RenderLayered();
        };

        string exactTargetLabel = _req.Tool == "run_command"
            ? I18n.T("Only for this command", "Только для этой команды")
            : I18n.T("Only for this file", "Только для этого файла");
        var scopeExactItem = new ToolStripMenuItem(exactTargetLabel)
        {
            Checked = _grantScope == "exact",
        };
        scopeExactItem.Click += (_, _) =>
        {
            _grantScope = "exact";
            _onDefaultGrantUpdated(_grantMinutes, _grantScope);
            RenderLayered();
        };

        menu.Items.Add(scopeToolItem);
        menu.Items.Add(scopeExactItem);

        Theme.StyleMenu(menu);
        menu.Closed += (_, _) =>
        {
            // Deferred dispose: disposing synchronously inside Closed races with
            // ToolStripManager.ModalMenuFilter, which still touches the dropdown
            // handle after CloseActiveDropDown (ObjectDisposedException on the UI
            // thread killed the tray on 2026-10-26). BeginInvoke runs after the
            // current message is fully processed.
            try { BeginInvoke((Action)(() => { try { if (!menu.IsDisposed) menu.Dispose(); } catch { } })); } catch { }
        };
        menu.Show(this, new Point((int)(_btnGrantMainRect.Left * ScaleFactor), (int)(_btnGrantMainRect.Bottom * ScaleFactor) + 2));
    }

    public void ForceResolve(string decision, int? grantMinutes, string? grantScope, string by)
    {
        if (_resolved) return;
        _resolved = true;
        _fadingOut = true;

        string normalizedDecision = decision;
        if (_isOAuthConsent && (decision == "allow_once" || decision == "allow_grant"))
        {
            normalizedDecision = "allow";
        }
        if (!Grantable && decision == "allow_grant")
        {
            // Critical calls are approved one at a time; never answer them with a grant.
            normalizedDecision = "allow_once"; grantMinutes = null; grantScope = null;
        }

        string? chosenOAuthScope = _isOAuthConsent ? GetSelectedOAuthScope() : null;
        if (_isOAuthConsent && normalizedDecision == "allow" && !string.IsNullOrEmpty(chosenOAuthScope))
        {
            AccessStore.RegisterPendingOAuthScopeOverride(_req.ClientId, chosenOAuthScope);
        }

        _onResolved(new ApprovalResponseDto
        {
            Id = _req.Id,
            Decision = normalizedDecision,
            GrantMinutes = _isOAuthConsent ? null : grantMinutes,
            GrantScope = _isOAuthConsent ? null : grantScope,
            LifetimeDays = _isOAuthConsent ? GetSelectedLifetimeDays() : null,
            Scope = chosenOAuthScope,
            By = by,
        });
    }

    private void RenderLayered()
    {
        if (IsDisposed || !IsHandleCreated || !_layoutVisible) return;

        using var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            RenderToGraphics(g);
        }

        IntPtr screenDc = IntPtr.Zero;
        IntPtr memDc = CreateCompatibleDC(IntPtr.Zero);
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;
        try
        {
            hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            oldBitmap = SelectObject(memDc, hBitmap);
            var size = new Size(Width, Height);
            var pointSrc = new Point(0, 0);
            var topPos = new Point(Left, Top);
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = (byte)Math.Clamp((int)(_alpha * 255), 0, 255),
                AlphaFormat = AC_SRC_ALPHA,
            };
            if (UpdateLayeredWindow(Handle, screenDc, ref topPos, ref size, memDc, ref pointSrc, 0, ref blend, ULW_ALPHA)) LayeredFrameVersion++;
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
        }
    }

}

/// <summary>Full-size view of a screenshot preview (click the preview on an approval card).</summary>
/// <summary>Archive for approved screenshots: saves the image with a
/// caption bar burned into an extended canvas below the photo (never over
/// the pixels). The model always receives the clean image; only the
/// archived copy carries the caption. Saving is best-effort: any failure
/// is logged and never fails the tool call.</summary>
internal static class ScreenshotArchive
{
    public static string? TrySave(string base64, string description, string note, string tokenLabel, Action<string> log)
    {
        try
        {
            if (!ScreenshotArchiveConfig.Enabled) return null;
            if (string.IsNullOrWhiteSpace(base64)) return null;
            string dir = ScreenshotArchiveConfig.Dir;
            Directory.CreateDirectory(dir);
            byte[] bytes = Convert.FromBase64String(base64);
            using var ms = new MemoryStream(bytes);
            using var src = Image.FromStream(ms);
            string text = string.IsNullOrWhiteSpace(description) ? (note ?? "") : description;
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (text.Length > 500) text = text[..500];
            using var outBmp = BuildCaptioned(src, text, tokenLabel);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string safeLabel = SanitizeLabel(tokenLabel);
            string name = stamp + "_" + safeLabel + ".jpg";
            string path = Path.Combine(dir, name);
            int n = 2;
            while (File.Exists(path)) { path = Path.Combine(dir, stamp + "_" + safeLabel + "-" + n + ".jpg"); n++; }
            var enc = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(e => e.MimeType == "image/jpeg");
            using var ep = new System.Drawing.Imaging.EncoderParameters(1);
            ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
            outBmp.Save(path, enc, ep);
            return Path.GetFileName(path);
        }
        catch (Exception ex)
        {
            try { log("screenshot archive failed: " + ex.Message); } catch { }
            return null;
        }
    }

    private static string SanitizeLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "legacy";
        char[] bad = Path.GetInvalidFileNameChars();
        var chars = label.Trim().Select(c => bad.Contains(c) ? '_' : c).ToArray();
        string s = new string(chars).Trim();
        if (s.Length > 40) s = s[..40].TrimEnd();
        return s.Length == 0 ? "legacy" : s;
    }

    private static Bitmap BuildCaptioned(Image src, string text, string tokenLabel)
    {
        if (string.IsNullOrWhiteSpace(text)) return new Bitmap(src);
        int w = src.Width;
        float descSize = Math.Clamp(w / 66f, 18f, 24f);
        float metaSize = Math.Max(13f, descSize * 0.68f);
        using var descFont = new Font("Segoe UI", descSize, FontStyle.Regular, GraphicsUnit.Pixel);
        using var metaFont = new Font("Segoe UI", metaSize, FontStyle.Regular, GraphicsUnit.Pixel);
        using var meas = new Bitmap(1, 1);
        using var mg = Graphics.FromImage(meas);
        mg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        float padX = Math.Max(24f, w * 0.02f);
        float maxTextW = w - padX * 2;
        var lines = WrapText(mg, text, descFont, maxTextW, 6);
        float lineH = descFont.Height * 1.35f;
        float metaH = metaFont.Height;
        int barH = (int)Math.Ceiling(3 + 14 + metaH + 10 + lines.Count * lineH + 22);
        var bmp = new Bitmap(w, src.Height + barH);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.DrawImage(src, 0, 0, w, src.Height);
            using (var barBrush = new SolidBrush(Color.FromArgb(22, 22, 22)))
                g.FillRectangle(barBrush, 0, src.Height, w, barH);
            using (var accentBrush = new SolidBrush(Theme.Accent))
                g.FillRectangle(accentBrush, 0, src.Height, w, 3);
            float y = src.Height + 3 + 14;
            string metaLeft = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  ·  " + tokenLabel;
            using (var metaBrush = new SolidBrush(Color.FromArgb(154, 154, 154)))
            {
                g.DrawString(metaLeft, metaFont, metaBrush, padX, y);
                string brand = Theme.ProductName;
                float bw = g.MeasureString(brand, metaFont).Width;
                g.DrawString(brand, metaFont, metaBrush, w - padX - bw, y);
            }
            y += metaH + 10;
            using (var textBrush = new SolidBrush(Color.White))
            {
                foreach (var line in lines)
                {
                    g.DrawString(line, descFont, textBrush, padX, y);
                    y += lineH;
                }
            }
        }
        return bmp;
    }

    private static List<string> WrapText(Graphics g, string text, Font font, float maxW, int maxLines)
    {
        var words = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        bool truncated = false;
        string cur = "";
        foreach (var word in words)
        {
            if (cur.Length == 0 && g.MeasureString(word, font).Width > maxW)
            {
                string rest = word;
                while (rest.Length > 0)
                {
                    int take = rest.Length;
                    while (take > 1 && g.MeasureString(rest[..take], font).Width > maxW) take--;
                    lines.Add(rest[..take]);
                    rest = rest[take..];
                    if (lines.Count >= maxLines) { truncated = true; break; }
                }
                if (truncated) break;
                continue;
            }
            string test = cur.Length == 0 ? word : cur + " " + word;
            if (g.MeasureString(test, font).Width <= maxW)
            {
                cur = test;
            }
            else
            {
                lines.Add(cur);
                cur = word;
                if (lines.Count >= maxLines) { truncated = true; break; }
            }
        }
        if (!truncated && cur.Length > 0) lines.Add(cur);
        if (truncated && lines.Count > 0)
        {
            string last = lines[^1];
            while (last.Length > 0 && g.MeasureString(last + "…", font).Width > maxW) last = last[..^1];
            lines[^1] = last + "…";
        }
        return lines;
    }
}

internal sealed class ScreenshotViewerForm : Form
{
    private readonly Image _image;
    private readonly Panel _toolbar = new Panel();
    private readonly Panel _canvas = new Panel();
    private readonly Panel _infoBar = new Panel();
    private readonly Label _zoomLabel = new Label();
    private readonly Label _descLabel = new Label();
    private readonly Label _metaLabel = new Label();
    private Button? _folderBtn;
    private string _descText = "";
    private bool _descIsPlaceholder;
    private float _zoom = 1f;
    private bool _fitMode = true;
    private PointF _pan;
    private Point _dragLast;
    private bool _dragging;

    public ScreenshotViewerForm(Image image, string? description = null, string? createdAt = null)
    {
        _image = image;
        Text = I18n.T("Screenshot", "Скриншот");
        Theme.ApplyLogo(this);
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        TopMost = true;
        BackColor = Color.Black;
        MinimumSize = new Size(420, 300);
        KeyPreview = true;

        _descText = string.IsNullOrWhiteSpace(description)
            ? I18n.T("No description", "Без описания")
            : description!;
        _descIsPlaceholder = string.IsNullOrWhiteSpace(description);
        string timePart = "";
        if (!string.IsNullOrWhiteSpace(createdAt) && DateTime.TryParse(createdAt, out var dt))
            timePart = dt.ToLocalTime().ToString("dd.MM HH:mm");
        _metaLabel.Text = (timePart.Length > 0 ? timePart + " · " : "") + image.Width + "×" + image.Height;

        // Toolbar: hint label first (fills the bar), buttons above it on the left.
        _toolbar.BackColor = Color.FromArgb(24, 24, 28);
        var hint = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            Text = I18n.T("Wheel zooms · drag to pan · double-click toggles fit · Esc closes", "Колесо — зум · перетаскивание — сдвиг · двойной клик — вписать/100% · Esc — закрыть"),
            ForeColor = Color.FromArgb(160, 160, 168),
            Font = Theme.Small,
        };
        _toolbar.Controls.Add(hint);
        int bx = 8;
        Button MkBtn(string text, int w, Action onClick)
        {
            var b = new Button
            {
                Text = text,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(48, 48, 56),
                FlatStyle = FlatStyle.Flat,
                Size = new Size(w, 30),
                Location = new Point(bx, 5),
                TabStop = false,
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += (_, _) => onClick();
            _toolbar.Controls.Add(b);
            bx += w + 6;
            return b;
        }
        MkBtn("−", 36, () => SetZoom(_zoom / 1.25f, null));
        MkBtn("+", 36, () => SetZoom(_zoom * 1.25f, null));
        MkBtn("100%", 64, () => SetZoom(1f, null));
        MkBtn(I18n.T("Fit", "Вписать"), 96, () => FitZoom());
        _zoomLabel.AutoSize = true;
        _zoomLabel.ForeColor = Color.White;
        _zoomLabel.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _zoomLabel.Location = new Point(bx + 4, 11);
        _toolbar.Controls.Add(_zoomLabel);
        Controls.Add(_toolbar);

        // Canvas: custom paint (zoom + pan), no PictureBox.
        _canvas.BackColor = Color.Black;
        _canvas.Paint += CanvasPaint;
        _canvas.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            _dragLast = e.Location;
            _canvas.Capture = true;
        };
        _canvas.MouseMove += (_, e) =>
        {
            if (!_dragging) return;
            _pan = new PointF(_pan.X + (e.X - _dragLast.X), _pan.Y + (e.Y - _dragLast.Y));
            _dragLast = e.Location;
            ClampPan();
            _canvas.Invalidate();
        };
        _canvas.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _dragging = false;
            _canvas.Capture = false;
        };
        _canvas.DoubleClick += (_, _) =>
        {
            if (_fitMode) SetZoom(1f, null);
            else FitZoom();
        };
        Controls.Add(_canvas);

        // Info bar below the image: description + meta line (+ folder button when archiving).
        _infoBar.BackColor = Color.FromArgb(24, 24, 28);
        _descLabel.Location = new Point(12, 8);
        _descLabel.ForeColor = _descIsPlaceholder ? Color.FromArgb(160, 160, 168) : Color.White;
        _descLabel.Font = new Font("Segoe UI", 10f);
        _infoBar.Controls.Add(_descLabel);
        _metaLabel.AutoSize = true;
        _metaLabel.ForeColor = Color.FromArgb(160, 160, 168);
        _metaLabel.Font = Theme.Small;
        _infoBar.Controls.Add(_metaLabel);
        if (ScreenshotArchiveConfig.Enabled)
        {
            _folderBtn = new Button
            {
                Text = I18n.T("Open folder", "Открыть папку"),
                Font = new Font("Segoe UI", 10f),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(48, 48, 56),
                FlatStyle = FlatStyle.Flat,
                Size = new Size(140, 30),
                TabStop = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
            };
            _folderBtn.FlatAppearance.BorderSize = 0;
            _folderBtn.Click += (_, _) =>
            {
                string dir = ScreenshotArchiveConfig.Dir;
                DesktopLauncher.OpenFolder(dir);
            };
            _infoBar.Controls.Add(_folderBtn);
        }
        Controls.Add(_infoBar);

        MouseWheel += (_, e) =>
        {
            if (!_canvas.Bounds.Contains(e.Location)) return;
            var at = new Point(e.Location.X - _canvas.Bounds.X, e.Location.Y - _canvas.Bounds.Y);
            float nz = e.Delta > 0 ? _zoom * 1.25f : _zoom / 1.25f;
            SetZoom(nz, at);
            ((HandledMouseEventArgs)e).Handled = true;
        };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
        };
        Resize += (_, _) => LayoutAll();

        // Initial size: fit the image into 85% of the working area.
        var wa = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
        int maxImgW = (int)(wa.Width * 0.85);
        int maxImgH = (int)(wa.Height * 0.85) - 130;
        float scale = Math.Min((float)maxImgW / image.Width, (float)maxImgH / image.Height);
        int imgW = Math.Max(200, (int)(image.Width * scale));
        int imgH = Math.Max(150, (int)(image.Height * scale));
        ClientSize = new Size(imgW, 40 + imgH + 70);
        LayoutAll();
        ClientSize = new Size(imgW, 40 + imgH + _infoBar.Height);
        LayoutAll();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _image.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }

    private void LayoutAll()
    {
        int w = ClientSize.Width;
        int h = ClientSize.Height;
        UpdateInfoLayout();
        _toolbar.Bounds = new Rectangle(0, 0, w, 40);
        _infoBar.Bounds = new Rectangle(0, h - _infoBar.Height, w, _infoBar.Height);
        _canvas.Bounds = new Rectangle(0, 40, w, Math.Max(0, h - 40 - _infoBar.Height));
        if (_fitMode) FitZoom();
        else { ClampPan(); _canvas.Invalidate(); }
    }

    private void UpdateInfoLayout()
    {
        int barW = Math.Max(100, ClientSize.Width);
        int textW = barW - 24 - (_folderBtn != null ? 152 : 0);
        _descLabel.Width = Math.Max(100, textW);
        var sz = TextRenderer.MeasureText(_descText, _descLabel.Font, new Size(_descLabel.Width, int.MaxValue), TextFormatFlags.WordBreak);
        int one = Math.Max(1, TextRenderer.MeasureText("A", _descLabel.Font).Height);
        int lines = Math.Clamp((int)Math.Ceiling(sz.Height / (float)one), 1, 6);
        int descH = lines * (_descLabel.Font.Height + 2);
        _descLabel.Height = descH;
        _descLabel.Location = new Point(12, 8);
        _metaLabel.Location = new Point(12, 8 + descH + 4);
        _infoBar.Height = 8 + descH + 4 + 20 + 10;
        if (_folderBtn != null)
            _folderBtn.Location = new Point(barW - 152, Math.Max(0, (_infoBar.Height - 30) / 2));
    }

    private void CanvasPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);
        int cw = _canvas.ClientSize.Width;
        int ch = _canvas.ClientSize.Height;
        if (cw <= 0 || ch <= 0) return;
        float w = _image.Width * _zoom;
        float h = _image.Height * _zoom;
        float x = (cw - w) / 2f + _pan.X;
        float y = (ch - h) / 2f + _pan.Y;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(_image, x, y, w, h);
    }

    private void SetZoom(float nz, Point? anchor)
    {
        nz = Math.Clamp(nz, 0.1f, 8f);
        if (anchor.HasValue && _canvas.ClientSize.Width > 0 && _canvas.ClientSize.Height > 0)
        {
            float w0 = _image.Width * _zoom;
            float h0 = _image.Height * _zoom;
            float x0 = (_canvas.ClientSize.Width - w0) / 2f + _pan.X;
            float y0 = (_canvas.ClientSize.Height - h0) / 2f + _pan.Y;
            float ix = (anchor.Value.X - x0) / _zoom;
            float iy = (anchor.Value.Y - y0) / _zoom;
            _zoom = nz;
            float w1 = _image.Width * _zoom;
            float h1 = _image.Height * _zoom;
            _pan = new PointF(
                anchor.Value.X - ix * _zoom - (_canvas.ClientSize.Width - w1) / 2f,
                anchor.Value.Y - iy * _zoom - (_canvas.ClientSize.Height - h1) / 2f);
        }
        else
        {
            _zoom = nz;
        }
        _fitMode = false;
        ClampPan();
        UpdateZoomLabel();
        _canvas.Invalidate();
    }

    private void FitZoom()
    {
        int cw = _canvas.ClientSize.Width;
        int ch = _canvas.ClientSize.Height;
        if (cw <= 0 || ch <= 0) return;
        _zoom = Math.Min((float)cw / _image.Width, (float)ch / _image.Height);
        _pan = new PointF(0f, 0f);
        _fitMode = true;
        UpdateZoomLabel();
        _canvas.Invalidate();
    }

    private void ClampPan()
    {
        float cw = _canvas.ClientSize.Width;
        float ch = _canvas.ClientSize.Height;
        _pan = new PointF(
            Math.Clamp(_pan.X, -cw / 2f, cw / 2f),
            Math.Clamp(_pan.Y, -ch / 2f, ch / 2f));
    }

    private void UpdateZoomLabel()
    {
        _zoomLabel.Text = ((int)Math.Round(_zoom * 100)) + "%";
    }
}
