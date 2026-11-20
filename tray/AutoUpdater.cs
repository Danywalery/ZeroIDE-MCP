using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZeroIDE.Mcp;

internal enum UpdateStage
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToInstall,
    WaitingForServerOff,
    Installing,
    Error,
}

internal sealed record ReleaseAssetInfo(
    string Tag,
    string Version,
    string Title,
    string HtmlUrl,
    string ZipUrl,
    string Sha256Url,
    string PublishedAt,
    string NotesSummary,
    string ReleaseNotes = "",
    bool IsDemo = false);

internal sealed record UpdateStateSnapshot(
    UpdateStage Stage,
    string CurrentVersion,
    ReleaseAssetInfo? Latest,
    int ProgressPercent,
    string Message,
    DateTimeOffset? LastCheckedAt,
    string PreparedPackageDir,
    bool BannerDismissed)
{
    public bool HasUpdate => Latest != null && Stage is UpdateStage.Available or UpdateStage.Downloading or UpdateStage.ReadyToInstall or UpdateStage.WaitingForServerOff or UpdateStage.Installing;
    public bool ShowBanner => HasUpdate && !BannerDismissed;
}

/// <summary>
/// Checks GitHub Releases for Danywalery/ZeroIDE-MCP, verifies SHA-256 against
/// zeroide-mcp.zip.sha256, safely extracts the release archive, and applies the
/// update via installer/install.ps1 -Update only when the MCP server is offline
/// (or when the owner explicitly confirms).
/// </summary>
internal sealed class AppUpdater : IDisposable
{
    public const string RepoOwner = "Danywalery";
    public const string RepoName = "ZeroIDE-MCP";
    public const string RepoUrl = "https://github.com/" + RepoOwner + "/" + RepoName;
    public const string ReleasesPageUrl = RepoUrl + "/releases";
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/" + RepoOwner + "/" + RepoName + "/releases/latest";

    private static readonly string[] TrustedHosts =
    {
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "github-releases.githubusercontent.com",
    };

    private static readonly string[] ForbiddenPackageFiles =
    {
        "config.env",
        "tray-config.json",
        "tokens.json",
        "oauth.json",
        "permissions.json",
        "telegram.json",
        "connections-v2.json",
        "connection-client.json",
    };

    private static readonly string[] RequiredPackageEntries =
    {
        "ZeroIDE-MCP.exe",
        Path.Combine("server", "server.mjs"),
        Path.Combine("installer", "install.ps1"),
    };

