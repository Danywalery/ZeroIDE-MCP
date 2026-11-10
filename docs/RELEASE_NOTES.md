# ZeroIDE MCP 0.3.0

First public release, for Windows 10/11 (x64). All automated suites pass in this build; installation on a clean PC with each AI client is still being qualified.

## Install

1. Download **zeroide-mcp.zip** from this release, not the "Source code" archives. You can check it against **zeroide-mcp.zip.sha256**: `Get-FileHash .\zeroide-mcp.zip -Algorithm SHA256`.
2. Extract it and give **AGENT_INSTALL.md** to the AI agent that will install it, or run `powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\install.ps1` from the extracted folder.
3. Start **ZeroIDE MCP** and switch MCP on when you want access to be available.

The installer needs no administrator rights, installs to `%LOCALAPPDATA%\ZeroIDE-MCP`, downloads portable Node.js and checks its pinned SHA-256, refuses to overwrite an existing installation, never prints tokens and does not switch MCP on by itself. The tray and the server dependencies come prebuilt; nothing is compiled on your PC.

Success means a real connection from your AI client, not only a passing local check. Never paste keys or tokens into a chat or a log.

## Changes since the earlier test builds

**Name**

- One product name, ZeroIDE MCP: the app is `ZeroIDE-MCP.exe` and installs to `%LOCALAPPDATA%\ZeroIDE-MCP`. If a build from before the rename is installed in `%LOCALAPPDATA%\AgentPower`, the installer stops and leaves it untouched; you decide whether to keep, migrate or delete it.

**Security**

- The server fails closed: without configured credentials every request gets 401, and an unreadable `tokens.json` no longer turns authentication off.
- Tokens in the URL (`?token=`) are refused by default. **Migration:** send `Authorization: Bearer <token>`. For an old client you can set `MCP_ALLOW_QUERY_TOKEN=1` in `config.env` for a while; each use is logged without the token.
- Limits for failed authentication, OAuth and discovery routes, client registration, authorization and waiting consent requests, plus request body size limits.
- Errors are returned as JSON without stack traces, and tool annotations match what the tools do.
- The installer checks the SHA-256 of the downloaded Node.js; portable Node.js is now 24.22.2 LTS.
- Release builds of the tray no longer contain build-machine paths.

**Other**

- Sample commands and paths in the tray use a neutral demo folder; the Online status shows the public host of the selected connection.
- `search_files` accepts a single file, reads UTF-16, BOM and CRLF files and reports a missing path as an error.
- The installer no longer fails on a second run when `%TEMP%` is an 8.3 short path.
- The package now includes `LICENSE` (MIT-0) and `THIRD_PARTY_NOTICES.md`.

The full list is in `CHANGELOG.md` in the repository.

## Tests

The release build reruns every automated suite: connection registry, CLI, installer and package contract, tray connection runtime, onboarding helpers, isolated server checks, security checks and the older v0.4 suites (folder confinement, token scopes and limits, rotation and revocation, client registration policy, OAuth flow).

## Known limits

- Not code-signed yet, so Windows SmartScreen may warn.
- The tray targets .NET 8 (Microsoft support ends on 10 November 2026).
- Not qualified yet: clean-PC onboarding with each AI client, provider-specific handover, recovery after a reboot or crash, Cloudflare and Tailscale reachability, OAuth issuer migration.
- It is not a sandbox: allowed commands run with your Windows user's permissions.
