using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZeroIDE.Mcp;

public sealed class TokensRegistry
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("tokens")]
    public List<TokenEntry> Tokens { get; set; } = new();
}

public sealed class TokenEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "full"; // read | readwrite | full

    [JsonPropertyName("root")]
    public string? Root { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = "manual"; // manual | oauth

    [JsonPropertyName("oauthClientId")]
    public string? OauthClientId { get; set; }

    [JsonPropertyName("familyId")]
    public string? FamilyId { get; set; }

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";

    [JsonPropertyName("expiresAt")]
    public string? ExpiresAt { get; set; }

    [JsonPropertyName("ttlDays")]
    public int? TtlDays { get; set; }

    [JsonPropertyName("maxRequests")]
    public int? MaxRequests { get; set; }

    [JsonPropertyName("usedRequests")]
    public int UsedRequests { get; set; }

    [JsonPropertyName("lastUsedAt")]
    public string? LastUsedAt { get; set; }

    [JsonPropertyName("revoked")]
    public bool Revoked { get; set; }

    [JsonPropertyName("rotatedFrom")]
    public string? RotatedFrom { get; set; }
}

public sealed class OAuthStore
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("clients")]
    public List<OAuthClientEntry> Clients { get; set; } = new();

    [JsonPropertyName("families")]
    public List<OAuthFamilyEntry> Families { get; set; } = new();
}

public sealed class OAuthClientEntry
{
    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = "";

    [JsonPropertyName("client_name")]
    public string ClientName { get; set; } = "";

    [JsonPropertyName("redirect_uris")]
    public List<string> RedirectUris { get; set; } = new();

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";
}

public sealed class OAuthFamilyEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = "";

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "readwrite";

    [JsonPropertyName("oauthScope")]
    public string? OauthScope { get; set; }

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";

    [JsonPropertyName("expiresAt")]
    public string? ExpiresAt { get; set; }

    [JsonPropertyName("currentRefreshHash")]
    public string CurrentRefreshHash { get; set; } = "";

    [JsonPropertyName("pastRefreshHashes")]
    public List<string> PastRefreshHashes { get; set; } = new();

    [JsonPropertyName("revoked")]
    public bool Revoked { get; set; }

    [JsonPropertyName("revokedAt")]
    public string? RevokedAt { get; set; }

    [JsonPropertyName("lastRotatedAt")]
    public string? LastRotatedAt { get; set; }
}

public sealed record MissedApprovalItem(
    string Ts,
    string Id,
    string Tool,
    string Target,
    string Detail,
    string Note,
    string Reason, // timeout | unavailable
    string Token
);

