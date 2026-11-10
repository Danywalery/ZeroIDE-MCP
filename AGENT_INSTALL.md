# ZeroIDE MCP — agent-led first setup

Read this before running commands. This is the **release ZIP** entry point, not a source-build guide. Speak the owner's language. Use the shipped scripts; do not write your own installer, MCP handshake, ngrok downloader or connection coordinator.

## 1. Welcome and one question — before installation

First explain what the owner received, even if they only said “unpack and follow the instructions”. Adapt this short introduction to their language:

> ZeroIDE MCP connects an AI assistant to this Windows computer: it can work with files, run commands and help with development or other tasks. You can use a supported web AI chat, not only a coding agent installed on the PC. Connecting each computer separately also lets a compatible AI service work with more than one of your computers. The Windows app shows connection status and lets you control access, confirmations and the action journal. It is not a sandbox: allowed commands run with your Windows user's permissions.
>
> Which AI would you like to connect to this computer?

Russian example:

> Привет! Это ZeroIDE MCP — приложение, которое даёт твоему AI доступ к файлам и командам на этом Windows-ПК. Можно работать из привычного веб-чата, например Notion, если он поддерживает MCP, а не только из агента, установленного на компьютере. Если подключить несколько ПК по отдельности, совместимый AI-сервис сможет работать с ними из одного места. В приложении ты управляешь доступом, подтверждениями и журналом действий. Это не изолированная песочница: разрешённые команды выполняются с правами твоего пользователя Windows.
>
> Какой AI подключаем?

Offer: **Claude / Cursor / Codex on this PC · ChatGPT · Claude in a browser · Notion · Other · Decide later**. For Other clarify where it runs. Ask one question at a time. Do not begin with a list of tunnel providers or technical terms. The client's actual MCP capabilities and account plan still need checking.

- A local client can use only-local access. No ngrok account is needed.
- A cloud/web client generally needs a reachable HTTPS MCP URL. A different computer may instead use an existing private network.
- Before an internet connection, explain: **AI using this address can control this PC and change files. Authorization is required; only an AI with an MCP token or an owner-approved sign-in can enter. Sensitive actions require confirmation according to the configured policy, and actions are recorded. Disabling MCP or exiting the tray disconnects access through this app.** Do not promise that an external service itself stops, that approvals block every attack, or that a public URL alone is authorization.
- Give **one recommendation**, its reason and limits. For a normal first cloud-client setup recommend ngrok where available: no own domain, account required, free-plan limits apply. “From you: sign in to ngrok and paste the account key in the window on this PC; approve the connection, then sign in from your AI client. I handle the technical setup.” Offer **Let's do it · I have my own domain · Another way · Details**. A domain or another provider uses `docs/AGENT_CONNECTIONS.md`, not the ngrok shortcut below.
- Explain once, not at every technical step. If the owner chooses Decide later, complete only the local installation and say internet/client setup is unfinished.

If ngrok is chosen, let the owner open `https://dashboard.ngrok.com/get-started/your-authtoken` and sign in **while you install and check the app**. Say that the key will be entered later in the PC window, not chat. This overlaps personal sign-in with downloads; do not block installation on the account page or ask them to send you the key.

## 2. Preflight and install

After the owner agrees to install, inspect only what is needed:

1. Locate `installer/install.ps1`, `tray/build/ZeroIDE-MCP.exe`, `server/server.mjs`, `agent-manifest.json` and the `tools` folder. Missing EXE means a source archive, not an installable release: stop and request `zeroide-mcp.zip` from Releases. Do not build on the owner's PC.
2. Check `%LOCALAPPDATA%\ZeroIDE-MCP` and the pre-rename folder `%LOCALAPPDATA%\AgentPower`. If either exists, **stop and ask Update / Restore / Delete**. This installer is fresh-only; never remove, overwrite or reinstall an existing installation as a workaround.
3. Check TCP ports 39011 and 39012. If occupied, identify the conflict without secrets, then stop and ask the owner. Do not kill processes or select a different live port silently.