    private static readonly Regex SemVerRe = new(
        @"^v?(?<maj>\d+)\.(?<min>\d+)(?:\.(?<pat>\d+))?(?:-(?<pre>[0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Sha256Re = new(@"\b([0-9a-fA-F]{64})\b", RegexOptions.Compiled);

    public const string FallbackVersion = "0.5.0";
    public static string CurrentVersion { get; } = DetectCurrentVersion();
    public static string ShortVersionLabel => "v" + CurrentVersion;

    private readonly string _installDir;
    private readonly string _currentVersion;
    private readonly Func<string> _getLogDir;
    private readonly Func<bool> _isServerActive;
    private readonly Action<string> _log;
    private readonly Action _exitForUpdate;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private UpdateStateSnapshot _state;
    private bool _disposed;

    public event Action? StateChanged;

    public UpdateStateSnapshot State
    {
        get { lock (_stateLock) return _state; }
    }

    public AppUpdater(
        string installDir,
        Func<string> getLogDir,
        Func<bool> isServerActive,
        Action<string> log,
        Action exitForUpdate,
        HttpMessageHandler? handler = null,
        string? currentVersion = null)
    {
        _installDir = string.IsNullOrWhiteSpace(installDir) ? AppContext.BaseDirectory.TrimEnd('\\', '/') : installDir.TrimEnd('\\', '/');
        _currentVersion = string.IsNullOrWhiteSpace(currentVersion) ? CurrentVersion : currentVersion.Trim().TrimStart('v', 'V');
        _getLogDir = getLogDir;
        _isServerActive = isServerActive;
        _log = log;
        _exitForUpdate = exitForUpdate;
        _http = handler != null ? new HttpClient(handler) : new HttpClient();
        _http.Timeout = TimeSpan.FromMinutes(3);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"ZeroIDE-MCP/{_currentVersion}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _state = new UpdateStateSnapshot(UpdateStage.Idle, _currentVersion, null, 0, "", null, "", false);
    }

    private static string DetectCurrentVersion()
    {
        try
        {
            var asm = typeof(AppUpdater).Assembly;
            if (!string.Equals(asm.GetName().Name, "ZeroIDE-MCP", StringComparison.OrdinalIgnoreCase))
                return FallbackVersion;
            string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                int plus = info.IndexOf('+');
                string clean = (plus >= 0 ? info[..plus] : info).Trim().TrimStart('v', 'V');
                if (TryParseVersion(clean, out _, out _, out _, out _)) return clean;
            }
            var v = asm.GetName().Version;
            if (v != null && (v.Major > 0 || v.Minor > 0 || v.Build > 0))
                return $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
        catch { }
        return FallbackVersion;
    }

    private void SetState(Func<UpdateStateSnapshot, UpdateStateSnapshot> update)
    {
        lock (_stateLock) _state = update(_state);
        try { StateChanged?.Invoke(); } catch { }
    }

    public void DismissBanner()
    {
        SetState(s => s with { BannerDismissed = true });
    }

    /// <summary>Preview / test helper: sets a fixed snapshot without any network calls.</summary>
    internal void SetStateForPreview(UpdateStateSnapshot snapshot)
    {
        lock (_stateLock) _state = snapshot;
        try { StateChanged?.Invoke(); } catch { }
    }

    // ============================================================ PURE HELPERS (TESTABLE)

    internal static bool TryParseVersion(string? raw, out int major, out int minor, out int patch, out string prerelease)
    {
        major = minor = patch = 0;
        prerelease = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var m = SemVerRe.Match(raw.Trim());
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups["maj"].Value, out major)) return false;
        if (!int.TryParse(m.Groups["min"].Value, out minor)) return false;
        patch = m.Groups["pat"].Success && int.TryParse(m.Groups["pat"].Value, out int p) ? p : 0;
        prerelease = m.Groups["pre"].Success ? m.Groups["pre"].Value : "";
        return true;
    }

    /// <summary>Returns true iff candidate is strictly newer than current (stable > prerelease for same numeric triplet).</summary>
    internal static bool IsNewerVersion(string? candidate, string? current)
    {
        if (!TryParseVersion(candidate, out int cMaj, out int cMin, out int cPat, out string cPre)) return false;
        if (!TryParseVersion(current, out int curMaj, out int curMin, out int curPat, out string curPre)) return false;
        if (cMaj != curMaj) return cMaj > curMaj;
        if (cMin != curMin) return cMin > curMin;
        if (cPat != curPat) return cPat > curPat;
        if (string.IsNullOrEmpty(cPre) && !string.IsNullOrEmpty(curPre)) return true;
        if (!string.IsNullOrEmpty(cPre) && string.IsNullOrEmpty(curPre)) return false;
        return string.Compare(cPre, curPre, StringComparison.OrdinalIgnoreCase) > 0;
    }

    internal static bool IsTrustedDownloadUrl(string? rawUrl)
    {
        if (!DesktopLauncher.IsSafeHttpUrl(rawUrl, out string safe)) return false;
        if (!Uri.TryCreate(safe, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        string host = uri.Host.ToLowerInvariant();
        foreach (string allowed in TrustedHosts)
        {
            if (host == allowed || host.EndsWith("." + allowed, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    internal static ReleaseAssetInfo? ParseReleaseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            bool draft = root.TryGetProperty("draft", out var dEl) && dEl.ValueKind == JsonValueKind.True;
            bool prerelease = root.TryGetProperty("prerelease", out var pEl) && pEl.ValueKind == JsonValueKind.True;
            if (draft || prerelease) return null;

            string tag = root.TryGetProperty("tag_name", out var tEl) ? (tEl.GetString() ?? "").Trim() : "";
            if (!TryParseVersion(tag, out int maj, out int min, out int pat, out string pre)) return null;
            string version = string.IsNullOrEmpty(pre) ? $"{maj}.{min}.{pat}" : $"{maj}.{min}.{pat}-{pre}";

            string title = root.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? "").Trim() : "";
            if (string.IsNullOrEmpty(title)) title = "ZeroIDE MCP " + tag;

            string htmlUrl = root.TryGetProperty("html_url", out var hEl) ? (hEl.GetString() ?? "").Trim() : ReleasesPageUrl;
            if (!IsTrustedDownloadUrl(htmlUrl)) htmlUrl = ReleasesPageUrl;

            string publishedAt = root.TryGetProperty("published_at", out var pubEl) ? (pubEl.GetString() ?? "").Trim() : "";
            string rawBody = root.TryGetProperty("body", out var bEl) ? (bEl.GetString() ?? "").Trim() : "";
            string releaseNotes = rawBody.Length > 8000 ? rawBody[..8000] : rawBody;
            string summary = SummarizeNotes(releaseNotes);

            string zipUrl = "";
            string shaUrl = "";
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.TryGetProperty("name", out var an) ? (an.GetString() ?? "").Trim() : "";
                    string url = a.TryGetProperty("browser_download_url", out var au) ? (au.GetString() ?? "").Trim() : "";
                    if (!IsTrustedDownloadUrl(url)) continue;
                    if (name.Equals("zeroide-mcp.zip", StringComparison.OrdinalIgnoreCase)) zipUrl = url;
                    else if (name.Equals("zeroide-mcp.zip.sha256", StringComparison.OrdinalIgnoreCase)) shaUrl = url;
                }
            }

            return new ReleaseAssetInfo(tag, version, title, htmlUrl, zipUrl, shaUrl, publishedAt, summary, releaseNotes);
        }
        catch (JsonException) { return null; }
    }

    private static string SummarizeNotes(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        string? headingFallback = null;
        foreach (string rawLine in body.Replace("\r\n", "\n").Split('\n'))
        {
            string trimmed = rawLine.Trim();
            if (trimmed.Length == 0) continue;
            bool isHeading = trimmed.StartsWith('#');
            string line = trimmed.TrimStart('#', '-', '*', '•', ' ').Trim();
            if (line.Length < 8) continue;
            if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
            string clipped = line.Length > 140 ? line[..137] + "…" : line;
            if (!isHeading) return clipped;
            headingFallback ??= clipped;
        }
        return headingFallback ?? "";
    }

    internal static string? ParseExpectedSha256(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Sha256Re.Match(text);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    internal static string ComputeFileSha256(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static string SafeExtractAndValidatePackage(string zipPath, string extractDir)
    {
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
        Directory.CreateDirectory(extractDir);
        string fullRoot = Path.GetFullPath(extractDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        using (var archive = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.FullName)) continue;
                string normalized = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                string target = Path.GetFullPath(Path.Combine(extractDir, normalized));
                if (!target.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Archive contains an unsafe path: " + entry.FullName);

                if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                string fileName = Path.GetFileName(target);
                if (ForbiddenPackageFiles.Any(f => f.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                    || fileName.EndsWith(".token", StringComparison.OrdinalIgnoreCase)
                    || fileName.StartsWith("tunnel_key", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Archive contains forbidden machine-local state: " + fileName);
                }

                string? parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                entry.ExtractToFile(target, overwrite: true);
            }
        }

        // Standard release zip has one top-level folder zeroide-mcp/; also tolerate root-level layout.
        string pkgRoot = Path.Combine(extractDir, "zeroide-mcp");
        if (!Directory.Exists(pkgRoot) && File.Exists(Path.Combine(extractDir, "ZeroIDE-MCP.exe")))
            pkgRoot = extractDir;

        foreach (string req in RequiredPackageEntries)
        {
            if (!File.Exists(Path.Combine(pkgRoot, req)))
                throw new InvalidOperationException("Release package is missing required file: " + req);
        }

        return pkgRoot;
    }

    internal static string BuildApplyScript(int parentPid, string packageRoot, string installDir, string stagingDir, string logFile, string? instanceId)
    {
        static string PsQuote(string s) => "'" + (s ?? "").Replace("'", "''") + "'";
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$parentPid = {parentPid}");
        sb.AppendLine($"$pkgRoot = {PsQuote(packageRoot)}");
        sb.AppendLine($"$installDir = {PsQuote(installDir)}");
        sb.AppendLine($"$stagingDir = {PsQuote(stagingDir)}");
        sb.AppendLine($"$logFile = {PsQuote(logFile)}");
        sb.AppendLine($"$instanceId = {PsQuote(instanceId ?? "")}");
        sb.AppendLine("function Write-Log([string]$m) { try { Add-Content -LiteralPath $logFile -Value ((Get-Date -Format 's') + ' [updater] ' + $m) -Encoding UTF8 } catch { } }");
        sb.AppendLine("Write-Log ('Starting update into ' + $installDir)");
        sb.AppendLine("for ($i = 0; $i -lt 80; $i++) {");
        sb.AppendLine("  if (-not (Get-Process -Id $parentPid -ErrorAction SilentlyContinue)) { break }");
        sb.AppendLine("  Start-Sleep -Milliseconds 250");
        sb.AppendLine("}");
        sb.AppendLine("Start-Sleep -Milliseconds 300");
        sb.AppendLine("try {");
        sb.AppendLine("  $installer = Join-Path $pkgRoot 'installer\\install.ps1'");
        sb.AppendLine("  $cmd = Get-Command $installer -ErrorAction Stop");
        sb.AppendLine("  if ($cmd.Parameters.ContainsKey('InstallDir')) {");
        sb.AppendLine("    & $installer -Update -InstallDir $installDir *>> $logFile");
        sb.AppendLine("  } else {");
        sb.AppendLine("    & $installer -Update *>> $logFile");
        sb.AppendLine("  }");
        sb.AppendLine("  if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw ('install.ps1 exited with ' + $LASTEXITCODE) }");
        sb.AppendLine("  Write-Log 'Update applied successfully'");
        sb.AppendLine("} catch {");
        sb.AppendLine("  Write-Log ('Update failed: ' + $_.Exception.Message)");
        sb.AppendLine("}");
        sb.AppendLine("try {");
        sb.AppendLine("  if ($instanceId) { $env:ZEROIDE_INSTANCE_ID = $instanceId }");
        sb.AppendLine("  $exe = Join-Path $installDir 'ZeroIDE-MCP.exe'");
        sb.AppendLine("  if (Test-Path -LiteralPath $exe) { Start-Process -FilePath $exe -WorkingDirectory $installDir }");
        sb.AppendLine("} catch { Write-Log ('Restart failed: ' + $_.Exception.Message) }");
        sb.AppendLine("try { if ([System.IO.Directory]::Exists($stagingDir)) { [System.IO.Directory]::Delete($stagingDir, $true) } } catch { }");
        return sb.ToString();
    }

    // ============================================================ ASYNC WORKFLOW

    public async Task<UpdateStateSnapshot> CheckAsync(bool autoUpdateEnabled, bool userInitiated = false, CancellationToken ct = default)
    {
        if (_disposed) return State;
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) return State;
        try
        {
            var prev = State;
            SetState(s => s with
            {
                Stage = UpdateStage.Checking,
                ProgressPercent = 0,
                Message = I18n.T("Checking GitHub Releases…", "Проверяем релизы на GitHub…"),
                BannerDismissed = userInitiated ? false : s.BannerDismissed,
            });

            using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            if ((int)resp.StatusCode == 404)
            {
                SetState(s => s with
                {
                    Stage = UpdateStage.UpToDate,
                    Latest = null,
                    LastCheckedAt = DateTimeOffset.Now,
                    Message = I18n.T("No published releases found yet.", "Опубликованных релизов пока нет."),
                });
                return State;
            }
            if (!resp.IsSuccessStatusCode)
            {
                string detail = $"HTTP {(int)resp.StatusCode}";
                _log("update check failed: " + detail);
                SetState(s => s with
                {
                    Stage = UpdateStage.Error,
                    LastCheckedAt = DateTimeOffset.Now,
                    Message = I18n.Format("Could not check updates ({0}).", "Не удалось проверить обновления ({0}).", detail),
                });
                return State;
            }

            string json = await resp.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            var release = ParseReleaseJson(json);
            if (release == null)
            {
                SetState(s => s with
                {
                    Stage = UpdateStage.UpToDate,
                    Latest = null,
                    LastCheckedAt = DateTimeOffset.Now,
                    Message = I18n.Format("You have the latest version ({0}).", "Установлена актуальная версия ({0}).", _currentVersion),
                });
                return State;
            }

            if (!IsNewerVersion(release.Version, _currentVersion))
            {
                SetState(s => s with
                {
                    Stage = UpdateStage.UpToDate,
                    Latest = release,
                    LastCheckedAt = DateTimeOffset.Now,
                    Message = I18n.Format("You have the latest version ({0}).", "Установлена актуальная версия ({0}).", _currentVersion),
                });
                return State;
            }

            bool samePrepared = prev.Latest != null
                && string.Equals(prev.Latest.Version, release.Version, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(prev.PreparedPackageDir)
                && Directory.Exists(prev.PreparedPackageDir);

            _log($"update available: {_currentVersion} -> {release.Version}");
            SetState(s => s with
            {
                Stage = samePrepared ? (_isServerActive() ? UpdateStage.WaitingForServerOff : UpdateStage.ReadyToInstall) : UpdateStage.Available,
                Latest = release,
                LastCheckedAt = DateTimeOffset.Now,
                BannerDismissed = userInitiated ? false : (prev.Latest?.Version == release.Version ? s.BannerDismissed : false),
                PreparedPackageDir = samePrepared ? prev.PreparedPackageDir : "",
                Message = I18n.Format("Version {0} is available.", "Доступна версия {0}.", release.Version),
            });

            if (autoUpdateEnabled && !string.IsNullOrEmpty(release.ZipUrl) && !string.IsNullOrEmpty(release.Sha256Url))
            {
                await DownloadAndPrepareLockedAsync(release, applyIfServerOff: true, ct).ConfigureAwait(false);
            }
            return State;
        }
        catch (OperationCanceledException)
        {
            SetState(s => s with
            {
                Stage = UpdateStage.Error,
                LastCheckedAt = DateTimeOffset.Now,
                Message = I18n.T("Update check timed out.", "Превышено время ожидания проверки обновлений."),
            });
            return State;
        }
        catch (Exception ex)
        {
            _log("update check error: " + ex.GetType().Name);
            SetState(s => s with
            {
                Stage = UpdateStage.Error,
                LastCheckedAt = DateTimeOffset.Now,
                Message = I18n.T("Could not reach GitHub Releases. Check internet access.", "Не удалось связаться с GitHub Releases. Проверьте интернет."),
            });
            return State;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Called when the owner clicks «Update now» / «Install».</summary>
    public async Task<bool> DownloadAndInstallNowAsync(CancellationToken ct = default)
    {
        if (_disposed) return false;
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) return false;
        try
        {
            var rel = State.Latest;
            if (rel == null || !IsNewerVersion(rel.Version, _currentVersion)) return false;

            if (string.IsNullOrEmpty(rel.ZipUrl) || string.IsNullOrEmpty(rel.Sha256Url))
            {
                DesktopLauncher.OpenUrl(rel.HtmlUrl);
                SetState(s => s with
                {
                    Stage = UpdateStage.Available,
                    Message = I18n.T("Release archive or SHA-256 checksum is missing; opened the Releases page.", "В релизе нет архива или файла SHA-256 — открыта страница релиза."),
                });
                return false;
            }

            bool prepared = await DownloadAndPrepareLockedAsync(rel, applyIfServerOff: false, ct).ConfigureAwait(false);
            if (!prepared) return false;
            return LaunchApplyAndExitLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Called when MCP server stops: if auto-update is enabled and an update was waiting, apply it now.</summary>
    public async Task TryAutoApplyAfterServerStopAsync(bool autoUpdateEnabled, CancellationToken ct = default)
    {
        if (_disposed || !autoUpdateEnabled || _isServerActive()) return;
        var cur = State;
        if (cur.Latest == null || !IsNewerVersion(cur.Latest.Version, _currentVersion)) return;
        if (cur.Stage is not (UpdateStage.WaitingForServerOff or UpdateStage.ReadyToInstall or UpdateStage.Available)) return;

        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            if (_isServerActive()) return;
            var latest = State.Latest;
            if (latest == null) return;
            if (string.IsNullOrEmpty(State.PreparedPackageDir) || !Directory.Exists(State.PreparedPackageDir))
            {
                if (string.IsNullOrEmpty(latest.ZipUrl) || string.IsNullOrEmpty(latest.Sha256Url)) return;
                bool ok = await DownloadAndPrepareLockedAsync(latest, applyIfServerOff: false, ct).ConfigureAwait(false);
                if (!ok) return;
            }
            if (_isServerActive())
            {
                SetState(s => s with
                {
                    Stage = UpdateStage.WaitingForServerOff,
                    Message = I18n.Format("Update {0} is ready — will install when MCP server is turned off.", "Обновление {0} готово — установится после выключения MCP-сервера.", latest.Version),
                });
                return;
            }
            LaunchApplyAndExitLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> DownloadAndPrepareLockedAsync(ReleaseAssetInfo release, bool applyIfServerOff, CancellationToken ct)
    {
        try
        {
            if (!string.IsNullOrEmpty(State.PreparedPackageDir) && Directory.Exists(State.PreparedPackageDir))
            {
                if (applyIfServerOff)
                {
                    if (_isServerActive())
                    {
                        SetState(s => s with
                        {
                            Stage = UpdateStage.WaitingForServerOff,
                            Message = I18n.Format("Update {0} is ready — will install when MCP server is turned off.", "Обновление {0} готово — установится после выключения MCP-сервера.", release.Version),
                        });
                        return true;
                    }
                    return LaunchApplyAndExitLocked();
                }
                return true;
            }

            if (!IsTrustedDownloadUrl(release.ZipUrl) || !IsTrustedDownloadUrl(release.Sha256Url))
            {
                SetState(s => s with
                {
                    Stage = UpdateStage.Error,
                    Message = I18n.T("Blocked untrusted download URL.", "Загрузка заблокирована: ненадёжный адрес файла."),
                });
                return false;
            }

            SetState(s => s with
            {
                Stage = UpdateStage.Downloading,
                ProgressPercent = 5,
                Message = I18n.Format("Downloading checksum for {0}…", "Загружаем контрольную сумму {0}…", release.Version),
            });

            string shaBody = await _http.GetStringAsync(release.Sha256Url, ct).ConfigureAwait(false);
            string? expectedSha = ParseExpectedSha256(shaBody);
            if (string.IsNullOrEmpty(expectedSha))
            {
                SetState(s => s with
                {
                    Stage = UpdateStage.Error,
                    Message = I18n.T("Invalid SHA-256 checksum file in the release.", "Некорректный файл контрольной суммы SHA-256 в релизе."),
                });
                return false;
            }

            string stagingDir = Path.Combine(_installDir, "update-staging");
            Directory.CreateDirectory(stagingDir);
            string zipPath = Path.Combine(stagingDir, "zeroide-mcp.zip");
            string extractDir = Path.Combine(stagingDir, "unpacked");

            SetState(s => s with
            {
                Stage = UpdateStage.Downloading,
                ProgressPercent = 10,
                Message = I18n.Format("Downloading ZeroIDE MCP {0}…", "Скачиваем ZeroIDE MCP {0}…", release.Version),
            });

            using (var resp = await _http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1;
                using var netStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buf = new byte[81920];
                long readTotal = 0;
                int lastPct = 10;
                int n;
                while ((n = await netStream.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    readTotal += n;
                    if (total > 0)
                    {
                        int pct = 10 + (int)Math.Clamp((readTotal * 75L) / total, 0, 75);
                        if (pct >= lastPct + 5)
                        {
                            lastPct = pct;
                            SetState(s => s with
                            {
                                ProgressPercent = pct,
                                Message = I18n.Format("Downloading {0}… {1}%", "Скачиваем {0}… {1}%", release.Version, pct),
                            });
                        }
                    }
                }
            }

            SetState(s => s with
            {
                ProgressPercent = 88,
                Message = I18n.T("Verifying SHA-256 integrity…", "Проверяем целостность SHA-256…"),
            });

            string actualSha = ComputeFileSha256(zipPath);
            if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(zipPath); } catch { }
                _log($"update SHA-256 mismatch: expected={expectedSha} actual={actualSha}");
                SetState(s => s with
                {
                    Stage = UpdateStage.Error,
                    ProgressPercent = 0,
                    Message = I18n.T("SHA-256 check failed! Downloaded archive was discarded.", "Ошибка проверки SHA-256! Скачанный архив удалён."),
                });
                return false;
            }

            SetState(s => s with
            {
                ProgressPercent = 94,
                Message = I18n.T("Unpacking update…", "Распаковываем обновление…"),
            });

            string pkgRoot = SafeExtractAndValidatePackage(zipPath, extractDir);
            try { File.Delete(zipPath); } catch { }

            if (applyIfServerOff && _isServerActive())
            {
                _log($"update {release.Version} verified and staged; waiting for MCP server to stop");
                SetState(s => s with
                {
                    Stage = UpdateStage.WaitingForServerOff,
                    ProgressPercent = 100,
                    PreparedPackageDir = pkgRoot,
                    Message = I18n.Format("Update {0} is ready — will install when MCP server is turned off.", "Обновление {0} готово — установится после выключения MCP-сервера.", release.Version),
                });
                return true;
            }

            SetState(s => s with
            {
                Stage = UpdateStage.ReadyToInstall,
                ProgressPercent = 100,
                PreparedPackageDir = pkgRoot,
                Message = I18n.Format("Update {0} verified (SHA-256 OK) and ready.", "Обновление {0} проверено (SHA-256 OK) и готово к установке.", release.Version),
            });

            if (applyIfServerOff && !_isServerActive())
            {
                return LaunchApplyAndExitLocked();
            }
            return true;
        }
        catch (Exception ex)
        {
            _log("update download/prepare error: " + ex.Message);
            SetState(s => s with
            {
                Stage = UpdateStage.Error,
                ProgressPercent = 0,
                Message = I18n.T("Update download failed: ", "Не удалось скачать обновление: ") + ex.Message,
            });
            return false;
        }
    }

    private bool LaunchApplyAndExitLocked()
    {
        string pkgRoot = State.PreparedPackageDir;
        if (string.IsNullOrEmpty(pkgRoot) || !Directory.Exists(pkgRoot)) return false;

        try
        {
            string stagingDir = Path.Combine(_installDir, "update-staging");
            Directory.CreateDirectory(stagingDir);
            string logDir = _getLogDir();
            try { Directory.CreateDirectory(logDir); } catch { }
            string logFile = Path.Combine(logDir, "update.log");
            string scriptPath = Path.Combine(stagingDir, "apply-update.ps1");
            string? instanceId = Environment.GetEnvironmentVariable("ZEROIDE_INSTANCE_ID");
            int pid = Process.GetCurrentProcess().Id;

            string script = BuildApplyScript(pid, pkgRoot, _installDir, stagingDir, logFile, instanceId);
            File.WriteAllText(scriptPath, script, new UTF8Encoding(false));

            SetState(s => s with
            {
                Stage = UpdateStage.Installing,
                ProgressPercent = 100,
                Message = I18n.T("Installing update and restarting…", "Устанавливаем обновление и перезапускаем…"),
            });

            var psi = new ProcessStartInfo("powershell.exe")
            {
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _installDir,
            };
            Process.Start(psi);
            _log("launched apply-update.ps1, exiting tray for update");
            _exitForUpdate();
            return true;
        }
        catch (Exception ex)
        {
            _log("failed to launch updater script: " + ex.Message);
            SetState(s => s with
            {
                Stage = UpdateStage.Error,
                Message = I18n.T("Could not start update installer: ", "Не удалось запустить установщик обновления: ") + ex.Message,
            });
            return false;
        }
    }

    internal static UpdateStateSnapshot CreateDemoUpdateSnapshot()
    {
        bool ru = I18n.IsRu;
        string summary = ru
            ? "Новое окно сравнения файлов, уведомления в Telegram и обновления в 1 клик"
            : "Redesigned diff viewer, Telegram alerts and one-click updates";
        string notes = ru
            ? "## Что нового\n"
              + "- **Уведомления в Telegram:** односторонние алерты о завершении задач, сообщениях агента и запросах подтверждения.\n"
              + "- **Новое окно сравнения файлов (diff):** нумерация строк До/После, перенос длинных строк, весь файл по клику и отмена правки из окна.\n"
              + "- **Проверка и автоустановка обновлений:** контроль SHA-256 релизов с GitHub и безопасное ожидание остановки MCP-сервера.\n\n"
              + "## Исправления\n"
              + "- Исправлена кнопка «Показать в папке» в разделе Изменений (поддержка пробелов и кириллицы в путях).\n"
              + "- На странице ввода токена ngrok/Cloudflare добавлена рабочая кнопка открытия страницы с ключом.\n"
              + "- Раскрытие длинных ответов модели в чате трея и сворачивание переписки.\n\n"
              + "## Безопасность\n"
              + "- Загрузка только с доверенных HTTPS-доменов GitHub Releases с проверкой SHA-256 и защитой от Zip Slip."
            : "## What's new\n"
              + "- **Telegram alerts:** one-way notifications for completed tasks, agent messages and approval requests.\n"
              + "- **Redesigned diff viewer:** Before/After line numbers, word wrap toggle, full-file view and quick undo.\n"
              + "- **Auto-update checks:** SHA-256 verification for GitHub Releases and safe waiting while MCP is running.\n\n"
              + "## Fixes\n"
              + "- Fixed «Show in folder» in Changes for paths with spaces and Unicode characters.\n"
              + "- Added working key page button directly inside the ngrok/Cloudflare token prompt.\n"
              + "- Expandable model answers and collapsible chat in the tray popup.\n\n"
              + "## Security\n"
              + "- Downloads restricted to trusted HTTPS GitHub domains with SHA-256 and Zip Slip verification.";

        var rel = new ReleaseAssetInfo(
            Tag: "v0.6.0",
            Version: "0.6.0",
            Title: "ZeroIDE MCP v0.6.0",
            HtmlUrl: ReleasesPageUrl,
            ZipUrl: "",
            Sha256Url: "",
            PublishedAt: DateTimeOffset.Now.ToString("o"),
            NotesSummary: summary,
            ReleaseNotes: notes,
            IsDemo: true);

        return new UpdateStateSnapshot(
            UpdateStage.Available,
            CurrentVersion,
            rel,
            0,
            I18n.Format("Version {0} is available.", "Доступна версия {0}.", rel.Version),
            DateTimeOffset.Now,
            "",
            false);
    }

    /// <summary>Safe in-UI progress animation for --demo-update: never downloads or restarts.</summary>
    public async Task RunDemoInstallAnimationAsync(CancellationToken ct = default)
    {
        if (_disposed) return;
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            var rel = State.Latest;
            if (rel == null) return;

            (int Pct, string Msg)[] steps =
            {
                (15, I18n.Format("Downloading checksum for {0}…", "Загружаем контрольную сумму {0}…", rel.Version)),
                (48, I18n.Format("Downloading {0}… {1}%", "Скачиваем {0}… {1}%", rel.Version, 48)),
                (82, I18n.Format("Downloading {0}… {1}%", "Скачиваем {0}… {1}%", rel.Version, 82)),
                (94, I18n.T("Verifying SHA-256 integrity…", "Проверяем целостность SHA-256…")),
            };

            foreach (var step in steps)
            {
                SetState(s => s with { Stage = UpdateStage.Downloading, ProgressPercent = step.Pct, Message = step.Msg });
                await Task.Delay(450, ct).ConfigureAwait(false);
            }

            if (_isServerActive())
            {
                SetState(s => s with
                {
                    Stage = UpdateStage.WaitingForServerOff,
                    ProgressPercent = 100,
                    Message = I18n.Format("Demo: {0} verified — waiting for MCP server to stop.", "Демо: {0} проверено (SHA-256 OK) — ждёт выключения MCP-сервера.", rel.Version),
                });
            }
            else
            {
                SetState(s => s with
                {
                    Stage = UpdateStage.ReadyToInstall,
                    ProgressPercent = 100,
                    Message = I18n.Format("Demo: {0} verified (SHA-256 OK). Real restart skipped in demo mode.", "Демо: обновление {0} проверено (SHA-256 OK). В демо-режиме перезапуск не выполняется.", rel.Version),
                });
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
        _gate.Dispose();
    }
}