public static class AccessStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string GetTokensPath() =>
        Path.Combine(AppContext.BaseDirectory, "tokens.json");

    public static string GetOAuthPath() =>
        Path.Combine(AppContext.BaseDirectory, "oauth.json");

    public static TokensRegistry LoadTokens()
    {
        string path = GetTokensPath();
        try
        {
            if (File.Exists(path))
            {
                string raw = File.ReadAllText(path, Utf8NoBom);
                var parsed = JsonSerializer.Deserialize<TokensRegistry>(raw);
                if (parsed != null)
                {
                    parsed.Tokens ??= new List<TokenEntry>();
                    return parsed;
                }
            }
        }
        catch { }

        return new TokensRegistry();
    }

    public static void SaveTokens(TokensRegistry reg)
    {
        string path = GetTokensPath();
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N")[..6];
            string json = JsonSerializer.Serialize(reg, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tmp, json, Utf8NoBom);
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    public static OAuthStore LoadOAuth()
    {
        string path = GetOAuthPath();
        try
        {
            if (File.Exists(path))
            {
                string raw = File.ReadAllText(path, Utf8NoBom);
                var parsed = JsonSerializer.Deserialize<OAuthStore>(raw);
                if (parsed != null)
                {
                    parsed.Clients ??= new List<OAuthClientEntry>();
                    parsed.Families ??= new List<OAuthFamilyEntry>();
                    return parsed;
                }
            }
        }
        catch { }

        return new OAuthStore();
    }

    public static void SaveOAuth(OAuthStore store)
    {
        string path = GetOAuthPath();
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N")[..6];
            string json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tmp, json, Utf8NoBom);
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    public static string GeneratePlainToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(24);
        string b64Url = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return "apw_" + b64Url;
    }

    public static string GenerateTokenId()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(4);
        return "tok_" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string ComputeSha256Hex(string plain)
    {
        byte[] hash = SHA256.HashData(Utf8NoBom.GetBytes(plain));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static (TokenEntry Entry, string Plaintext) CreateToken(
        string label,
        string scope,
        int? ttlDays,
        int? maxRequests,
        string? root,
        string? rotatedFrom = null)
    {
        string cleanScope = scope is "read" or "readwrite" or "full" ? scope : "full";
        string? cleanRoot = string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root.Trim().Trim('"'));
        string plain = GeneratePlainToken();
        DateTime now = DateTime.UtcNow;

        var entry = new TokenEntry
        {
            Id = GenerateTokenId(),
            Label = string.IsNullOrWhiteSpace(label) ? "Token" : label.Trim(),
            Hash = ComputeSha256Hex(plain),
            Scope = cleanScope,
            Root = cleanRoot,
            Source = "manual",
            OauthClientId = null,
            FamilyId = null,
            CreatedAt = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ExpiresAt = ttlDays.HasValue && ttlDays.Value > 0
                ? now.AddDays(ttlDays.Value).ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                : null,
            TtlDays = ttlDays.HasValue && ttlDays.Value > 0 ? ttlDays.Value : null,
            MaxRequests = maxRequests.HasValue && maxRequests.Value > 0 ? maxRequests.Value : null,
            UsedRequests = 0,
            LastUsedAt = null,
            Revoked = false,
            RotatedFrom = rotatedFrom,
        };

        var reg = LoadTokens();
        reg.Tokens.Add(entry);
        SaveTokens(reg);
        return (entry, plain);
    }

    public static bool RevokeToken(string id)
    {
        var reg = LoadTokens();
        var t = reg.Tokens.FirstOrDefault(x => x.Id == id);
        if (t == null) return false;
        t.Revoked = true;
        SaveTokens(reg);
        return true;
    }

    public static bool EnableToken(string id)
    {
        var reg = LoadTokens();
        var t = reg.Tokens.FirstOrDefault(x => x.Id == id);
        if (t == null) return false;
        t.Revoked = false;
        if (!string.IsNullOrEmpty(t.ExpiresAt) && DateTime.TryParse(t.ExpiresAt, out var exp) && exp.ToUniversalTime() <= DateTime.UtcNow)
        {
            int addDays = t.TtlDays.HasValue && t.TtlDays.Value > 0 ? t.TtlDays.Value : 30;
            t.ExpiresAt = DateTime.UtcNow.AddDays(addDays).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        }
        SaveTokens(reg);
        return true;
    }

    public static bool DeleteToken(string id)
    {
        var reg = LoadTokens();
        int removed = reg.Tokens.RemoveAll(x => x.Id == id);
        if (removed <= 0) return false;
        SaveTokens(reg);
        return true;
    }

    public static bool UpdateTokenScope(string id, string newScope)
    {
        string cleanScope = newScope is "read" or "readwrite" or "full" ? newScope : "full";
        var reg = LoadTokens();
        var t = reg.Tokens.FirstOrDefault(x => x.Id == id);
        if (t == null) return false;
        t.Scope = cleanScope;
        SaveTokens(reg);
        return true;
    }

    public static (TokenEntry? NewEntry, string? Plaintext) RotateToken(string id, int graceMinutes = 10)
    {
        var reg = LoadTokens();
        var old = reg.Tokens.FirstOrDefault(x => x.Id == id);
        if (old == null) return (null, null);

        DateTime now = DateTime.UtcNow;
        DateTime graceEnd = now.AddMinutes(Math.Max(1, graceMinutes));
        if (DateTime.TryParse(old.ExpiresAt, out var oldExp))
        {
            if (graceEnd < oldExp.ToUniversalTime())
                old.ExpiresAt = graceEnd.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        }
        else
        {
            old.ExpiresAt = graceEnd.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        }

        string plain = GeneratePlainToken();
        var entry = new TokenEntry
        {
            Id = GenerateTokenId(),
            Label = old.Label,
            Hash = ComputeSha256Hex(plain),
            Scope = old.Scope,
            Root = old.Root,
            Source = "manual",
            OauthClientId = null,
            FamilyId = null,
            CreatedAt = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ExpiresAt = old.TtlDays.HasValue && old.TtlDays.Value > 0
                ? now.AddDays(old.TtlDays.Value).ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                : null,
            TtlDays = old.TtlDays,
            MaxRequests = old.MaxRequests,
            UsedRequests = 0,
            LastUsedAt = null,
            Revoked = false,
            RotatedFrom = old.Id,
        };

        reg.Tokens.Add(entry);
        SaveTokens(reg);
        return (entry, plain);
    }

    public static bool DisconnectOAuthFamily(string familyId)
    {
        var oauth = LoadOAuth();
        var fam = oauth.Families.FirstOrDefault(f => f.Id == familyId);
        if (fam == null) return false;

        fam.Revoked = true;
        fam.RevokedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        SaveOAuth(oauth);

        var reg = LoadTokens();
        bool changedTokens = false;
        foreach (var t in reg.Tokens)
        {
            if (t.FamilyId == familyId && !t.Revoked)
            {
                t.Revoked = true;
                changedTokens = true;
            }
        }
        if (changedTokens)
        {
            SaveTokens(reg);
        }
        return true;
    }

    public static bool EnableOAuthFamily(string familyId)
    {
        var oauth = LoadOAuth();
        var fam = oauth.Families.FirstOrDefault(f => f.Id == familyId);
        if (fam == null) return false;

        fam.Revoked = false;
        fam.RevokedAt = null;
        if (!string.IsNullOrEmpty(fam.ExpiresAt) && DateTime.TryParse(fam.ExpiresAt, out var fExp) && fExp.ToUniversalTime() <= DateTime.UtcNow)
        {
            fam.ExpiresAt = DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        }
        SaveOAuth(oauth);

        var reg = LoadTokens();
        bool changedTokens = false;
        var famTokens = reg.Tokens.Where(t => t.FamilyId == familyId).OrderByDescending(t => t.CreatedAt).ToList();
        if (famTokens.Count > 0)
        {
            var latest = famTokens[0];
            latest.Revoked = false;
            if (!string.IsNullOrEmpty(latest.ExpiresAt) && DateTime.TryParse(latest.ExpiresAt, out var tExp) && tExp.ToUniversalTime() <= DateTime.UtcNow)
            {
                latest.ExpiresAt = DateTime.UtcNow.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            }
            changedTokens = true;
        }
        if (changedTokens)
        {
            SaveTokens(reg);
        }
        return true;
    }

    public static bool DeleteOAuthFamily(string familyId)
    {
        var oauth = LoadOAuth();
        var fam = oauth.Families.FirstOrDefault(f => f.Id == familyId);
        if (fam == null) return false;

        string clientId = fam.ClientId;
        oauth.Families.RemoveAll(f => f.Id == familyId);
        if (!string.IsNullOrEmpty(clientId) && !oauth.Families.Any(f => f.ClientId == clientId))
        {
            oauth.Clients.RemoveAll(c => c.ClientId == clientId);
        }
        SaveOAuth(oauth);

        var reg = LoadTokens();
        int removedTok = reg.Tokens.RemoveAll(t => t.FamilyId == familyId);
        if (removedTok > 0)
        {
            SaveTokens(reg);
        }
        return true;
    }

    public static bool UpdateOAuthFamilyScope(string familyId, string newScope)
    {
        string cleanScope = newScope is "read" or "readwrite" or "full" ? newScope : "full";
        var oauth = LoadOAuth();
        var fam = oauth.Families.FirstOrDefault(f => f.Id == familyId);
        if (fam == null) return false;

        fam.Scope = cleanScope;
        fam.OauthScope = "mcp." + cleanScope;
        SaveOAuth(oauth);

        var reg = LoadTokens();
        bool changedTokens = false;
        foreach (var t in reg.Tokens)
        {
            if (t.FamilyId == familyId && t.Scope != cleanScope)
            {
                t.Scope = cleanScope;
                changedTokens = true;
            }
        }
        if (changedTokens)
        {
            SaveTokens(reg);
        }
        return true;
    }

    public static void RegisterPendingOAuthScopeOverride(string? clientId, string chosenScope)
    {
        string cleanScope = chosenScope is "read" or "readwrite" or "full" ? chosenScope : "full";
        DateTime startedAt = DateTime.UtcNow;

        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 90; attempt++) // up to ~22 seconds while client exchanges code at /oauth/token
            {
                await Task.Delay(250);
                try
                {
                    var oauth = LoadOAuth();
                    var targetFam = oauth.Families
                        .Where(f => !f.Revoked && (string.IsNullOrEmpty(clientId) || f.ClientId == clientId))
                        .OrderByDescending(f => f.CreatedAt)
                        .FirstOrDefault();

                    if (targetFam != null &&
                        DateTime.TryParse(targetFam.CreatedAt, out var cdt) &&
                        (DateTime.UtcNow - cdt.ToUniversalTime()).TotalSeconds <= 60)
                    {
                        if (targetFam.Scope != cleanScope)
                        {
                            UpdateOAuthFamilyScope(targetFam.Id, cleanScope);
                        }
                        else
                        {
                            // Ensure any issued token in tokens.json also matches
                            UpdateOAuthFamilyScope(targetFam.Id, cleanScope);
                        }
                        break;
                    }
                }
                catch { }
            }
        });
    }

    public static (string StatusText, Color StatusColor, bool IsActive) GetTokenStatus(TokenEntry t)
    {
        if (t.Revoked)
            return (I18n.T("Revoked", "Отозван"), Theme.Danger, false);
        if (!string.IsNullOrEmpty(t.ExpiresAt) && DateTime.TryParse(t.ExpiresAt, out var exp) && exp.ToUniversalTime() <= DateTime.UtcNow)
            return (I18n.T("Expired", "Истёк"), Theme.Warning, false);
        if (t.MaxRequests.HasValue && t.UsedRequests >= t.MaxRequests.Value)
            return (I18n.T("Exhausted", "Лимит исчерпан"), Theme.Warning, false);
        return (I18n.T("Active", "Активен"), Theme.Success, true);
    }

    public static (string StatusText, Color StatusColor, bool IsActive) GetFamilyStatus(OAuthFamilyEntry f)
    {
        if (f.Revoked)
            return (I18n.T("Disabled", "Отключена"), Theme.Danger, false);
        if (!string.IsNullOrEmpty(f.ExpiresAt) && DateTime.TryParse(f.ExpiresAt, out var exp) && exp.ToUniversalTime() <= DateTime.UtcNow)
            return (I18n.T("Expired", "Истекла"), Theme.Warning, false);
        return (I18n.T("Active", "Активна"), Theme.Success, true);
    }

    public static string FormatScope(string? scope) => FormatScopeRu(scope);
    public static string FormatScopeRu(string? scope) => (scope ?? "").ToLowerInvariant() switch
    {
        "read" => I18n.T("Read-only: files and journals", "Только чтение файлов и журналов"),
        "readwrite" => I18n.T("Read and write files (no PowerShell commands)", "Чтение и запись файлов (без команд PowerShell)"),
        _ => I18n.T("Full access (including running PowerShell commands)", "Полный доступ (включая выполнение команд PowerShell)"),
    };

    public static string FormatScopeShort(string? scope) => FormatScopeShortRu(scope);
    public static string FormatScopeShortRu(string? scope) => (scope ?? "").ToLowerInvariant() switch
    {
        "read" => I18n.T("read", "чтение"),
        "readwrite" => I18n.T("read+write", "чтение+запись"),
        _ => I18n.T("full", "полный"),
    };
}