From the unpacked release root run exactly this existing entry point:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\install.ps1
```

It installs per user, downloads portable Node.js, uses the bundled server dependencies, generates local credentials without printing them, and creates the Start Menu shortcut. No administrator rights, .NET SDK or source compilation should be needed. SSH is not required. Do not hand-write `config.env`, `tray-config.json` or server code.

Open **ZeroIDE MCP** from the created Start Menu shortcut in the owner's desktop session. If launching via a command, ensure it survives the agent's command/job lifetime. Do not repeatedly launch more copies.

Tell the owner **“Open the tray app and press Enable MCP. The server starts off deliberately; I cannot enable it for you.”** Never click it, change configuration to enable it, or mistake the closed port before Enable for installation failure. Wait, do not poll indefinitely.

## 3. One ready-made local check

After the owner enables MCP, run the installed helper once:

```powershell
& "$env:LOCALAPPDATA\ZeroIDE-MCP\tools\Test-LocalMcp.ps1"
```

It reads credentials only in local memory, performs `initialize → initialized → tools/list`, checks the 14 built-in tools and closes its session. It never invokes tools, prints a token, enables MCP or counts as a real AI sign-in.

- `local_ready`: continue. This proves only local readiness.
- `waiting_for_owner` / `ENABLE_MCP`: ask the owner to enable, then rerun once after their confirmation.
- `failed`: report the returned safe phase/code and stop. Do not dump raw exceptions, HTTP bodies or credentials, or patch product files to make the check pass.

For a local-only client, proceed directly to step 5 with the exact local MCP URL from the app. No provider setup is needed.

## 4. Standard first ngrok connection — one helper, not generated code

Only after the owner chooses ngrok, and only when this is still a local-only installation with no pending connection change:

```powershell
& "$env:LOCALAPPDATA\ZeroIDE-MCP\tools\Setup-Ngrok.ps1" -OwnerChoseNgrok
```

Run this **one call in the background** if your command tool has a short timeout: it waits for the owner's PC windows. Tell them in advance:

1. Open `https://dashboard.ngrok.com/get-started/your-authtoken`, create/sign in to their own account and keep its **Authtoken** ready. This account key is **not the MCP password**.
2. Paste it into the masked **PC window**, never into chat. The agent receives only a protected configuration file path.
3. Review the app's connection permission window and press Allow if they agree. Never approve it for them.

The helper downloads official ngrok if needed, checks its Windows signature and publisher, shows progress in the app, requests protected key entry, and uses the existing coordinator to prepare → request owner launch consent → discover the assigned URL → check this server's identity → activate. First internet setup uses the app's combined launch/activation consent; no redundant agent confirmation is needed. It does not start ngrok outside the app, bypass consent, stop an existing ngrok process, adopt another draft or replace a working internet connection.

Interpret the final JSON:

- `awaiting_client`: copy the returned URL and proceed immediately to the chosen client. **Not finished yet.** Keep `taskId` for clearing progress after real success.
- `waiting_for_owner`: show the stated owner action; do not invent missing consent.
- `cancelled`: respect cancellation. The current connection is unchanged.
- `needs_attention`: stop the shortcut and inspect state with `tools/Configure-Connection.ps1 -Action state`. A candidate or already-active URL may remain after an interrupted response. **Do not rerun automatically, cancel blindly or replace registry/configuration files.** Use `docs/AGENT_CONNECTIONS.md` for deliberate recovery or a different provider.

A timeout while a PC consent window is open is not permission to issue the same request again. Check the original background job and app state first.

## 5. Finish in the actual AI client

Give short **client-specific** steps based on current verified client instructions: where to add MCP, the exact URL, and the supported authentication method. Do not guess settings paths, plan eligibility or OAuth support.

- Prefer **OAuth** when the client supports this server's flow: complete sign-in and approve the access card on this PC. On the ngrok warning page, **Visit Site** may be needed before continuing OAuth.
- If Bearer authentication is required, use **Access** in the app to create a separate named token, with only the needed permissions and an expiration where available. The owner enters it directly into their client, never into chat. Explain how to revoke it.
- The **Legacy / permanent MCP token** already exists at `%LOCALAPPDATA%\ZeroIDE-MCP\server\config.env` (`MCP_TOKEN`). It is a compatibility credential, **not the default onboarding route**. Do not display it, read it back through a tool or put it in command arguments/logs. The local check reads it internally without exposing it. If a client cannot use OAuth or a suitable named token, explain the limitation and ask before resorting to legacy access.
- Provider key, MCP token and the app's local control credential are different secrets. Never interchange them.

Success requires the **chosen real client** to sign in, list tools and perform an owner-approved harmless check. A localhost check, public HTTP 200, process running, or initialize response alone is not completion. If the real-client check fails, say what is ready and what remains; do not claim success.

After real success, clear **only your own** progress task via the coordinator (`progress` with `{ "taskId": "<returned taskId>", "clear": true }`, using a non-secret body file). Explain: **Connection** holds the URL and connection check; **Access** holds clients, tokens and revocation. Disable MCP or exit the tray to disconnect. Do not leave a claim of completed onboarding while client setup is pending.

## Time and failure discipline

The aim is a short, understandable path, not skipping safety. Use ready-made helpers and one concise update when work moves to a new stage. Do not re-read the entire source tree, ask the owner to choose among many providers, generate scripts, repeat checks, or perform unrelated diagnostics on the happy path.

Downloads, provider account registration and client sign-in vary. **Do not promise a measured 3–4 minute total before a fresh-machine timed test.** Report installation/setup time separately from owner absence, but do include necessary owner clicks, key entry and client sign-in in the end-to-end experience.

For failures, return a safe summary and the failing phase. A download can be retried once only if no install/setup mutation occurred; after an ambiguous or partially completed operation inspect state first. Never use reinstall, a server restart, disabled authorization, raw secret dumps or source edits as a troubleshooting shortcut. The manual advanced contract is `docs/AGENT_CONNECTIONS.md`.
