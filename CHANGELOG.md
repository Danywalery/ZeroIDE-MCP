# Changelog

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