// ============================================================ DIALOG: Create Bearer Token
internal sealed class CreateTokenDialog : Form
{
    public string TokenLabel => _txtLabel.Text.Trim();
    public string SelectedScope { get; private set; } = "full";
    public int? TtlDays => int.TryParse(_txtTtl.Text.Trim(), out int d) && d > 0 ? d : null;
    public int? MaxRequests => int.TryParse(_txtMaxReq.Text.Trim(), out int m) && m > 0 ? m : null;
    public string? RootFolder => string.IsNullOrWhiteSpace(_txtRoot.Text) ? null : _txtRoot.Text.Trim();

    private readonly TextBox _txtLabel;
    private readonly TextBox _txtTtl;
    private readonly TextBox _txtMaxReq;
    private readonly TextBox _txtRoot;
    private readonly AccessScopeButton _btnScopeRead;
    private readonly AccessScopeButton _btnScopeRw;
    private readonly AccessScopeButton _btnScopeFull;

    public CreateTokenDialog(string defaultProjectDir)
    {
        Text = I18n.Format("Create access token — {0}", "Создать токен доступа — {0}", Theme.ProductName);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(640, 664);
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        Theme.StyleForm(this);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        var close = new AccessActionButton(ButtonKind.Subtle, IconKind.Close)
        {
            Bounds = new Rectangle(568, 18, 44, 44),
            AccessibleName = I18n.T("Close", "Закрыть"),
            DialogResult = DialogResult.Cancel,
            TabIndex = 10,
        };
        close.Click += (_, _) => Close();
        Controls.Add(close);

        _txtLabel = AddField(new Rectangle(28, 138, 584, 44), I18n.T("My agent", "Мой агент"), I18n.T("Token label", "Метка токена"), 0);
        _btnScopeRead = MakeScopeButton(new Rectangle(28, 250, 184, 72), "read", I18n.T("Read", "Чтение"), I18n.T("Read-only access", "Только чтение"), IconKind.Eye, 1);
        _btnScopeRw = MakeScopeButton(new Rectangle(228, 250, 184, 72), "readwrite", I18n.T("Read + write", "Чтение + запись"), I18n.T("Files, no commands", "Файлы, без команд"), IconKind.Edit, 2);
        _btnScopeFull = MakeScopeButton(new Rectangle(428, 250, 184, 72), "full", I18n.T("Full access", "Полный доступ"), I18n.T("Files and commands", "Файлы и команды"), IconKind.Terminal, 3);
        UpdateScopeButtons();

        _txtTtl = AddField(new Rectangle(28, 372, 280, 44), "30", I18n.T("Lifetime in days", "Срок жизни в днях"), 4, I18n.T("days", "дней"));
        _txtMaxReq = AddField(new Rectangle(332, 372, 280, 44), "", I18n.T("Request limit", "Лимит запросов"), 5);
        _txtRoot = AddField(new Rectangle(28, 490, 444, 44), "", I18n.T("Root restriction folder", "Папка-ограничение root"), 6);
        _txtRoot.Font = Theme.Mono;

        var browse = new AccessActionButton(ButtonKind.Secondary)
        {
            Text = I18n.T("Browse…", "Обзор…"), Bounds = new Rectangle(484, 490, 128, 44), TabIndex = 7,
        };
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = string.IsNullOrWhiteSpace(_txtRoot.Text) ? defaultProjectDir : _txtRoot.Text };
            if (dlg.ShowDialog(this) == DialogResult.OK) _txtRoot.Text = dlg.SelectedPath;
        };
        Controls.Add(browse);

        var cancel = new AccessActionButton(ButtonKind.Secondary)
        {
            Text = I18n.T("Cancel", "Отмена"), DialogResult = DialogResult.Cancel,
            Bounds = new Rectangle(312, 600, 112, 44), TabIndex = 8,
        };
        var create = new AccessActionButton(ButtonKind.Primary)
        {
            Text = I18n.T("Create token", "Создать токен"), Bounds = new Rectangle(436, 600, 176, 44), TabIndex = 9,
        };
        create.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_txtLabel.Text))
            {
                MessageBox.Show(this, I18n.T("Enter a token label (name).", "Введите метку (название) токена."), I18n.T("Token creation", "Создание токена"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };
        Controls.Add(cancel);
        Controls.Add(create);
        AcceptButton = create;
        CancelButton = cancel;
        Shown += (_, _) => _txtLabel.Focus();
    }

    private TextBox AddField(Rectangle bounds, string text, string label, int tabIndex, string suffix = "")
    {
        var frame = new AccessInputFrame(suffix) { Bounds = bounds, TabIndex = tabIndex };
        frame.Editor.Text = text;
        frame.Editor.AccessibleName = label;
        Controls.Add(frame);
        return frame.Editor;
    }

    private AccessScopeButton MakeScopeButton(Rectangle bounds, string key, string title, string subtitle, IconKind icon, int tabIndex)
    {
        var b = new AccessScopeButton(title, subtitle, icon) { Bounds = bounds, TabIndex = tabIndex };
        b.Click += (_, _) => { SelectedScope = key; UpdateScopeButtons(); };
        Controls.Add(b);
        return b;
    }

    private void UpdateScopeButtons()
    {
        _btnScopeRead.Selected = SelectedScope == "read";
        _btnScopeRw.Selected = SelectedScope == "readwrite";
        _btnScopeFull.Selected = SelectedScope == "full";
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AccessDialogDrawing.RoundWindow(this);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && e.Y < 88) AccessDialogDrawing.Drag(Handle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        Theme.PrepareGraphics(g);
        g.Clear(Theme.Bg1);
        AccessDialogDrawing.Header(g, Width, IconKind.Access,
            I18n.T("Create access token", "Создать токен доступа"),
            I18n.T("Set access rights and limits for an app or agent.", "Настройте права и ограничения для приложения или агента."));

        AccessDialogDrawing.Label(g, I18n.T("Token label", "Метка токена"), 28, 108, 584);
        AccessDialogDrawing.Hint(g, I18n.T("For example, Claude Desktop or Web Agent", "Например, Claude Desktop или Web Agent"), 28, 186, 584);
        AccessDialogDrawing.Label(g, I18n.T("Access scope", "Область доступа"), 28, 220, 584);
        AccessDialogDrawing.Label(g, I18n.T("Lifetime", "Срок действия"), 28, 342, 280);
        AccessDialogDrawing.Label(g, I18n.T("Request limit", "Лимит запросов"), 332, 342, 280);
        AccessDialogDrawing.Hint(g, I18n.T("Empty = never expires", "Пусто — бессрочно"), 28, 420, 280);
        AccessDialogDrawing.Hint(g, I18n.T("Empty = unlimited", "Пусто — без лимита"), 332, 420, 280);
        AccessDialogDrawing.Label(g, I18n.T("Root restriction folder", "Папка-ограничение root"), 28, 460, 584);
        AccessDialogDrawing.Hint(g, I18n.T("Empty = no folder restriction", "Пусто — без ограничения папки"), 28, 538, 584);
        using (var pen = new Pen(Theme.Divider)) g.DrawLine(pen, 28, 580, Width - 28, 580);
        AccessDialogDrawing.Border(g, ClientRectangle);
    }
}

