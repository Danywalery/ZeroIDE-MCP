# Changelog

## 0.5.0 — 2026-11-20

### Added: safer agent work & undo

- **Self-protection.** File tools never read ZeroIDE MCP's own credentials (`server/config.env`, `tokens.json`, `oauth.json`, `telegram.json`, `tray-config.json`, tunnel keys) and never change its settings, journals, change history or program folder; such calls get `DENIED [PROTECTED_PATH]`. Commands that touch these files or the tray's approval channel are rated critical. `MCP_ALLOW_SELF_ACCESS=1` in `config.env` lifts the rule for owners who want that.
- **Risk assessment & autonomy controls.** Every command and file change is rated low, medium, high or critical by transparent rules: wiping a disk or restore points, weakening Defender or the firewall, extracting saved credentials, keyloggers, downloading and running code, granting administrator rights, recursive or forced deletion, destructive Git commands and more. By default an `allow` rule no longer lets a high-risk call through silently, and a temporary grant for a whole tool never covers a critical call. In **Settings → Tools (Execution)**, separate toggles control **High-risk & Red-Flag protection** and **Critical action protection (Critical)** (with confirmation) — disabling both gives trusted setups full unattended autonomy while `system_info` reports the active mode.
- **Approval cards with risk and a diff.** The card says in plain words what the call can do and why it was rated so; file writes and edits show the change itself (added and removed lines). For a critical call the default button is **Deny**, and the only way to allow it is **Allow this once**.
- **Change history, undo & built-in File Compare window.** Before `write_file`, `edit_file` or `file_ops` overwrites, edits, moves or deletes a file, the previous version goes to ZeroIDE history (`%LOCALAPPDATA%\ZeroIDE-MCP\history`, kept 30 days) instead of a `.bak` next to the file. Results report a `change_id`. Settings: `MCP_HISTORY_DIR`, `MCP_HISTORY_DAYS`, `MCP_BACKUP_MODE=history|sidecar|both` (`sidecar` brings back the `.bak` copies). The new **Settings → Changes** section (and **Agent changes (undo)…** in the tray menu) lets you filter recent changes (**All changes**, **Today**, **Deletions**), revert any edit/delete/move/creation with **Undo** (reversible), reveal files in Explorer via **Show in folder**, and inspect modifications in the new built-in **File Compare (diff)** window with Before/After line numbers, `+N / −M` header chips, **Changes only** / **Full file** toggle, **Wrap lines** toggle, clickable skipped-line dividers, **Copy diff** (`Ctrl+C`), **Show in folder**, and **Undo change**.

### Added: live tray collaboration, Telegram alerts & built-in updates

- **Multi-agent collaboration & two-way tray chat.** Tools support an optional `agent` identifier (`QA Demo Agent`, `Claude · Landing`, etc.). The tray popup **panel** (`TrayMenuMode: "panel"`, now the default) includes a redesigned **Task Card** with progress chips, elapsed time and structured `Now` / `Done` / `Next` sections, a collapsible **Write to agent** dialogue thread, and click-to-expand support (`Show full ▾` / `Collapse ▴`) for long model responses in both the top banner and chat bubbles.
- **One-way Telegram alerts (Settings → Notifications).** Receive notifications in Telegram when a task finishes, an agent sends a message or an approval card waits for your decision. Configured through a matching ZeroIDE-styled dialog with `/start` chat auto-detection and test delivery; credentials live in self-protected `telegram.json`, with automatic secret redaction and HTML escaping.
- **Built-in updates (Danywalery/ZeroIDE-MCP).** Automatically checks GitHub Releases on startup and every 12 hours (`CheckUpdatesEnabled = true` by default). Displays a dismissible update banner across Settings tabs, a sidebar dot indicator on **About**, an in-app **What's new** changelog dialog (`UpdateNotesDialog`), and an **Updates** card in **About** with manual **Check now** / **Install** controls. Auto-installation (`AutoUpdateEnabled = false` by default) verifies `SHA-256` against `zeroide-mcp.zip.sha256`, rejects Zip Slip / machine-local state files, and waits until the MCP server goes offline before applying via `installer/install.ps1 -Update`.

### Added: MCP tools (14 → 19 in total)

- `file_ops`: copy, move or rename, delete (reversibly via ZeroIDE history trash) and create folders without a shell.
- `system_info`: Windows version, CPU, memory, disks, displays and developer tools, plus how ZeroIDE is configured for the calling agent (project folder, confinement, scope, tool permissions, active risk gate mode, active grants, undo history location).
- `read_image`: inspect local image files (PNG, JPEG, GIF, BMP, ICO, WebP); large images are downscaled on the PC; defaults to `ask` in Settings → Tools.
- `agent_message`: explicit agent-to-owner/peer communication for multi-agent runs.
- `await_owner`: optional 5-minute standby window after finishing a task (`deny` by default in Settings → Tools; preserved across permission presets; `"5 min"` pill hidden when disabled). Holds the MCP connection live while you test the result on your PC so you can send quick follow-up tweaks directly from the tray's **Write to agent** chat in the exact same context — especially useful with web chats where closing the turn otherwise requires switching back to the browser and prompting the agent from scratch.
- Enhanced `edit_file` (atomic multi-replacement `edits[]` and `replaceAll`) and `user_message` (optional `open` link, file or folder — the banner displays an **Open** button; nothing opens automatically, and programs/scripts are forced to reveal-only in Explorer; stronger server nudges to keep `task_status` and `user_message` updated).

