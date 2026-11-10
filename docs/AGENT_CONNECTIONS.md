# Agent-guided AI connection

ZeroIDE MCP is a local-first Windows app. The agent does the technical setup; the owner chooses their AI and performs only personal sign-in, key entry and consent. Accounts and domains belong to the owner. Providers are open-ended.

## Start with the explanation, not installation

Follow `AGENT_INSTALL.md` first: explain what ZeroIDE MCP does and ask which AI the owner wants **before installing anything**. Do not assume the owner read GitHub. Explain files/commands, compatible web chats, separate connections to multiple PCs, owner controls, and Windows-user permissions (not a sandbox). Then give one recommendation. The conversation below also applies when an installed app is being connected to a different client.

## Short path for a fresh local-only installation

Use the shipped helpers, not agent-generated code:

```powershell
& "$env:LOCALAPPDATA\ZeroIDE-MCP\tools\Test-LocalMcp.ps1"
# Only after the owner chooses ngrok, with MCP enabled and no existing public connection/draft:
& "$env:LOCALAPPDATA\ZeroIDE-MCP\tools\Setup-Ngrok.ps1" -OwnerChoseNgrok
```

The first performs an authenticated, read-only protocol check and explicitly closes its session. The second installs signed official ngrok if absent, shows progress, opens protected key entry, and drives the existing coordinator through preparation, owner launch consent, address discovery, identity check and activation. Run the second call in a background job so personal sign-in/consent can take time. Do not issue duplicate calls while the original job waits.

`awaiting_client` means **finish in the chosen real AI client**, not done. Prefer OAuth; otherwise create a named least-privilege token with expiration where supported in Access. The legacy permanent `MCP_TOKEN` in `%LOCALAPPDATA%\ZeroIDE-MCP\server\config.env` is for compatibility, not a default owner-facing setup step. Never display it. The local check uses it internally without returning it.

The ngrok shortcut is intentionally first-time only: it refuses existing public links, pending changes, retained old connections, another active guide or an already-running ngrok process. On `needs_attention`, inspect the returned phase and coordinator state; a candidate may remain, or activation may already have happened. Never blindly retry, roll back or overwrite settings. Use the advanced contract below for recovery, another provider, an existing connection, or a domain.

After actual client access is demonstrated, clear only the returned `taskId`. Keep the distinction between local readiness, reachable internet address and actual client access throughout the UI and conversation. No 3–4 minute end-to-end timing is claimed until measured on a fresh installation.

## Conversation and client handoff

Ask one question at a time, using buttons when available. No transport/profile/route jargon in owner-facing text.

1. **Which AI will work with this computer?** Buttons: **Claude / Cursor / Codex on this PC**, **ChatGPT**, **Claude in a browser**, **Notion**, **Other**, **Decide later**. Ask the name, not whether the user needs a tunnel. For Other, clarify where the client runs. Local apps need only local access. Another device may use an existing private network. Cloud AI needs a public HTTPS address.
2. Before internet setup, state plainly: **AI in a browser or on another computer will be able to control this PC and change files through this address. Access is closed without authorization; only an AI with an MCP token or an owner-approved sign-in can enter. Sensitive actions require confirmation, actions are journaled, and exiting the tray disconnects AI from the PC.** This same explanation appears in local consent. Do not promise that an external provider itself is stopped by the tray.
3. Inspect installed software and existing accounts without exposing secrets. Give **one recommendation**, with its reason, limits, what the owner must do and what you will do. Example: stable free ngrok hostname, no own domain needed. **From you: sign in through Google and paste the key into the window on this PC. I handle the rest.** Buttons: **Let's do it**, **I have my own domain**, **Another way**, **Details**. Never turn this into a provider exam or pretend free plans have no limits.
4. Install official software after the owner's choice. For ngrok, guide the owner to their account dashboard, initiate `secret-request` and wait. The owner enters the authtoken in the masked PC window. **This is a provider account key, not an MCP password.** Never accept secrets in chat, a JSON body, command arguments or logs. Do not read the protected file back through a tool. Other provider credentials use the generic protected file request, or a provider's own supported sign-in flow.
5. Report actual progress and owner steps through the API below. Configure the service and submit a candidate without disturbing the working connection. Do not expose `/local/*` or the tray control port. Keep the exact full MCP path, including custom proxy rewrites; do not blindly append `/mcp`.
6. Check the candidate, obtain local consent and activate. **Never restart, stop or replace the active connection without explicit interruption consent.** If a free provider cannot run two endpoints at once, explain this rather than killing the working endpoint.
7. Finish **in the chosen AI client**: give client-specific URL/authentication steps using current verified client instructions. For ngrok's OAuth warning, click **Visit Site**, then continue sign-in; approve the access card on this PC. Success means a real client signs in and sees/uses the MCP tools, not just a local check. Only after that should the owner retire the previous connection. Clear progress, leave a short setup note, and explain tray → Connection / Access and how disabling MCP or exiting the tray disconnects AI.

Cloudflare Quick Tunnel is temporary; a named tunnel normally uses the owner's domain. Tailscale Serve is private, Funnel is public and needs external DNS/HTTPS validation. Any compatible provider is allowed. Cloudflare/Tailscale are not proven working defaults on this development PC.

## Live coordinator

Use `tools/Configure-Connection.ps1`. It reads the owner-control secret locally in memory and never prints it. Do not overwrite the live registry with the offline utility. MCP must be enabled for starting managed candidates and network checks. Read the current revision and operation ID, never guess them.

