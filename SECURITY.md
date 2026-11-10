# Security

ZeroIDE MCP lets an AI agent run PowerShell commands, read and write files and search on a Windows PC. Treat every connection to it as remote code execution on that PC.

## Reporting a vulnerability

Do not open a public issue. Report it privately through the Security tab of this repository (Report a vulnerability) and include the version, steps to reproduce and the impact. Never attach real tokens, keys, `config.env`, `tokens.json`, `oauth.json` or log files.

## Security model

- **Off by default.** The server listens only on `127.0.0.1` and runs only while it is switched on in the tray. Remote access goes through the connection you choose (ngrok, Cloudflare Tunnel or your own SSH server).
- **Authentication is required and fails closed.** Every MCP request needs `Authorization: Bearer <token>`: a token from the registry (`tokens.json`, stored as SHA-256 hashes), an OAuth access token, or the legacy `MCP_TOKEN` from `config.env`. Revoked and expired registry tokens are refused. If no credentials are configured at all, the server answers 401 to everything instead of running open. An unreadable `tokens.json` does not switch authentication off.
- **No tokens in URLs.** `?token=` is not accepted by default, because URLs end up in proxy logs, tunnel dashboards and browser history. For an old client you can turn it back on temporarily with `MCP_ALLOW_QUERY_TOKEN=1` in `config.env`. The server then prints a warning at startup and writes each use to the audit log (at most once a minute, never the token itself).
- **Limits.** All requests: 1200 per minute. Failed authentication: 60 per 5 minutes. OAuth and discovery routes: 120 per minute. Client registration: 20 per 15 minutes. Authorization requests: 30 per 10 minutes, with at most 5 consent requests waiting in the tray. Request bodies: 64 KB on small routes, 50 MB on `/mcp`.
- **Approvals.** With the default `permissions.json`, reading tools and the journal run without asking. `write_file`, `edit_file` and `run_command` wait for your approval in the tray (60 seconds, then refused).
- **Honest metadata and errors.** Tools that change the PC are not marked read-only. Unexpected errors return JSON without stack traces.
- **Audit.** Every tool call goes to `logs/audit.jsonl` with secrets masked. `/local/status` (used by the tray) shows which authentication methods are active.

## Known trade-offs

- Limits are counted per client address as the server sees it. Behind a tunnel, all remote traffic comes from the local tunnel process, so someone sending many wrong tokens can also block you for up to 5 minutes.
- The legacy `MCP_TOKEN` is a long-lived secret stored in plain text. Prefer registry tokens or OAuth, and rotate it if it was ever pasted somewhere.
- The installer downloads Node.js from nodejs.org and checks a pinned SHA-256 before unpacking it. The release package and the tray app are not code-signed yet, so Windows SmartScreen may warn.

## Keeping secrets safe on your PC

These files grant access to the PC or to your server: `%LOCALAPPDATA%\ZeroIDE-MCP\server\config.env`, `tokens.json`, `oauth.json`, `tunnel_key` (SSH key for the tunnel). `tray-config.json` holds the connection settings. Backups and copies of an installation contain the same files. Do not keep copies inside project folders, archives, chats or cloud-synced folders, and keep them out of git (`.gitignore` excludes these names).

## Security tests

`node tests/security-v04.mjs` starts isolated servers on free ports with test credentials and checks the rules above (68 checks). It never touches the installed copy. `node tests/isolated-v04.mjs` adds 61 checks for folder confinement, token scopes, limits and expiry, token rotation and revocation, the client registration policy and the OAuth flow. CI and the release workflow run both on every build.