// ============================================================ DIALOG: Show Token Once
internal sealed class ShowTokenOnceDialog : Form
{
    public ShowTokenOnceDialog(string title, TokenEntry entry, string plaintextToken)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(520, 250);
        BackColor = Theme.Bg1;
        ForeColor = Color.White;
        Font = Theme.Font(10.0f);

        var lblWarn = new Label
        {
            Text = I18n.T("⚠ Save the token secret now! tokens.json stores only its SHA-256 hash — you will not be able to see this token again.", "⚠ Сохраните секрет токена сейчас! В файле tokens.json хранится только его SHA-256 хеш — повторно увидеть этот токен невозможно."),
            Location = new Point(20, 16),
            Size = new Size(480, 40),
            ForeColor = Theme.WarningText,
            Font = Theme.Font(9.75f, true),
        };
        Controls.Add(lblWarn);

        var lblMeta = new Label
        {
            Text = I18n.Format("Label: {0}  ·  ID: {1}  ·  Scope: {2}", "Метка: {0}  ·  ID: {1}  ·  Scope: {2}", entry.Label, entry.Id, entry.Scope),
            Location = new Point(20, 62),
            Size = new Size(480, 22),
            ForeColor = Theme.TextSecondary,
            Font = Theme.Font(9.5f),
        };
        Controls.Add(lblMeta);