### Changed & Fixed

- **New ZeroIDE brand identity:** vector & icon logo across `ZeroIDE-MCP.exe`, taskbar, 32×32 tray status icons, setup wizard, Settings and dialogs.
- **Modern default tray & clearer controls:** `TrayMenuMode` defaults to `"panel"`; all 19 server tools appear in Settings → Tools; clicking the local status banner button opens the setup wizard directly on the internet/ngrok step.
- **Reliable desktop launching (`DesktopLauncher`):** opening provider key pages, **Show in folder** (including spaces, Cyrillic paths and deleted-file parent folder fallback), agent message **Open** links, screenshot folder and dashboard links now explicitly target the interactive desktop (`winsta0\default`); added a working **Open key page** button directly inside the TopMost secret entry dialog.
- **Installer & testability:** `installer/install.ps1` adds `-InstallDir` support and migrates `telegram.json`; `ZEROIDE_INSTANCE_ID` supports isolated side-by-side tray instances; `/approval/simulate-click` works only when `ZEROIDE_TEST_SIMULATE_CLICK=1`.
- **One version & expanded CI coverage:** aligned to `0.5.0` everywhere; automated test suite expanded to 10 suites / 562 checks (`guardian-v05.mjs` 79 checks, `TrayUiTests` 118 checks, `RuntimeTests` 80 checks, plus `server-proof`, `security-v04`, `isolated-v04`, `connections-v2`, `cli-smoke`, `installer-smoke`, and `onboarding-helpers`).

## 0.4.0 — 2026-11-15

### Added: setup wizard for people, not only agents

- `ZeroIDE-MCP.exe` started from the unpacked release package opens a setup wizard instead of creating a stray portable copy inside the zip folder. It explains what the app does, offers **Manually, with guidance** or **With an AI agent on this PC** (copies a task that points at `AGENT_INSTALL.md`), runs a preflight (package files, ports 39011/39012, existing or legacy installation) and then runs the same `installer/install.ps1` the agent path uses, with progress and a log.
- After installation the app opens the connection part of the wizard once: which AI (an agent on this PC, ChatGPT, Claude, Notion, another computer), then local access or internet access. For a web chat it recommends ngrok and drives the shipped `tools/Setup-Ngrok.ps1` helper, so the manual path and the agent path share one implementation: protected key entry, launch consent, address check and activation all happen in the app. Cloudflare Tunnel, Tailscale and existing addresses go through "Enter the address" or a task for an agent. The last screen hands off to the AI client with the URL, generic sign-in steps and a live "client connected" check.
- **Setup wizard** button in Connection reopens the wizard. `tray-config.json` gains `SetupCompleted`.
- `installer/install.ps1 -Update` replaces the app, server and helpers of an existing installation and keeps `config.env`, `tray-config.json`, tokens, connections and logs; the app must be closed. The fresh-install path is unchanged.
- The release zip ships `ZeroIDE-MCP.exe` at the package root (double-click = wizard); the installer also accepts `tray/build/ZeroIDE-MCP.exe` from a source build.
- An interrupted installation (folder without `tray-config.json`) is completed on the next run instead of being refused; the generated MCP token is kept.

### Added: Tailscale and Cloudflare Tunnel helpers

- `tools/Setup-Tailscale.ps1`: private access from the owner's other computers. Installs signed official Tailscale when missing (visible installer, administrator prompt), waits for the owner's sign-in (opens the auth page), requires HTTPS certificates in the tailnet (`waiting_for_owner` / `ENABLE_HTTPS` with the admin URL otherwise), picks a free Serve port (443, 8443, 10000; existing Serve/Funnel configs are never touched) and runs a foreground `tailscale serve` through the coordinator, so the app starts and stops it with MCP. Scope `Private`.
- `tools/Setup-Cloudflare.ps1 -Hostname <public hostname>`: the owner creates the tunnel and its public hostname in Cloudflare Zero Trust; the helper installs signed official cloudflared when missing, asks for the tunnel token in the protected PC window and runs `cloudflared tunnel run --token-file` through the coordinator.
- The wizard's provider screen drives all three helpers; Start switches MCP on, clears a failed attempt's progress and draft, and passes the installation folder explicitly. Owner prompts (key entry, launch consent) are now shown on top of other windows.

### Added: Uninstall

