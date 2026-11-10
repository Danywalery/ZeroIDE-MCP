# ZeroIDE MCP

**Let an AI assistant work on your Windows PC, but only while you allow it.**

ZeroIDE MCP is a local [MCP](https://modelcontextprotocol.io) server with a Windows tray app. It gives an AI client (Claude, ChatGPT, Cursor, Codex, Notion or another MCP client) tools to run PowerShell commands and work with files on your computer. The server is off by default: you switch it on in the tray for as long as you need it, approve sensitive actions and see every call in the audit log.

Русская версия: [README.ru.md](README.ru.md)

> **Status: 0.3.0, the first public release.** The automated suites pass in CI, but installation on a clean PC with every AI client is not fully qualified yet. See [Known limits](#known-limits).

## Features

- **14 MCP tools:** PowerShell commands (including background jobs and their output), files (read, write, edit, list, search), task status, messages to the owner, screenshots, the audit log and an agent journal.
- **Off by default.** The server listens on 127.0.0.1 only and runs only while it is switched on in the tray. Switching it off or exiting the tray closes access through the app.
- **Approvals.** By default reading is allowed, while writing or editing files and running commands wait for your confirmation in the tray.
- **Access control.** OAuth sign-in that you approve in the tray, or named tokens with a scope (read, read-write or full), an optional folder they cannot leave, an expiry date and a request limit. Tokens can be rotated and revoked.
- **Remote access of your choice.** Local only, ngrok, Cloudflare Tunnel with your own domain, or your own SSH server. Accounts and domains are yours; the project runs no gateway of its own. See [CONNECTIONS.md](CONNECTIONS.md).
- **Audit.** Every tool call is logged, with secrets masked.

It is **not a sandbox**: allowed commands run with your Windows user's permissions. Treat every connection as remote code execution on the PC and read [SECURITY.md](SECURITY.md).

## Requirements

- Windows 10 or 11, x64, PowerShell 5.1 or later. No administrator rights needed.
- Internet during installation: portable Node.js is downloaded and checked against a pinned SHA-256.
- An AI client with MCP support. Cloud and web clients need a reachable HTTPS address, for example through ngrok.

## Install

1. Open the **Releases** page of this repository and download `zeroide-mcp.zip` (not the "Source code" archives). You can compare its hash with `zeroide-mcp.zip.sha256`:
   ```powershell
   Get-FileHash .\zeroide-mcp.zip -Algorithm SHA256
   ```
2. Extract the archive.
3. The easiest way is to give `AGENT_INSTALL.md` from the archive to your AI agent and let it guide you. Or install it yourself from the extracted folder:
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\install.ps1
   ```
4. Start **ZeroIDE MCP** from the Start menu and switch MCP on when the AI needs access. Set up the connection for your AI client in the tray; [docs/AGENT_CONNECTIONS.md](docs/AGENT_CONNECTIONS.md) has the details for agents.

The installer puts everything in `%LOCALAPPDATA%\ZeroIDE-MCP`, refuses to overwrite an existing installation, never prints tokens and does not switch MCP on by itself. It uses the local ports 39011 and 39012.

## Repository layout

| Path | Contents |
|---|---|
| `server/` | MCP server for Node.js (`server.mjs`) and the token CLI (`tokens-cli.mjs`) |
| `tray/` | Tray app (C#, .NET 8, Windows Forms): starts and stops the server and the connection, approvals, access, journal |
| `installer/` | `install.ps1` for the release package |
| `tools/` | Connection and onboarding helpers used by the installer and by agents |
| `tests/` | Automated tests; they run isolated and never touch an installed copy |
| `docs/` | Agent connection guide, building from source, release notes |
| `AGENT_INSTALL.md`, `agent-manifest.json` | Entry point for an AI agent that installs from the release package |

## Building from source

The repository holds source only; releases are built by GitHub Actions. To build and test locally, see [docs/BUILDING.md](docs/BUILDING.md).

## Known limits

- The release package and the tray app are not code-signed yet, so Windows SmartScreen may warn.
- The tray targets .NET 8, whose Microsoft support ends on 10 November 2026. A move to a newer .NET is planned.
- Not qualified yet: clean-PC onboarding with each AI client, recovery after a reboot or crash, external reachability through Cloudflare or Tailscale.
- Request limits count per client address as the server sees it. Behind a tunnel all remote traffic shares one address.

## License

[MIT No Attribution (MIT-0)](LICENSE). Third-party components keep their own licenses, see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
