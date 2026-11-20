# Security

ZeroIDE MCP lets an AI agent run PowerShell commands, read and write files and search on a Windows PC. Treat every connection to it as remote code execution on that PC.

## Reporting a vulnerability

Do not open a public issue. Report it privately through the Security tab of this repository (Report a vulnerability) and include the version, steps to reproduce and the impact. Never attach real tokens, keys, `config.env`, `tokens.json`, `oauth.json` or log files.

## Security model

- **Off by default.** The server listens only on `127.0.0.1` and runs only while it is switched on in the tray. Remote access goes through the connection you choose (ngrok, Cloudflare Tunnel or your own SSH server).
- **Authentication is required and fails closed.** Every MCP request needs `Authorization: Bearer <token>`: a token from the registry (`tokens.json`, stored as SHA-256 hashes), an OAuth access token, or the legacy `MCP_TOKEN` from `config.env`. Revoked and expired registry tokens are refused. If no credentials are configured at all, the server answers 401 to everything instead of running open. An unreadable `tokens.json` does not switch authentication off.
- **No tokens in URLs.** `?token=` is not accepted by default, because URLs end up in proxy logs, tunnel dashboards and browser history. For an old client you can turn it back on temporarily with `MCP_ALLOW_QUERY_TOKEN=1` in `config.env`. The server then prints a warning at startup and writes each use to the audit log (at most once a minute, never the token itself).
- **Limits.** All requests: 1200 per minute. Failed authentication: 60 per 5 minutes. OAuth and discovery routes: 120 per minute. Client registration: 20 per 15 minutes. Authorization requests: 30 per 10 minutes, with at most 5 consent requests waiting in the tray. Request bodies: 64 KB on small routes, 50 MB on `/mcp`.
- **Approvals.** With the default `permissions.json`, reading tools, `system_info` and the journal run without asking. `write_file`, `edit_file`, `file_ops` and `run_command` wait for your approval in the tray (60 seconds, then refused), and so does `read_image`, because the picture leaves the PC.
- **Risk assessment.** Every command and file change is rated low, medium, high or critical by transparent rules (wiping disks or restore points, weakening Defender or the firewall, extracting credentials, keyloggers, download-and-run, granting administrator rights, recursive or forced deletion, destructive Git commands, touching ZeroIDE itself). The approval card shows the rating, the reasons and, for file changes, the diff. An `allow` rule does not pass a high-risk call silently, and a temporary grant for a whole tool never covers a critical call: for a critical call the card defaults to **Deny** and offers only **Allow this once**. The rules are a reading aid, not a guarantee: a command can still do harm the rules do not recognize.
- **Self-protection.** File tools never read ZeroIDE's credentials (`server/config.env`, `tokens.json`, `oauth.json`, `tray-config.json`, tunnel keys and other `*.token`, `*.key`, `*.pem` files in the installation folder) and never write its `permissions.json`, logs, journals, change history or program folder; such calls return `DENIED [PROTECTED_PATH]`. Commands that mention these files or the tray control channel are rated critical. `run_command` itself runs with your Windows rights, so this is a guard rail, not a sandbox. `MCP_ALLOW_SELF_ACCESS=1` in `config.env` turns the rule off.
- **Approval channel.** Only the owner presses approval buttons. The tray's `/approval/simulate-click` test endpoint is refused unless the tray was started with `ZEROIDE_TEST_SIMULATE_CLICK=1` for a test run.
- **Change history.** Before `write_file`, `edit_file` or `file_ops` overwrites, edits, moves or deletes a file, the previous version is kept in `%LOCALAPPDATA%\ZeroIDE-MCP\history` for 30 days (`MCP_HISTORY_DIR`, `MCP_HISTORY_DAYS`; files over 25 MB are recorded without a copy). The owner undoes changes in **Settings → Changes**. `MCP_BACKUP_MODE=sidecar` or `both` also keeps `.bak` copies next to the files. History holds old file contents, so treat it like the files themselves.
- **Honest metadata and errors.** Tools that change the PC are not marked read-only. Unexpected errors return JSON without stack traces.
- **Audit.** Every tool call goes to `logs/audit.jsonl` with secrets masked. `/local/status` (used by the tray) shows which authentication methods are active.

## Known trade-offs

- Limits are counted per client address as the server sees it. Behind a tunnel, all remote traffic comes from the local tunnel process, so someone sending many wrong tokens can also block you for up to 5 minutes.
- The legacy `MCP_TOKEN` is a long-lived secret stored in plain text. Prefer registry tokens or OAuth, and rotate it if it was ever pasted somewhere.
- The installer downloads Node.js from nodejs.org and checks a pinned SHA-256 before unpacking it. The release package and the tray app are not code-signed yet, so Windows SmartScreen may warn.

## Keeping secrets safe on your PC

These files grant access to the PC or to your server: `%LOCALAPPDATA%\ZeroIDE-MCP\server\config.env`, `tokens.json`, `oauth.json`, `tunnel_key` (SSH key for the tunnel). `tray-config.json` holds the connection settings. Backups and copies of an installation contain the same files. Do not keep copies inside project folders, archives, chats or cloud-synced folders, and keep them out of git (`.gitignore` excludes these names).

## Security tests

`node tests/security-v04.mjs` starts isolated servers on free ports with test credentials and checks the rules above (68 checks). It never touches the installed copy. `node tests/isolated-v04.mjs` adds 61 checks for folder confinement, token scopes, limits and expiry, token rotation and revocation, the client registration policy and the OAuth flow. `node tests/guardian-v05.mjs` adds 72 checks for self-protection, risk ratings and approvals, change history and undo, and the new file tools. CI and the release workflow run all three on every build.