        var txtToken = new TextBox
        {
            Text = plaintextToken,
            ReadOnly = true,
            Location = new Point(20, 92),
            Size = new Size(480, 30),
            BackColor = Theme.Bg0,
            ForeColor = Theme.SuccessText,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.FontMono(11.0f, true),
        };
        Controls.Add(txtToken);

        var lblCopied = new Label
        {
            Text = "",
            Location = new Point(20, 130),
            Size = new Size(480, 20),
            ForeColor = Theme.SuccessText,
            Font = Theme.Font(9.5f, true),
        };
        Controls.Add(lblCopied);

        var btnCopy = new Button
        {
            Text = I18n.T("Copy token", "Скопировать токен"),
            Location = new Point(20, 164),
            Size = new Size(180, 36),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Accent,
            ForeColor = Color.White,
            Font = Theme.Font(10.0f, true),
            Cursor = Cursors.Hand,
        };
        btnCopy.FlatAppearance.BorderSize = 0;
        btnCopy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(plaintextToken);
                lblCopied.Text = I18n.T("✓ Copied to clipboard", "✓ Скопировано в буфер обмена");
            }
            catch { }
        };
        Controls.Add(btnCopy);

        var btnClose = new Button
        {
            Text = I18n.T("I saved it, close", "Я сохранил, закрыть"),
            DialogResult = DialogResult.OK,
            Location = new Point(340, 164),
            Size = new Size(160, 36),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.SurfaceRaised,
            ForeColor = Color.White,
            Font = Theme.Font(9.75f, true),
            Cursor = Cursors.Hand,
        };
        btnClose.FlatAppearance.BorderSize = 0;
        Controls.Add(btnClose);
        AcceptButton = btnCopy;
        CancelButton = btnClose;
        Theme.StyleForm(this);
        Theme.StyleTextBox(txtToken);
        txtToken.Font = Theme.Mono;
        txtToken.ForeColor = Theme.SuccessText;
        Theme.StyleButton(btnCopy, ButtonKind.Primary);
        Theme.StyleButton(btnClose, ButtonKind.Secondary);
        btnClose.Font = Theme.SmallStrong;
    }
}

