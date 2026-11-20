using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZeroIDE.Mcp;

internal sealed class TelegramConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("botToken")]
    public string BotToken { get; set; } = "";

    [JsonPropertyName("chatId")]
    public string ChatId { get; set; } = "";

    [JsonPropertyName("chatLabel")]
    public string ChatLabel { get; set; } = "";

    [JsonPropertyName("botUsername")]
    public string BotUsername { get; set; } = "";

    [JsonPropertyName("notifyTaskDone")]
    public bool NotifyTaskDone { get; set; } = true;

    [JsonPropertyName("notifyAgentMessages")]
    public bool NotifyAgentMessages { get; set; } = true;

    [JsonPropertyName("notifyApprovals")]
    public bool NotifyApprovals { get; set; } = true;

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BotToken) && !string.IsNullOrWhiteSpace(ChatId);
}

internal sealed record TelegramDetectResult(
    bool Ok,
    string ChatId,
    string ChatLabel,
    string BotUsername,
    string Message,
    bool NeedsStartMessage = false);

internal sealed record TelegramSendResult(bool Ok, string Detail);

/// <summary>
/// One-way Telegram alerts sent directly by the tray app (never exposed as an MCP tool).
/// Credentials live in telegram.json next to ZeroIDE-MCP.exe with private ACLs and server self-protection.
/// </summary>
internal sealed class TelegramNotifier : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly Regex BotTokenPattern = new(@"^\d{5,15}:[A-Za-z0-9_-]{20,120}$", RegexOptions.Compiled);
    private static readonly Regex ChatIdPattern = new(@"^-?\d{4,20}$", RegexOptions.Compiled);
    private static readonly Regex SecretPatterns = new(
        @"(?i)(Bearer\s+[A-Za-z0-9._\-~+/=]{12,}|\b\d{6,15}:[A-Za-z0-9_-]{25,}\b|\b(sk|ghp|gho|glpat|xox[baprs]|cr|ngrok)_[A-Za-z0-9_\-]{12,}\b|(token|secret|password|apikey|api_key|authtoken)\s*[=:]\s*['""]?[^\s'""]{8,}['""]?)",
        RegexOptions.Compiled);

    private readonly string _configPath;
    private readonly Action<string> _log;
    private readonly HttpClient _http;
    private readonly object _lock = new();
    private readonly HashSet<string> _sentKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> _sentOrder = new();
    private TelegramConfig _cfg;

    public TelegramConfig Config { get { lock (_lock) return _cfg; } }
    public string ConfigPath => _configPath;

    public TelegramNotifier(string configPath, Action<string>? log = null, HttpClient? httpClient = null)
    {
        _configPath = configPath;
        _log = log ?? (_ => { });
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        _cfg = LoadFrom(_configPath);
    }

    public static string DefaultConfigPath => Path.Combine(AppContext.BaseDirectory, "telegram.json");

    public static TelegramConfig LoadFrom(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var parsed = JsonSerializer.Deserialize<TelegramConfig>(File.ReadAllText(path, Utf8NoBom));
                if (parsed != null) return parsed;
            }
        }
        catch { }
        return new TelegramConfig();
    }

    public static void SaveTo(string path, TelegramConfig cfg)
    {
        string json = JsonSerializer.Serialize(cfg, JsonOpts);
        TunnelProfiles.WritePrivate(path, json);
    }

    public void UpdateConfig(TelegramConfig updated)
    {
        lock (_lock) _cfg = updated;
        try { SaveTo(_configPath, updated); }
        catch (Exception ex) { _log("telegram config save error: " + ex.GetType().Name); }
    }

    public static bool IsValidBotToken(string? raw) => !string.IsNullOrWhiteSpace(raw) && BotTokenPattern.IsMatch(raw.Trim());
    public static bool IsValidChatId(string? raw) => !string.IsNullOrWhiteSpace(raw) && ChatIdPattern.IsMatch(raw.Trim());

    public static string RedactSecrets(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return SecretPatterns.Replace(text, "[REDACTED]");
    }

    public static string EscapeHtml(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    private static string Clip(string? text, int maxLen)
    {
        string s = RedactSecrets(text).Trim();
        if (s.Length <= maxLen) return s;
        return s[..(maxLen - 1)].TrimEnd() + "…";
    }

    public static string FormatTaskDone(string agent, string title, string current, int doneCount)
    {
        string host = Environment.MachineName;
        var sb = new StringBuilder();
        sb.Append("✅ <b>").Append(EscapeHtml(I18n.T("Task completed", "Задача выполнена"))).Append("</b>");
        if (!string.IsNullOrWhiteSpace(agent))
            sb.Append(" · ").Append(EscapeHtml(Clip(agent, 60)));
        sb.Append("\n<b>").Append(EscapeHtml(Clip(title, 200))).Append("</b>");
        if (!string.IsNullOrWhiteSpace(current) && !string.Equals(current.Trim(), title.Trim(), StringComparison.Ordinal))
            sb.Append('\n').Append(EscapeHtml(Clip(current, 420)));
        if (doneCount > 0)
            sb.Append("\n\n").Append(EscapeHtml(I18n.Format("Completed steps: {0}", "Выполнено этапов: {0}", doneCount)));
        sb.Append("\n<code>").Append(EscapeHtml(host)).Append(" · ZeroIDE MCP</code>");
        return sb.ToString();
    }

    public static string FormatAgentMessage(string agent, string kind, string text, MessageLink? link = null)
    {
        string host = Environment.MachineName;
        string icon = kind == "warning" ? "⚠️" : kind == "request" ? "💬" : "ℹ️";
        string heading = kind == "warning"
            ? I18n.T("Agent warning", "Важно от агента")
            : kind == "request"
                ? I18n.T("Agent request", "Запрос от агента")
                : I18n.T("Agent message", "Сообщение от агента");
        var sb = new StringBuilder();
        sb.Append(icon).Append(" <b>").Append(EscapeHtml(heading)).Append("</b>");
        if (!string.IsNullOrWhiteSpace(agent))
            sb.Append(" · ").Append(EscapeHtml(Clip(agent, 60)));
        sb.Append('\n').Append(EscapeHtml(Clip(text, 700)));
        if (link != null && !string.IsNullOrWhiteSpace(link.Target))
        {
            string label = link.Kind == "url" ? link.Target : Path.GetFileName(link.Target.TrimEnd('\\', '/'));
            if (!string.IsNullOrWhiteSpace(label))
                sb.Append("\n📎 <code>").Append(EscapeHtml(Clip(label, 140))).Append("</code>");
        }
        sb.Append("\n<code>").Append(EscapeHtml(host)).Append(" · ZeroIDE MCP</code>");
        return sb.ToString();
    }

    public static string FormatApproval(ApprovalRequestDto req)
    {
        string host = Environment.MachineName;
        string level = (req.Assessment?.Level ?? "medium").ToLowerInvariant();
        string icon = level == "critical" ? "🛑" : level == "high" ? "⚠️" : "🔔";
        string riskLabel = level switch
        {
            "critical" => I18n.T("CRITICAL RISK", "КРИТИЧЕСКИЙ РИСК"),
            "high" => I18n.T("High risk", "Высокий риск"),
            "low" => I18n.T("Low risk", "Низкий риск"),
            _ => I18n.T("Approval needed", "Требуется подтверждение"),
        };
        var sb = new StringBuilder();
        sb.Append(icon).Append(" <b>").Append(EscapeHtml(riskLabel)).Append("</b>");
        if (!string.IsNullOrWhiteSpace(req.Agent))
            sb.Append(" · ").Append(EscapeHtml(Clip(req.Agent, 60)));
        sb.Append("\n").Append(EscapeHtml(I18n.T("Tool: ", "Инструмент: "))).Append("<code>").Append(EscapeHtml(req.Tool)).Append("</code>");
        string target = !string.IsNullOrWhiteSpace(req.Summary?.Target) ? req.Summary.Target : (req.Summary?.Title ?? "");
        if (!string.IsNullOrWhiteSpace(target))
            sb.Append("\n<code>").Append(EscapeHtml(Clip(target, 260))).Append("</code>");
        if (!string.IsNullOrWhiteSpace(req.Assessment?.Headline))
            sb.Append('\n').Append(EscapeHtml(Clip(req.Assessment.Headline, 200)));
        if (req.TimeoutSec > 0)
            sb.Append("\n\n").Append(EscapeHtml(I18n.Format("Waiting on PC ({0}s timeout)", "Ждёт решения на ПК (таймаут {0} с)", req.TimeoutSec)));
        sb.Append("\n<code>").Append(EscapeHtml(host)).Append(" · ZeroIDE MCP</code>");
        return sb.ToString();
    }

    public static (string ChatId, string Label) ParseLatestChatFromUpdates(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("ok", out var okEl) || okEl.ValueKind != JsonValueKind.True)
            return ("", "");
        if (!root.TryGetProperty("result", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return ("", "");

        string bestId = "", bestLabel = "";
        foreach (var upd in arr.EnumerateArray())
        {
            JsonElement msg = default;
            if (upd.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object) msg = m;
            else if (upd.TryGetProperty("edited_message", out var em) && em.ValueKind == JsonValueKind.Object) msg = em;
            else continue;

            if (!msg.TryGetProperty("chat", out var chat) || chat.ValueKind != JsonValueKind.Object) continue;
            if (!chat.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) continue;
            string cid = idEl.GetInt64().ToString();
            string uname = chat.TryGetProperty("username", out var unEl) ? (unEl.GetString() ?? "") : "";
            string first = chat.TryGetProperty("first_name", out var fnEl) ? (fnEl.GetString() ?? "") : "";
            string title = chat.TryGetProperty("title", out var ttEl) ? (ttEl.GetString() ?? "") : "";
            string label = !string.IsNullOrWhiteSpace(uname) ? "@" + uname : !string.IsNullOrWhiteSpace(title) ? title : first;
            bestId = cid;
            bestLabel = label;
        }
        return (bestId, bestLabel);
    }

    private bool MarkOnce(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return true;
        lock (_lock)
        {
            if (!_sentKeys.Add(key)) return false;
            _sentOrder.Enqueue(key);
            while (_sentOrder.Count > 120) _sentKeys.Remove(_sentOrder.Dequeue());
            return true;
        }
    }

    public void NotifyTaskDone(string agentId, string agentName, string title, string current, int doneCount, DateTime? completedAt)
    {
        TelegramConfig cfg;
        lock (_lock) cfg = _cfg;
        if (!cfg.Enabled || !cfg.IsConfigured || !cfg.NotifyTaskDone) return;
        string key = $"done:{agentId}:{title}:{(completedAt ?? DateTime.Now):yyyyMMddHHmm}";
        if (!MarkOnce(key)) return;
        string html = FormatTaskDone(agentName, title, current, doneCount);
        _ = SendHtmlInternalAsync(cfg.BotToken, cfg.ChatId, html);
    }

    public void NotifyAgentMessage(string id, string agent, string kind, string text, MessageLink? link)
    {
        TelegramConfig cfg;
        lock (_lock) cfg = _cfg;
        if (!cfg.Enabled || !cfg.IsConfigured || !cfg.NotifyAgentMessages) return;
        string key = !string.IsNullOrWhiteSpace(id) ? "msg:" + id : "msg:" + kind + ":" + text.GetHashCode();
        if (!MarkOnce(key)) return;
        string html = FormatAgentMessage(agent, kind, text, link);
        _ = SendHtmlInternalAsync(cfg.BotToken, cfg.ChatId, html);
    }

    public void NotifyApproval(ApprovalRequestDto req)
    {
        TelegramConfig cfg;
        lock (_lock) cfg = _cfg;
        if (!cfg.Enabled || !cfg.IsConfigured || !cfg.NotifyApprovals) return;
        string key = "apr:" + (string.IsNullOrWhiteSpace(req.Id) ? req.Tool + ":" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() : req.Id);
        if (!MarkOnce(key)) return;
        string html = FormatApproval(req);
        _ = SendHtmlInternalAsync(cfg.BotToken, cfg.ChatId, html);
    }

    public async Task<TelegramDetectResult> DetectChatAsync(string rawToken, CancellationToken ct = default)
    {
        string token = (rawToken ?? "").Trim();
        if (!IsValidBotToken(token))
            return new(false, "", "", "", I18n.T("Invalid bot token format. Copy the full token from @BotFather.", "Неверный формат токена бота. Скопируй полный токен у @BotFather."));

        string botUsername = "";
        try
        {
            using var meRes = await _http.GetAsync($"https://api.telegram.org/bot{token}/getMe", ct);
            string meJson = await meRes.Content.ReadAsStringAsync(ct);
            if (!meRes.IsSuccessStatusCode)
                return new(false, "", "", "", I18n.T("Telegram rejected this bot token. Check it in @BotFather.", "Telegram отклонил этот токен бота. Проверь его в @BotFather."));
            using var doc = JsonDocument.Parse(meJson);
            if (doc.RootElement.TryGetProperty("result", out var res) && res.TryGetProperty("username", out var un))
                botUsername = un.GetString() ?? "";
        }
        catch (TaskCanceledException)
        {
            return new(false, "", "", "", I18n.T("Timed out reaching api.telegram.org. Check your internet connection.", "Не удалось дождаться ответа от api.telegram.org. Проверь интернет."));
        }
        catch
        {
            return new(false, "", "", "", I18n.T("Could not reach api.telegram.org. Check your network or proxy.", "Не удалось подключиться к api.telegram.org. Проверь сеть или прокси."));
        }

        try
        {
            using var updRes = await _http.GetAsync($"https://api.telegram.org/bot{token}/getUpdates?limit=20&allowed_updates=%5B%22message%22%5D", ct);
            string updJson = await updRes.Content.ReadAsStringAsync(ct);
            var (cid, label) = ParseLatestChatFromUpdates(updJson);
            if (!string.IsNullOrEmpty(cid))
            {
                string who = string.IsNullOrEmpty(label) ? $"ID {cid}" : $"{label} (ID {cid})";
                return new(true, cid, label, botUsername, I18n.Format("Chat found: {0}", "Чат найден: {0}", who));
            }
            string botRef = string.IsNullOrEmpty(botUsername) ? I18n.T("your bot", "своему боту") : "@" + botUsername;
            return new(false, "", "", botUsername,
                I18n.Format("Bot {0} is valid! Open it in Telegram, send /start (or any message), then click “Detect my chat” again.", "Бот {0} найден! Открой его в Telegram, нажми Start (или напиши любое сообщение), а затем снова нажми «Определить мой чат».", botRef),
                NeedsStartMessage: true);
        }
        catch
        {
            return new(false, "", "", botUsername, I18n.T("Could not read recent messages from Telegram. You can still enter Chat ID manually.", "Не удалось прочитать сообщения у Telegram. Можно ввести Chat ID вручную."));
        }
    }

    public Task<TelegramSendResult> SendTestAsync(string rawToken, string rawChatId, CancellationToken ct = default)
    {
        string token = (rawToken ?? "").Trim();
        string chatId = (rawChatId ?? "").Trim();
        if (!IsValidBotToken(token))
            return Task.FromResult(new TelegramSendResult(false, I18n.T("Enter a valid bot token first.", "Сначала введи корректный токен бота.")));
        if (!IsValidChatId(chatId))
            return Task.FromResult(new TelegramSendResult(false, I18n.T("Enter a numeric Chat ID or click “Detect my chat”.", "Укажи числовой Chat ID или нажми «Определить мой чат».")));

        string html = "🔔 <b>ZeroIDE MCP</b>\n"
            + EscapeHtml(I18n.T("Test notification succeeded! One-way alerts from this PC are connected.", "Тестовое уведомление доставлено! Односторонние алерты с этого ПК подключены."))
            + "\n<code>" + EscapeHtml(Environment.MachineName) + "</code>";
        return SendHtmlInternalAsync(token, chatId, html, ct);
    }

    private async Task<TelegramSendResult> SendHtmlInternalAsync(string token, string chatId, string html, CancellationToken ct = default)
    {
        try
        {
            string payload = JsonSerializer.Serialize(new
            {
                chat_id = chatId,
                text = html,
                parse_mode = "HTML",
                disable_web_page_preview = true,
            });
            using var content = new StringContent(payload, Utf8NoBom, "application/json");
            using var res = await _http.PostAsync($"https://api.telegram.org/bot{token}/sendMessage", content, ct);
            if (res.IsSuccessStatusCode)
            {
                _log("telegram alert sent");
                return new(true, I18n.T("Test message sent! Check Telegram.", "Тестовое сообщение отправлено! Проверь Telegram."));
            }
            string detail = $"HTTP {(int)res.StatusCode}";
            try
            {
                string body = await res.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("description", out var d))
                    detail = Clip(d.GetString(), 140);
            }
            catch { }
            _log("telegram alert failed: " + detail);
            return new(false, I18n.Format("Telegram error: {0}", "Ошибка Telegram: {0}", detail));
        }
        catch (TaskCanceledException)
        {
            _log("telegram alert timeout");
            return new(false, I18n.T("Timed out reaching api.telegram.org.", "Таймаут подключения к api.telegram.org."));
        }
        catch (Exception ex)
        {
            // Never log exception message directly if it might contain the request URI with the bot token.
            _log("telegram alert error: " + ex.GetType().Name);
            return new(false, I18n.T("Network error contacting api.telegram.org.", "Сетевая ошибка при обращении к api.telegram.org."));
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}

internal sealed class TelegramSettingsDialog : Form
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    private const int WM_NCLBUTTONDOWN = 0xA1, HT_CAPTION = 0x2;

    private readonly TelegramNotifier _notifier;
    private readonly TelegramConfig _initial;
    private readonly TextBox _tokenBox;
    private readonly TextBox _chatIdBox;
    private readonly List<(RectangleF Rect, string Id, Action OnClick)> _hits = new();
    private Point _mousePos = new(-1, -1);
    private string _hoveredId = "";
    private bool _tokenFocused, _chatFocused, _showToken, _busy;
    private string _chatLabel;
    private string _botUsername;
    private string _statusText;
    private Color _statusTone;

    public TelegramSettingsDialog(TelegramNotifier notifier)
    {
        _notifier = notifier;
        _initial = notifier.Config;
        _chatLabel = _initial.ChatLabel ?? "";
        _botUsername = _initial.BotUsername ?? "";

        Text = I18n.T("Telegram alerts", "Уведомления в Telegram");
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(600, 520);
        BackColor = Theme.Bg1;
        ForeColor = Theme.TextPrimary;
        Font = Theme.Body;
        DoubleBuffered = true;
        KeyPreview = true;
        ShowInTaskbar = false;
        TopMost = true;
        Theme.ApplyLogo(this);

        if (_initial.IsConfigured)
        {
            string who = string.IsNullOrWhiteSpace(_chatLabel) ? $"ID {_initial.ChatId}" : $"{_chatLabel} (ID {_initial.ChatId})";
            _statusTone = Theme.Success;
            _statusText = I18n.Format("Connected: {0}. Ready to send alerts.", "Подключено: {0}. Готово к отправке уведомлений.", who);
        }
        else
        {
            _statusTone = Theme.Info;
            _statusText = I18n.T("Create a bot in @BotFather, send /start to it, then click “Detect my chat”.", "Создай бота в @BotFather, напиши ему /start и нажми «Определить мой чат».");
        }

        _tokenBox = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Bg0,
            ForeColor = Theme.TextPrimary,
            Font = Theme.FontMono(10f),
            UseSystemPasswordChar = true,
            MaxLength = 256,
            Location = new Point(44, 152),
            Size = new Size(470, 22),
            PlaceholderText = _initial.IsConfigured
                ? I18n.T("Saved token (leave empty to keep)", "Токен сохранён (оставь пустым, чтобы не менять)")
                : "123456789:AAH...",
        };
        _tokenBox.GotFocus += (_, _) => { _tokenFocused = true; Invalidate(); };
        _tokenBox.LostFocus += (_, _) => { _tokenFocused = false; Invalidate(); };

        _chatIdBox = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Bg0,
            ForeColor = Theme.TextPrimary,
            Font = Theme.FontMono(10.5f),
            MaxLength = 32,
            Text = _initial.ChatId ?? "",
            Location = new Point(44, 306),
            Size = new Size(304, 22),
            PlaceholderText = "123456789",
        };
        _chatIdBox.GotFocus += (_, _) => { _chatFocused = true; Invalidate(); };
        _chatIdBox.LostFocus += (_, _) => { _chatFocused = false; Invalidate(); };

        Controls.Add(_tokenBox);
        Controls.Add(_chatIdBox);

        Shown += (_, _) => { Activate(); BringToFront(); _tokenBox.Focus(); };
        ApplyRoundedRegion();
    }

    private void ApplyRoundedRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        using var path = Theme.RoundRect(new RectangleF(0, 0, Width, Height), 16f);
        var prev = Region;
        Region = new Region(path);
        prev?.Dispose();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyRoundedRegion();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
        if (keyData == Keys.Enter && !_busy && (_tokenBox.Focused || _chatIdBox.Focused)) { SaveAndClose(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mousePos = e.Location;
        var hit = _hits.FirstOrDefault(h => h.Rect.Contains(e.Location));
        string nid = hit.Id ?? "";
        Cursor = !string.IsNullOrEmpty(nid) ? Cursors.Hand : Cursors.Default;
        if (nid != _hoveredId) { _hoveredId = nid; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _mousePos = new Point(-1, -1);
        Cursor = Cursors.Default;
        if (!string.IsNullOrEmpty(_hoveredId)) { _hoveredId = ""; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        foreach (var hit in _hits.ToList())
        {
            if (hit.Rect.Contains(e.Location))
            {
                hit.OnClick();
                return;
            }
        }
        if (e.Y <= 54)
        {
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
        }
    }

    private void PaintDialog(Graphics g)
    {
        _hits.Clear();
        Theme.PrepareGraphics(g);
        var saved = g.Save();
        using var winPath = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), 16f);
        g.SetClip(winPath);

        using (var bg = new LinearGradientBrush(ClientRectangle, Theme.BgGradTop, Theme.BgGradBottom, 90f))
            g.FillRectangle(bg, ClientRectangle);

        // ---- Header (matching SettingsForm)
        Theme.DrawLogo(g, new RectangleF(20, 13, 28, 28));
        string hdrTitle = I18n.T("Telegram alerts", "Уведомления в Telegram");
        Theme.DrawText(g, hdrTitle, PanelTypography.Get(14.5f, true), Theme.TextPrimary, new RectangleF(58, 15, 250, 24));

        Color chipCol = _initial.IsConfigured ? (_initial.Enabled ? Theme.Success : Theme.TextTertiary) : Theme.TextTertiary;
        string chipText = _initial.IsConfigured
            ? (_initial.Enabled ? I18n.T("Connected", "Подключено") : I18n.T("Paused", "На паузе"))
            : I18n.T("Not set up", "Не настроено");
        var chipFont = PanelTypography.Get(11.5f, true);
        float titleW = Theme.MeasureText(g, hdrTitle, PanelTypography.Get(14.5f, true)).Width;
        float chipW = Theme.MeasureChipOutlinedWidth(g, chipText, true, chipFont);
        Theme.DrawChipOutlined(g, new RectangleF(58 + titleW + 14, 15, chipW, 24), chipText, chipCol, Theme.WithAlpha(chipCol, 22), dot: true, font: chipFont);

        var closeRect = new RectangleF(Width - 46, 6, 40, 40);
        bool closeHov = closeRect.Contains(_mousePos);
        if (closeHov) Theme.FillRound(g, closeRect, 10f, Theme.Danger);
        Theme.DrawIcon(g, IconKind.Close, new RectangleF(closeRect.X + 13, closeRect.Y + 13, 14, 14), closeHov ? Color.White : Theme.TextSecondary, 1.8f);
        _hits.Add((closeRect, "win_close", () => { DialogResult = DialogResult.Cancel; Close(); }));
        Theme.DrawDivider(g, 0, Width, 54);

        // ---- Card 1: Bot token
        var card1 = new RectangleF(20, 72, Width - 40, 128);
        Theme.FillRound(g, card1, Theme.RadiusCard, Theme.Surface);
        Theme.StrokeRound(g, card1, Theme.RadiusCard, Theme.Border);

        Theme.DrawIconTile(g, new RectangleF(card1.X + 16, card1.Y + 16, 36, 36), IconKind.Lock, Theme.Accent, 17);
        Theme.DrawText(g, I18n.T("1. Bot token from @BotFather", "1. Токен бота от @BotFather"), PanelTypography.Get(13.5f, true), Theme.TextPrimary, new RectangleF(card1.X + 64, card1.Y + 14, 290, 22));
        Theme.DrawText(g, I18n.T("Stored in a private file protected from MCP agents", "Хранится в защищённом файле и недоступен агентам MCP"), PanelTypography.Get(11.5f), Theme.TextSecondary, new RectangleF(card1.X + 64, card1.Y + 36, card1.Width - 80, 18));

        string shieldBadge = I18n.T("Protected", "Защищено");
        float sbW = Theme.MeasureChipOutlinedWidth(g, shieldBadge, true, PanelTypography.Get(10.5f, true));
        Theme.DrawChipOutlined(g, new RectangleF(card1.Right - 16 - sbW, card1.Y + 15, sbW, 22), shieldBadge, Theme.Success, Theme.SuccessSubtle, dot: true, font: PanelTypography.Get(10.5f, true));

        var tokenWrap = new RectangleF(card1.X + 16, card1.Y + 70, card1.Width - 32, 40);
        Theme.FillRound(g, tokenWrap, 10f, Theme.Bg0);
        Theme.StrokeRound(g, tokenWrap, 10f, _tokenFocused ? Theme.Accent : Theme.BorderStrong, _tokenFocused ? 1.5f : 1f);
        _hits.Add((new RectangleF(tokenWrap.X, tokenWrap.Y, tokenWrap.Width - 40, tokenWrap.Height), "focus_token", () => _tokenBox.Focus()));

        var eyeRect = new RectangleF(tokenWrap.Right - 36, tokenWrap.Y + 4, 32, 32);
        bool eyeHov = eyeRect.Contains(_mousePos);
        if (eyeHov) Theme.FillRound(g, eyeRect, 7f, Theme.SurfaceHover);
        Theme.DrawIcon(g, IconKind.Eye, new RectangleF(eyeRect.X + 8, eyeRect.Y + 8, 16, 16), _showToken ? Theme.AccentText : (eyeHov ? Theme.TextPrimary : Theme.TextTertiary), 1.7f);
        _hits.Add((eyeRect, "toggle_eye", () => { _showToken = !_showToken; _tokenBox.UseSystemPasswordChar = !_showToken; Invalidate(); }));

        // ---- Card 2: Chat ID & auto-detection
        var card2 = new RectangleF(20, 214, Width - 40, 216);
        Theme.FillRound(g, card2, Theme.RadiusCard, Theme.Surface);
        Theme.StrokeRound(g, card2, Theme.RadiusCard, Theme.Border);

        Theme.DrawIconTile(g, new RectangleF(card2.X + 16, card2.Y + 16, 36, 36), IconKind.User, Theme.Info, 17);
        Theme.DrawText(g, I18n.T("2. Recipient chat (Chat ID)", "2. Чат получателя (Chat ID)"), PanelTypography.Get(13.5f, true), Theme.TextPrimary, new RectangleF(card2.X + 64, card2.Y + 14, 280, 22));
        Theme.DrawText(g, I18n.T("Send /start to your bot in Telegram, then click Detect", "Отправь боту /start в Telegram и нажми «Определить мой чат»"), PanelTypography.Get(11.5f), Theme.TextSecondary, new RectangleF(card2.X + 64, card2.Y + 36, card2.Width - 80, 18));

        if (!string.IsNullOrWhiteSpace(_botUsername))
        {
            string openBotText = "@" + _botUsername.TrimStart('@') + " ↗";
            var obFont = PanelTypography.Get(11f, true);
            float obW = Theme.MeasureText(g, openBotText, obFont).Width + 22f;
            var obRect = new RectangleF(card2.Right - 16 - obW, card2.Y + 15, obW, 24);
            bool obHov = obRect.Contains(_mousePos);
            Theme.FillRound(g, obRect, 7f, obHov ? Theme.SurfaceHover : Theme.SurfaceRaised);
            Theme.StrokeRound(g, obRect, 7f, obHov ? Theme.Accent : Theme.Border);
            Theme.DrawText(g, openBotText, obFont, Theme.InfoText, obRect, StringAlignment.Center);
            _hits.Add((obRect, "open_bot", () => DesktopLauncher.OpenUrl("https://t.me/" + _botUsername.TrimStart('@'))));
        }

        var chatWrap = new RectangleF(card2.X + 16, card2.Y + 80, 326, 40);
        Theme.FillRound(g, chatWrap, 10f, Theme.Bg0);
        Theme.StrokeRound(g, chatWrap, 10f, _chatFocused ? Theme.Accent : Theme.BorderStrong, _chatFocused ? 1.5f : 1f);
        _hits.Add((chatWrap, "focus_chat", () => _chatIdBox.Focus()));

        var detectRect = new RectangleF(chatWrap.Right + 10, card2.Y + 80, card2.Right - 16 - (chatWrap.Right + 10), 40);
        ButtonState detState = _busy ? ButtonState.Disabled : (detectRect.Contains(_mousePos) ? ButtonState.Hover : ButtonState.Normal);
        Theme.DrawButton(g, detectRect, I18n.T("Detect my chat", "Определить мой чат"), ButtonKind.Secondary, detState, PanelTypography.Get(12f, true), 10f, icon: IconKind.Search);
        _hits.Add((detectRect, "detect_chat", () => { if (!_busy) _ = RunDetectAsync(); }));

        // Status note box inside card 2
        var statusBox = new RectangleF(card2.X + 16, card2.Y + 134, card2.Width - 32, 66);
        Theme.FillRound(g, statusBox, 10f, Theme.WithAlpha(_statusTone, 24));
        Theme.StrokeRound(g, statusBox, 10f, Theme.WithAlpha(_statusTone, 84));
        Theme.DrawStatusDot(g, new PointF(statusBox.X + 16, statusBox.Y + 20), 4f, _statusTone, halo: false);
        Theme.DrawText(g, _statusText, PanelTypography.Get(12f), Theme.TextPrimary, new RectangleF(statusBox.X + 30, statusBox.Y + 10, statusBox.Width - 42, statusBox.Height - 16), StringAlignment.Near, StringAlignment.Near, ellipsis: false, wrap: true);

        // ---- Footer bar
        float footerY = Height - 68;
        Theme.DrawDivider(g, 0, Width, footerY);

        if (_initial.IsConfigured)
        {
            var disRect = new RectangleF(20, footerY + 16, 126, 36);
            ButtonState disState = _busy ? ButtonState.Disabled : (disRect.Contains(_mousePos) ? ButtonState.Hover : ButtonState.Normal);
            Theme.DrawButton(g, disRect, I18n.T("Disconnect", "Отключить"), ButtonKind.Danger, disState, PanelTypography.Get(12.5f, true), 10f, icon: IconKind.Trash);
            _hits.Add((disRect, "disconnect", () =>
            {
                if (_busy) return;
                _notifier.UpdateConfig(new TelegramConfig());
                DialogResult = DialogResult.OK;
                Close();
            }));
        }

        var saveRect = new RectangleF(Width - 20 - 134, footerY + 16, 134, 36);
        var testRect = new RectangleF(saveRect.X - 10 - 150, footerY + 16, 150, 36);
        var cancelRect = new RectangleF(testRect.X - 10 - 100, footerY + 16, 100, 36);

        Theme.DrawButton(g, cancelRect, I18n.T("Cancel", "Отмена"), ButtonKind.Subtle, cancelRect.Contains(_mousePos) ? ButtonState.Hover : ButtonState.Normal, PanelTypography.Get(12.5f, true), 10f);
        _hits.Add((cancelRect, "cancel", () => { DialogResult = DialogResult.Cancel; Close(); }));

        ButtonState testState = _busy ? ButtonState.Disabled : (testRect.Contains(_mousePos) ? ButtonState.Hover : ButtonState.Normal);
        Theme.DrawButton(g, testRect, I18n.T("Send test", "Отправить тест"), ButtonKind.Secondary, testState, PanelTypography.Get(12.5f, true), 10f);
        _hits.Add((testRect, "send_test", () => { if (!_busy) _ = RunTestAsync(); }));

        ButtonState saveState = _busy ? ButtonState.Disabled : (saveRect.Contains(_mousePos) ? ButtonState.Hover : ButtonState.Normal);
        Theme.DrawButton(g, saveRect, I18n.T("Save", "Сохранить"), ButtonKind.Primary, saveState, PanelTypography.Get(12.5f, true), 10f, icon: IconKind.Check);
        _hits.Add((saveRect, "save", () => { if (!_busy) SaveAndClose(); }));

        using (var border = new Pen(Theme.BorderStrong)) g.DrawPath(border, winPath);
        g.Restore(saved);
    }

    protected override void OnPaint(PaintEventArgs e) => PaintDialog(e.Graphics);

    /// <summary>Renders the dialog surface for UI visual checks without showing a window.</summary>
    internal void RenderPreviewToFile(string filePath)
    {
        using var bmp = new Bitmap(ClientSize.Width, ClientSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            PaintDialog(g);
            // Draw representative placeholder text inside input boxes for preview clarity
            Theme.DrawText(g, _initial.IsConfigured ? "••••••••••••••••••••••••••••••" : "123456789:AAH...", Theme.FontMono(10f), _initial.IsConfigured ? Theme.TextSecondary : Theme.TextTertiary, new RectangleF(44, 151, 460, 22));
            Theme.DrawText(g, string.IsNullOrEmpty(_chatIdBox.Text) ? "123456789" : _chatIdBox.Text, Theme.FontMono(10.5f), string.IsNullOrEmpty(_chatIdBox.Text) ? Theme.TextTertiary : Theme.TextPrimary, new RectangleF(44, 305, 300, 22));
        }
        bmp.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
    }

    private string EffectiveToken() => string.IsNullOrWhiteSpace(_tokenBox.Text) ? (_initial.BotToken ?? "").Trim() : _tokenBox.Text.Trim();

    private async Task RunDetectAsync()
    {
        _busy = true;
        _statusTone = Theme.Info;
        _statusText = I18n.T("Checking bot and recent /start messages…", "Проверяю бота и последние сообщения /start…");
        Invalidate();
        var res = await _notifier.DetectChatAsync(EffectiveToken());
        _busy = false;
        if (IsDisposed) return;
        if (!string.IsNullOrWhiteSpace(res.BotUsername)) _botUsername = res.BotUsername;
        if (res.Ok)
        {
            _chatIdBox.Text = res.ChatId;
            _chatLabel = res.ChatLabel;
            _statusTone = Theme.Success;
            _statusText = res.Message;
        }
        else
        {
            _statusTone = res.NeedsStartMessage ? Theme.Warning : Theme.Danger;
            _statusText = res.Message;
        }
        Invalidate();
    }

    private async Task RunTestAsync()
    {
        _busy = true;
        _statusTone = Theme.Info;
        _statusText = I18n.T("Sending test message…", "Отправляю тестовое сообщение…");
        Invalidate();
        var res = await _notifier.SendTestAsync(EffectiveToken(), _chatIdBox.Text.Trim());
        _busy = false;
        if (IsDisposed) return;
        _statusTone = res.Ok ? Theme.Success : Theme.Danger;
        _statusText = res.Detail;
        Invalidate();
    }

    private void SaveAndClose()
    {
        string token = EffectiveToken();
        string chatId = _chatIdBox.Text.Trim();
        if (!TelegramNotifier.IsValidBotToken(token))
        {
            _statusTone = Theme.Warning;
            _statusText = I18n.T("Enter a valid bot token from @BotFather.", "Введи корректный токен бота от @BotFather.");
            Invalidate();
            return;
        }
        if (!TelegramNotifier.IsValidChatId(chatId))
        {
            _statusTone = Theme.Warning;
            _statusText = I18n.T("Enter a numeric Chat ID or click “Detect my chat”.", "Укажи числовой Chat ID или нажми «Определить мой чат».");
            Invalidate();
            return;
        }
        string label = chatId == _initial.ChatId ? _chatLabel : (_chatLabel ?? "");
        _notifier.UpdateConfig(new TelegramConfig
        {
            Enabled = true,
            BotToken = token,
            ChatId = chatId,
            ChatLabel = label,
            BotUsername = _botUsername,
            NotifyTaskDone = _initial.NotifyTaskDone,
            NotifyAgentMessages = _initial.NotifyAgentMessages,
            NotifyApprovals = _initial.NotifyApprovals,
        });
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tokenBox.Clear();
        base.Dispose(disposing);
    }
}