```powershell
& .\tools\Configure-Connection.ps1 -Action state
& .\tools\Configure-Connection.ps1 -Action prepare -Revision 0 -ProfileFile .\candidate.json
# Read returned revision and pending.OperationId.
& .\tools\Configure-Connection.ps1 -Action start -Revision 1 -OperationId OPERATION_ID
& .\tools\Configure-Connection.ps1 -Action address -Revision 1 -OperationId OPERATION_ID -Endpoint https://assigned.example/Full/Path/
& .\tools\Configure-Connection.ps1 -Action check -Revision 2 -OperationId OPERATION_ID
& .\tools\Configure-Connection.ps1 -Action activate -Revision 2 -OperationId OPERATION_ID
# Failure: reload state, diagnose, retry or cancel; do not destroy the active connection.
& .\tools\Configure-Connection.ps1 -Action cancel -Revision 2 -OperationId OPERATION_ID
```

Start/check do not increment the revision; prepare/address/activate/cancel do. API: authenticated loopback `GET /connections/state`, `POST /connections/{prepare,start,address,check,activate,cancel}`. POST bodies require `revision`, `operationId` except prepare, plus `profile` or `endpoint` where relevant. CAS and operation binding reject stale requests. No agent-supplied verification or consent flag is trusted. After an interrupted call reload state before retrying; this is not an exactly-once queue.

First internet setup: consent to a managed launch also permits activation of the checked candidate from that operation, provided the command/executable/config approval still matches. External first activation asks consent separately. Replacing an existing public address always shows old → new and asks the owner to switch. A declined or failed operation keeps the candidate and the working connection intact.

## Progress, notes and protected key entry

These are authenticated loopback POST requests, without registry revision. Use `-Action progress|note|secret-request -BodyFile .\request.json`. Body files contain **only non-secret instructions**, never the key. Run calls that wait for consent in the background; wait for the owner, do not click approval for them.

Progress example:

```json
{
  "taskId": "setup-unique-id",
  "title": "Internet address through ngrok",
  "steps": [
    {"text": "Install ngrok", "state": "done", "owner": false},
    {"text": "Enter the provider key on this PC", "state": "current", "owner": true},
    {"text": "Check the address and connect AI", "state": "todo", "owner": false}
  ],
  "waitingFor": "Paste the key in the window on your PC"
}
```

Task ID ≤64 chars, title ≤80, waitingFor ≤200; at most 8 steps, text ≤90, state `done|current|todo`. All text is inert and control/bidi characters are removed. Another task cannot overwrite an active task. Clear with `{"taskId":"setup-unique-id","clear":true}`; the owner can also hide progress. After ten minutes without updates the UI warns of stale progress. Hiding/clearing progress does not cancel a pending connection change or stop the agent.

Note: `{"text":"Your short, plain-language setup note"}` (≤600 chars). Stored privately, displayed in **How this works for you**; never executed. Do not include secrets.

ngrok key request: `{"kind":"ngrok-authtoken","reason":"For your internet address","apiPort":4042}`. Generic key: `{"kind":"provider-token","name":"provider-name","reason":"Why the account key is needed"}`; name is `[a-z0-9-]{1,32}`. The app returns only `{stored, kind, configurationFile}` or `{stored:false, kind}` on cancellation. Each saved key gets a new protected local file, never overwriting the active config. Choose an unused ngrok API port before requesting. Returned ngrok config is v2 with authtoken and loopback web_addr. Pass its path in the managed launch plan. Secrets never enter the API request or response.

## Profiles and ownership

External example (exact case-sensitive property names):

```json
{
  "Id": "my-provider",
  "Name": "My connection",
  "Scope": "Public",
  "Ownership": "External",
  "Adapter": "external",
  "Endpoint": "https://my-provider.example/Full/Custom/Path/",
  "AddressKind": "stable"
}
```

`External`: app does not launch, adopt, restart or kill it. `Managed`: absolute `Launch.Executable`, `Arguments` array, optional absolute `WorkingDirectory` and `ConfigurationFile`. Keys stay in protected files, never arguments. Approval is bound to command, executable bytes and declared config bytes. Changes require fresh consent. Managed processes must support supervised lifetime; detached services are External. They start/stop with MCP, not merely opening the tray. A managed candidate may initially have an empty endpoint; set it through `address` before checking.

Existing ngrok/Cloudflare keep their original lifecycle until explicit migration. Do not relabel the active app-managed service as External to evade ownership. Address editing verifies the replacement while the old connection still runs; it cannot create a provider domain.

## Verification, OAuth and recovery

Verification compares a fresh public per-boot challenge with the authenticated local server, then sends the local MCP Bearer for `initialize`; no tools are invoked. No redirects, bounded responses, session explicitly closed. Proof is bound to the exact candidate and expires after five minutes. **This PC is the check's vantage point, not a cloud AI client.**

`remoteClientVerified` becomes true only for server-recorded initialized client evidence matching the current host/path after the latest switch; the built-in verifier is excluded. A missing marker, custom rewritten prefix or older server means **not confirmed**, not failure and never invented success. Actual client testing is still required. Host evidence is an onboarding hint, not an authorization boundary or proof of network geography. A historical sign-in is not a live health check.

A public identity proof does not make a provider trustworthy with authorization headers. The owner must choose a trusted service. When host/path changes, check OAuth issuer, redirect URI and metadata; a reachable Bearer endpoint does not automatically prove OAuth support.

Tray → **Connection** shows type, URL, check, progress and a copied non-secret task for the user's own agent. It does not secretly launch an agent. **Access** owns the client list and revocation. Keep previous managed processes until the new client works. All app-owned staged/current/retained processes stop with MCP or tray exit; external services remain independently managed. History survives restart; retained process references do not.

## Validation and limits

Run registry, CLI smoke, native runtime and isolated server-proof tests. Inspect native RU/EN renders. Generic fixture tests do not prove fresh-machine setup, every provider, reboot/crash recovery or OAuth migration. Do not claim those without end-to-end evidence.