// ============================================================ WINDOW: Missed Approval Requests
internal sealed class MissedRequestsForm : Form
{
    private readonly TrayContext _ctx;
    private List<MissedApprovalItem> _items = new();
    private readonly ToolTip _toolTip = new();
    private string? _lastHoveredTarget;
    private record HitZone(RectangleF Bounds, string Id, Action OnClick, string? Tooltip = null);
    private readonly List<HitZone> _zones = new();
    private int _scrollY;
    private int _maxScrollY;
    private string? _hoveredZoneId;
    private readonly AccessActionButton _clearButton;

    public MissedRequestsForm(TrayContext ctx)
    {
        _ctx = ctx;
        Text = I18n.Format("Missed approval requests — {0}", "Пропущенные запросы подтверждения — {0}", Theme.ProductName);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 560);
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        Theme.StyleForm(this);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        var close = new AccessActionButton(ButtonKind.Subtle, IconKind.Close)
        {
            Bounds = new Rectangle(688, 18, 44, 44), AccessibleName = I18n.T("Close", "Закрыть"),
            DialogResult = DialogResult.Cancel, TabIndex = 1,
        };
        close.Click += (_, _) => Close();
        Controls.Add(close);
        CancelButton = close;
        _clearButton = new AccessActionButton(ButtonKind.Secondary)
        {
            Text = I18n.T("Clear list", "Очистить список"), Bounds = new Rectangle(572, 104, 160, 44), TabIndex = 0, Visible = false,
        };
        _clearButton.Click += async (_, _) =>
        {
            await _ctx.ClearMissedApprovalsAsync();
            await ReloadAsync();
        };
        Controls.Add(_clearButton);
        Shown += async (_, _) => await ReloadAsync();
        MouseWheel += (_, e) =>
        {
            if (_maxScrollY <= 0) return;
            int next = Math.Clamp(_scrollY - (e.Delta > 0 ? 44 : -44), 0, _maxScrollY);
            if (next != _scrollY) { _scrollY = next; Invalidate(); }
        };
        MouseMove += (_, e) =>
        {
            var z = _zones.LastOrDefault(h => h.Bounds.Contains(e.Location));
            string? id = z?.Id;
            if (id != _hoveredZoneId)
            {
                _hoveredZoneId = id;
                if (z?.Tooltip != null && z.Tooltip != _lastHoveredTarget)
                {
                    _lastHoveredTarget = z.Tooltip;
                    _toolTip.SetToolTip(this, z.Tooltip);
                }
                else if (z?.Tooltip == null && _lastHoveredTarget != null)
                {
                    _lastHoveredTarget = null;
                    _toolTip.SetToolTip(this, null);
                }
            }
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AccessDialogDrawing.RoundWindow(this);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && e.Y < 88) AccessDialogDrawing.Drag(Handle);
    }

    public async Task ReloadAsync()
    {
        _items = await _ctx.FetchMissedApprovalsAsync();
        _clearButton.Visible = _items.Count > 0;
        Invalidate();
    }

    public void RenderToBitmapFile(string filePath, List<MissedApprovalItem>? sampleItems = null)
    {
        if (sampleItems != null) _items = sampleItems;
        _clearButton.Visible = _items.Count > 0;
        using var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            DrawContent(g);
            foreach (Control c in Controls)
            {
                if (ReferenceEquals(c, _clearButton) && _items.Count == 0) continue;
                using var cb = new Bitmap(c.Width, c.Height);
                c.DrawToBitmap(cb, new Rectangle(0, 0, c.Width, c.Height));
                g.DrawImageUnscaled(cb, c.Location);
            }
        }
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        bmp.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        DrawContent(e.Graphics);
    }

    private void DrawContent(Graphics g)
    {
        _zones.Clear();
        Theme.PrepareGraphics(g);
        g.Clear(Theme.Bg1);
        AccessDialogDrawing.Header(g, Width, IconKind.Clock,
            I18n.T("Missed requests", "Пропущенные запросы"),
            I18n.T("Requests declined by timeout or while the tray was unavailable.", "Запросы, отклонённые по таймауту или при недоступном трее."));

        Theme.DrawText(g, I18n.Format("Requests: {0}", "Запросов: {0}", _items.Count), Theme.BodyStrong, Theme.TextSecondary,
            new RectangleF(28, 108, 400, 36));
        var clipRect = new RectangleF(24, 160, Width - 48, Height - 184);
        var state = g.Save();
        g.SetClip(clipRect);
        float y = 160f - _scrollY;
        float totalH = 0;
        if (_items.Count == 0)
        {
            float cx = Width / 2f;
            Theme.FillRound(g, new RectangleF(cx - 28, 242, 56, 56), 14, Theme.SurfaceRaised);
            Theme.DrawIcon(g, IconKind.Clock, new RectangleF(cx - 13, 257, 26, 26), Theme.TextTertiary, 1.8f);
            Theme.DrawText(g, I18n.T("No missed requests", "Пропущенных запросов нет"), Theme.Title, Theme.TextPrimary,
                new RectangleF(64, 320, Width - 128, 30), StringAlignment.Center);
            Theme.DrawText(g, I18n.T("Requests that time out or arrive while the tray is unavailable will appear here.",
                "Здесь появятся запросы без ответа или поступившие при недоступном трее."), Theme.Body, Theme.TextSecondary,
                new RectangleF(138, 360, Width - 276, 60), StringAlignment.Center, StringAlignment.Near, wrap: true);
        }
        else
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                var item = _items[i];
                bool hasNote = !string.IsNullOrWhiteSpace(item.Note);
                float cardH = hasNote ? 138f : 108f;
                var card = new RectangleF(28, y, Width - 56, cardH);
                Color sem = item.Reason == "unavailable" ? Theme.DangerText : Theme.WarningText;
                Color baseCol = item.Reason == "unavailable" ? Theme.Danger : Theme.Warning;
                Theme.FillRound(g, card, Theme.RadiusCard, Theme.Surface);
                Theme.StrokeRound(g, card, Theme.RadiusCard, Theme.Border);
                Theme.FillRound(g, new RectangleF(card.X + 16, y + 16, 32, 32), 8, Theme.WithAlpha(baseCol, 27));
                Theme.DrawIcon(g, item.Reason == "unavailable" ? IconKind.Warning : IconKind.Clock,
                    new RectangleF(card.X + 24, y + 24, 16, 16), sem, 1.6f);

                string reason = item.Reason == "unavailable" ? I18n.T("Tray unavailable", "Трей недоступен") : I18n.T("Timed out", "Таймаут");
                float badgeW = Theme.MeasureChipWidth(g, reason, Theme.SmallStrong);
                var badge = new RectangleF(card.Right - badgeW - 16, y + 16, badgeW, 24);
                Theme.DrawChip(g, badge, reason, sem, Theme.WithAlpha(baseCol, 26));
                Theme.DrawText(g, item.Tool, Theme.BodyStrong, Theme.TextPrimary,
                    new RectangleF(card.X + 60, y + 12, Math.Max(20, badge.Left - card.X - 76), 24));

                string localTime = DateTime.TryParse(item.Ts, out var dt) ? dt.ToLocalTime().ToString("dd.MM HH:mm:ss") : item.Ts;
                string tokenSuffix = !string.IsNullOrEmpty(item.Token) && item.Token != "legacy"
                    ? I18n.Format("  ·  token: {0}", "  ·  токен: {0}", item.Token) : "";
                Theme.DrawText(g, localTime + tokenSuffix, Theme.Small, Theme.TextSecondary,
                    new RectangleF(card.X + 60, y + 36, card.Width - 76, 20));

                var targetBox = new RectangleF(card.X + 16, y + 64, card.Width - 32, 28);
                Theme.FillRound(g, targetBox, 6, Theme.Bg1);
                Theme.DrawText(g, item.Target, Theme.Mono, Theme.TextPrimary, new RectangleF(targetBox.X + 10, targetBox.Y, targetBox.Width - 20, targetBox.Height));
                _zones.Add(new HitZone(targetBox, "target_" + i, () => { }, item.Target));
                if (hasNote)
                {
                    var noteRect = new RectangleF(card.X + 16, y + 102, card.Width - 32, 22);
                    Theme.DrawText(g, item.Note, Theme.Small, Theme.TextSecondary, noteRect);
                    _zones.Add(new HitZone(noteRect, "note_" + i, () => { }, item.Note));
                }
                y += cardH + 12;
                totalH += cardH + 12;
            }
        }
        g.Restore(state);
        _maxScrollY = Math.Max(0, (int)(totalH - 12 - clipRect.Height));
        if (_maxScrollY > 0)
        {
            var track = new RectangleF(Width - 14, clipRect.Y, 3, clipRect.Height);
            float thumbH = Math.Max(32, clipRect.Height * clipRect.Height / Math.Max(1, totalH));
            float thumbY = track.Y + (track.Height - thumbH) * Math.Clamp(_scrollY / (float)_maxScrollY, 0, 1);
            Theme.FillRound(g, track, 1.5f, Theme.Trough);
            Theme.FillRound(g, new RectangleF(track.X, thumbY, 3, thumbH), 1.5f, Theme.TextTertiary);
        }
        AccessDialogDrawing.Border(g, ClientRectangle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }
}