- Settings gains an **Uninstall** section (between Screenshots and About). It lists what is removed and what stays, asks for confirmation, stops the server and managed connections, removes the autostart entry and the Start Menu shortcut, exits, and a detached PowerShell deletes the installation folder with all local data. Accounts and tunnels at ngrok, Cloudflare and Tailscale, and the Tailscale app itself, stay.

## 0.3.0 — 2026-11-11

First public release: 0.3.0-rc.1 plus the changes below.

### Renamed: one product name, ZeroIDE MCP

- The Windows app is now `ZeroIDE-MCP.exe` (was `AgentPowerTray.exe`), the install folder is `%LOCALAPPDATA%\ZeroIDE-MCP` (was `%LOCALAPPDATA%\AgentPower`) and the Start menu shortcut is **ZeroIDE MCP**. The release package is `zeroide-mcp.zip`; the npm package and the MCP server name are `zeroide-mcp`.
- Internal identifiers follow the same name: .NET namespace `ZeroIDE.Mcp`, single-instance mutex `ZeroIDE-MCP.SingleInstance`, autostart value `ZeroIDE-MCP`, tray control header `X-ZeroIDE-Control`. The tray and the server ship together, so nothing has to be changed by hand.
- **Migration:** an existing `%LOCALAPPDATA%\AgentPower` installation is not moved or modified. The installer stops when it finds one, so two copies never compete for the same ports; the owner decides whether to keep, migrate or delete it.
- When no project folder is configured, the tray now falls back to the user profile instead of a developer folder on the Desktop.

### Changed

- One version everywhere: `agent-manifest.json`, `server/package.json` and the tray are 0.3.0, and the server reports 0.3.0 in MCP `serverInfo`, `/health` and `/local/status` instead of the old internal numbers 2.0.0 and 2.0.0-win.5.

## 0.3.0-rc.1 — 2026-11-10

Release candidate. The repository is prepared for publication; the server, tray and installer changes below were prepared on the same day.

### Repository

- Internal design specs, agent briefs, handoff notes, scratch scripts, old test fixtures and machine-specific deploy checks are no longer in the repository.
- `server/server-v04.mjs` is now `server/server.mjs`; the stale pre-i18n `server/server.mjs` was removed. Nothing changes for users: the tray still starts `server\server.mjs` from the installation.
- New README in English and Russian (`README.ru.md`), `docs/BUILDING.md` and `THIRD_PARTY_NOTICES.md`. Release notes moved to `docs/RELEASE_NOTES.md`. The release package now includes `LICENSE` and `THIRD_PARTY_NOTICES.md`.
- License: MIT No Attribution (MIT-0).
- Versions aligned: `agent-manifest.json` and `server/package.json` 0.3.0-rc.1, tray 0.3.0.

### Security

- The server fails closed: when no credentials are configured, every request gets 401 instead of open access. An unreadable `tokens.json` no longer leaves the server without authentication.
- `?token=` in the URL is no longer accepted by default. **Migration:** clients must send `Authorization: Bearer <token>`. To keep an old client working for a while, set `MCP_ALLOW_QUERY_TOKEN=1` in `config.env`. Each use is logged (without the token) and the server warns at startup.
- New limits for failed authentication, OAuth and discovery routes, client registration, authorization requests and waiting consent requests. Request body size limits per route (64 KB on small routes, 50 MB on `/mcp`).
- Errors are returned as JSON without stack traces. Tool annotations match what the tools do, and tool descriptions no longer mention a VPS.
- Release builds of the tray app no longer contain the build machine's folder paths (`ContinuousIntegrationBuild` for Release in `tray/ZeroIDE-MCP.csproj`).
- The installer checks the SHA-256 of the downloaded Node.js archive against a pinned value before unpacking it.
- `.gitignore` also excludes `oauth.json`, `audit.jsonl` and `journal.jsonl`.

### Changed

- Portable Node.js in the installer: 20.18.1 (end of life since 2026-04-30) → 24.22.2 LTS. The release workflow uses Node 24.
- Tray: sample commands and paths in the interface use a neutral demo folder instead of the developer's own. Chart previews go to `--preview-out=<folder>`, `ZEROIDE_PREVIEW_DIR`, the checkout's `output\charts` or `%TEMP%\zeroide-previews`. The "Online" status shows the public host of the selected connection.
- `search_files` accepts a single file, reads UTF-16, BOM and CRLF files, and reports a missing path as an error.

### Fixed

- The installer no longer fails on a second run when `%TEMP%` is an 8.3 short path (seen on an account with a non-ASCII name). Temporary Node.js files are removed after unpacking.

### Tests

- New `tests/security-v04.mjs`: 68 checks on isolated servers, added to the release workflow.
- New `tests/isolated-v04.mjs` runs the older v0.4 suites (`smoke-v04`, `phase3-v04`, `dcr-policy-test`, `oauth-test`; 61 checks) on isolated servers with free ports instead of a prepared folder and the fixed port 39021. Added to CI and the release workflow.
- `oauth-test`: the registration check follows the current policy (unknown HTTPS sites may register and the owner decides; plain-HTTP remote redirects are refused).
