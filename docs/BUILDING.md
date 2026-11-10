# Building and testing from source

End users do not need this page: they install the release package (see the README). It is for developers.

## Requirements

- Windows 10/11 x64, PowerShell 5.1 or later, Git
- .NET 8 SDK (tray app and C# test projects)
- Node.js 24 (the server itself also runs on Node.js 20)

## Build

```powershell
# Tray app: self-contained single-file exe -> tray\build\ZeroIDE-MCP.exe
.\tray\build-tray.ps1

# Server dependencies
cd server
npm ci --omit=dev
cd ..
```

`server/server.mjs` is the server. The tray starts `<ServerDir>\server.mjs` with Node.js; settings come from `config.env` next to it (see `server/config.env.example`) and from environment variables that the tray sets.

## Tests

The Node.js suites copy the server to a temporary folder and use free ports, a fake tray and a clean environment, so they never reach an installed copy, its tokens or its logs.

| Command | Checks | Covers |
|---|---|---|
| `node tests/connections-runtime/server-proof.mjs` | 26 | server side of connection checks: minimal health, verifier versus real client |
| `node tests/security-v04.mjs` | 68 | authentication fails closed, no tokens in URLs, rate and size limits, error format |
| `node tests/isolated-v04.mjs` | 61 | folder confinement, token scopes, limits and expiry, rotation and revocation, client registration policy, OAuth flow |
| `dotnet run --project tests/connections-v2/Tests.csproj -c Release` | 45 | connection registry |
| `.\tests\connections-v2\cli-smoke.ps1` | 8 | connection CLI; build it first with `dotnet build tools/ConnectionCtl/ConnectionCtl.csproj -c Release` |
| `.\tests\installer-smoke.ps1` | 18 | installer and package contract |
| `.\tests\onboarding-helpers.ps1` | 57 | onboarding helper scripts against a temporary MCP server |
| `dotnet run --project tests/connections-runtime/RuntimeTests.csproj -c Release -- output/renders` | 80 | tray connection runtime; renders UI states to `output/renders` |

CI (`.github/workflows/ci.yml`) runs all of them on every push and pull request.

If you run tests from a shell started by the tray (for example from an AI session that uses ZeroIDE MCP itself), clear `MCP_*`, `TRAY_*`, `PORT` and `PROJECT_DIR` first. The Node.js suites already build a clean environment for the servers they start.

## Releases

Pushing a tag that starts with `v` runs `.github/workflows/release.yml`. It builds the tray, installs the server dependencies, reruns the tests, stages the package and publishes `zeroide-mcp.zip` with its `.sha256` as a GitHub release. Tags with a hyphen, such as `v0.3.0-rc.1`, become pre-releases. Running the workflow manually creates a `dev-<run number>` release. The release text comes from `docs/RELEASE_NOTES.md`; update it and `CHANGELOG.md` before tagging.

The package contains `AGENT_INSTALL.md`, `agent-manifest.json`, `LICENSE`, `THIRD_PARTY_NOTICES.md`, `installer/`, `tools/`, `docs/AGENT_CONNECTIONS.md`, `docs/RELEASE_NOTES.md`, `server/` (server, token CLI and dependencies) and `tray/build/ZeroIDE-MCP.exe`. Machine-local state (`config.env`, `tokens.json`, `oauth.json`, tunnel keys) is never packaged; the workflow checks this.