// ============================================================ Shared access-window controls
// Owner-drawn presentation around standard WinForms inputs/buttons: native text editing,
// keyboard navigation, AcceptButton/CancelButton and DialogResult semantics are retained.
internal sealed class AccessInputFrame : UserControl
{
    public TextBox Editor { get; } = new();
    private readonly string _suffix;

    public AccessInputFrame(string suffix)
    {
        _suffix = suffix;
        DoubleBuffered = true;
        BackColor = Theme.Bg1;
        Cursor = Cursors.IBeam;
        TabStop = false;
        Editor.BorderStyle = BorderStyle.None;
        Editor.BackColor = Theme.Surface;
        Editor.ForeColor = Theme.TextPrimary;
        Editor.Font = Theme.Body;
        Editor.TabIndex = 0;
        Editor.Enter += (_, _) => Invalidate();
        Editor.Leave += (_, _) => Invalidate();
        Controls.Add(Editor);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) Editor.Focus();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        Editor.Location = new Point(14, Math.Max(0, (Height - Editor.PreferredHeight) / 2));
        Editor.Width = Math.Max(20, Width - 28 - (string.IsNullOrEmpty(_suffix) ? 0 : 62));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        Theme.PrepareGraphics(g);
        g.Clear(BackColor);
        var r = new RectangleF(.5f, .5f, Width - 1f, Height - 1f);
        Theme.FillRound(g, r, Theme.RadiusControl, Theme.Surface);
        Theme.StrokeRound(g, r, Theme.RadiusControl, Editor.Focused ? Theme.Accent : Theme.BorderStrong, Editor.Focused ? 1.5f : 1f);
        if (!string.IsNullOrEmpty(_suffix))
            Theme.DrawText(g, _suffix, Theme.Small, Theme.TextSecondary, new RectangleF(Width - 72, 0, 58, Height), StringAlignment.Far);
    }
}

