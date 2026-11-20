# ZeroIDE MCP 0.5.0

Safer, clearer AI work on Windows 10/11 (x64): risk assessment before every dangerous action, one-click undo with a dark diff viewer, live tray chat, one-way Telegram alerts, and built-in GitHub Releases updates.

## What's new since v0.4.0

- **Risk cards, diff previews & autonomy controls.** Every command and file change is rated low, medium, high or critical before execution. File writes/edits show the exact `+`/`−` diff on the card; by default high-risk calls bypass silent `allow`, and critical calls default to **Deny** with **Allow this once** only. In **Settings → Tools (Execution)**, two separate toggles control **High-risk & Red-Flag protection** and **Critical action protection (Critical)** so trusted setups can opt into full unattended autonomy.
- **Undo of agent changes & built-in File Compare window.** Previous file versions are kept in ZeroIDE history (`%LOCALAPPDATA%\ZeroIDE-MCP\history`, 30 days) instead of `.bak` sidecars. New in v0.5.0: **Settings → Changes** offers one-click reversible **Undo**, working **Show in folder**, and a new dark **File Compare (Diff)** window (line numbers, `+N / −M` chips, Full file / Wrap lines toggles, Copy diff).
- **Self-protection.** MCP agents cannot read ZeroIDE tokens/configs (`config.env`, `tokens.json`, `oauth.json`, `telegram.json`, `tray-config.json`, tunnel keys) or edit permissions, logs, history or the app folder (`PROTECTED_PATH`).
- **Tray panel upgrade, live chat & task card.** Panel mode is now the default: structured **Task Card** with progress chips and `Now` / `Done` / `Next` steps, collapsible two-way **Write to agent** chat, click-to-expand long model replies, and the new ZeroIDE logo across all windows and tray icons.
- **19 MCP tools + optional 5-min standby (`await_owner`).** Added `file_ops`, `system_info`, `read_image` (asks first), `agent_message`, and optional `await_owner` (off by default — holds the session open for 5 minutes after finishing so you can test results on your PC and send quick follow-up tweaks straight from the tray chat in the same context without returning to a web chat); enhanced `edit_file` (`edits[]`, `replaceAll`) and `user_message` (safe **Open** button for URLs, files and folders).
- **One-way Telegram alerts (Settings → Notifications).** Optional Telegram bot notifications for task completion, agent messages and approval cards, configured in a ZeroIDE-styled dialog with `/start` chat auto-detection and secret redaction.
- **Built-in update checker & auto-updater.** Checks `Danywalery/ZeroIDE-MCP` GitHub Releases on startup and every 12 hours, shows an in-app update banner and **What's new** dialog, verifies `SHA-256` against `zeroide-mcp.zip.sha256`, and applies opt-in auto-updates only when the MCP server is offline.
- **Setup & desktop fixes.** Working **Open key page** button inside the TopMost ngrok/Cloudflare token prompt, reliable desktop opening via `DesktopLauncher` (`winsta0\default`), `-InstallDir` support in `install.ps1`, and `ZEROIDE_INSTANCE_ID` for isolated instances.

Full changelog in `CHANGELOG.md`. Questions, ideas or collaboration: [Telegram @danywalery](https://t.me/danywalery).

## Install

1. Download **zeroide-mcp.zip** from this release, not the "Source code" archives. You can check it against **zeroide-mcp.zip.sha256**: `Get-FileHash .\zeroide-mcp.zip -Algorithm SHA256`.
2. Extract it (it unpacks into a single `zeroide-mcp` folder) and start **ZeroIDE-MCP.exe** from that folder: the setup wizard explains the app, installs it and sets up the connection to your AI (manually with guidance, or by giving a task to an AI agent on this PC). The command line still works: `powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\install.ps1` (`-Update` for an existing installation; it keeps your settings, tokens and connections).
3. Start **ZeroIDE MCP** and switch MCP on when you want access to be available.

The installer needs no administrator rights, installs to `%LOCALAPPDATA%\ZeroIDE-MCP`, downloads portable Node.js and checks its pinned SHA-256, refuses to overwrite an existing installation, never prints tokens and does not switch MCP on by itself. The tray and the server dependencies come prebuilt; nothing is compiled on your PC.

Success means a real connection from your AI client, not only a passing local check. Never paste keys or tokens into a chat or a log.

## Changes in 0.4.0

- **Setup wizard.** Unzip and double-click `ZeroIDE-MCP.exe`: the wizard installs the app with one click and walks you through the connection step by step. The agent-led path is still there.
- **Connection options, set up for you:** ngrok for web chats, Tailscale for your other computers, Cloudflare Tunnel on your own domain, with a comparison screen in the wizard.
- **Uninstall** in Settings, and `install.ps1 -Update` for in-place updates.

## Tests

The release build reruns all 10 automated suites (562 checks): connection registry, CLI, installer and package contract, tray connection runtime, onboarding helpers, isolated server checks, security checks, the v0.4 suites (folder confinement, token scopes and limits, rotation and revocation, client registration policy, OAuth flow), the v0.5 guardian suite (self-protection, risk ratings & autonomy toggles, change history and undo, new file tools) and the tray UI suite (tray panel chat/task card, approval cards, risk toggles, Settings → Changes & diff compare dialog, Telegram alerts, GitHub auto-updater).

## Known limits

- Not code-signed yet, so Windows SmartScreen may warn.
- The tray targets .NET 8 (Microsoft support ends on 10 November 2026).
- Risk ratings are rules, not a guarantee: a harmful command they do not recognize is shown as low risk. Commands still wait for your approval by default, so read them before allowing.
- Undo covers changes made through `write_file`, `edit_file` and `file_ops`. Changes made by `run_command` are not recorded; prefer the file tools when you want them reversible.
- Not qualified yet: clean-PC onboarding with each AI client, provider-specific handover, recovery after a reboot or crash, Cloudflare and Tailscale reachability, OAuth issuer migration.
- It is not a sandbox: allowed commands run with your Windows user's permissions.