internal sealed class AccessActionButton : Button
{
    private readonly ButtonKind _kind;
    private readonly IconKind? _icon;
    private bool _hover;
    private bool _pressed;

    public AccessActionButton(ButtonKind kind, IconKind? icon = null)
    {
        _kind = kind;
        _icon = icon;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Theme.Bg1;
        ForeColor = Theme.TextPrimary;
        Font = Theme.BodyStrong;
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _pressed = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.PrepareGraphics(g);
        g.Clear(BackColor);
        var state = !Enabled ? ButtonState.Disabled : _pressed ? ButtonState.Pressed : _hover ? ButtonState.Hover : ButtonState.Normal;
        var r = new RectangleF(.5f, .5f, Width - 1f, Height - 1f);
        Theme.DrawButton(g, r, Text, _kind, state, Font);
        if (_icon is IconKind icon)
            Theme.DrawIcon(g, icon, new RectangleF((Width - 16) / 2f, (Height - 16) / 2f, 16, 16), Enabled ? Theme.TextSecondary : Theme.TextDisabled, 1.7f);
        if (Focused && ShowFocusCues) Theme.StrokeRound(g, RectangleF.Inflate(r, -3, -3), 5, Theme.AccentText, 1f);
    }
}

internal sealed class AccessScopeButton : Button
{
    private bool _selected;
    private bool _hover;
    private readonly string _subtitle;
    private readonly IconKind _icon;
    public bool Selected { get => _selected; set { _selected = value; Invalidate(); } }

    public AccessScopeButton(string title, string subtitle, IconKind icon)
    {
        Text = title;
        _subtitle = subtitle;
        _icon = icon;
        AccessibleName = title + ". " + subtitle;
        AccessibleRole = AccessibleRole.RadioButton;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Theme.Bg1;
        ForeColor = Theme.TextPrimary;
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.PrepareGraphics(g);
        g.Clear(Theme.Bg1);
        var r = new RectangleF(.5f, .5f, Width - 1f, Height - 1f);
        Theme.FillRound(g, r, Theme.RadiusControl, _selected ? Theme.Composite(Theme.WithAlpha(Theme.Accent, 25), Theme.Bg1) : _hover ? Theme.SurfaceRaised : Theme.Surface);
        Theme.StrokeRound(g, r, Theme.RadiusControl, _selected ? Theme.Accent : _hover ? Theme.BorderStrong : Theme.Border, _selected ? 1.5f : 1f);
        Theme.DrawIcon(g, _icon, new RectangleF(14, 14, 16, 16), _selected ? Theme.AccentText : Theme.TextSecondary, 1.7f);
        Theme.DrawText(g, Text, Theme.BodyStrong, _selected ? Theme.TextPrimary : Theme.TextSecondary, new RectangleF(38, 10, Width - 50, 24));
        Theme.DrawText(g, _subtitle, Theme.Small, Theme.TextSecondary, new RectangleF(14, 40, Width - 28, 22));
        if (_selected) Theme.FillRound(g, new RectangleF(14, Height - 6, Width - 28, 2), 1, Theme.Accent);
        if (Focused && ShowFocusCues) Theme.StrokeRound(g, RectangleF.Inflate(r, -3, -3), 5, Theme.AccentText);
    }
}

internal static class AccessDialogDrawing
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    public static void RoundWindow(Form f)
    {
        try { int pref = 2; DwmSetWindowAttribute(f.Handle, 33, ref pref, sizeof(int)); } catch { }
    }
    public static void Drag(IntPtr hwnd) { ReleaseCapture(); SendMessage(hwnd, 0xA1, (IntPtr)2, IntPtr.Zero); }
    public static void Header(Graphics g, int width, IconKind icon, string title, string subtitle)
    {
        Theme.FillRound(g, new RectangleF(28, 24, 40, 40), 10, Theme.AccentSubtle);
        Theme.DrawIcon(g, icon, new RectangleF(38, 34, 20, 20), Theme.AccentText, 1.7f);
        Theme.DrawText(g, title, Theme.Display, Theme.TextPrimary, new RectangleF(84, 19, width - 154, 32));
        Theme.DrawText(g, subtitle, Theme.Small, Theme.TextSecondary, new RectangleF(84, 52, width - 154, 32), vertical: StringAlignment.Near, wrap: true);
        using var p = new Pen(Theme.Divider);
        g.DrawLine(p, 28, 92, width - 28, 92);
    }
    public static void Label(Graphics g, string text, float x, float y, float w) =>
        Theme.DrawText(g, text, Theme.BodyStrong, Theme.TextPrimary, new RectangleF(x, y, w, 22));
    public static void Hint(Graphics g, string text, float x, float y, float w) =>
        Theme.DrawText(g, text, Theme.Small, Theme.TextSecondary, new RectangleF(x, y, w, 22));
    public static void Border(Graphics g, Rectangle r) =>
        Theme.StrokeRound(g, new RectangleF(.5f, .5f, r.Width - 1f, r.Height - 1f), 8, Theme.BorderStrong);
}
