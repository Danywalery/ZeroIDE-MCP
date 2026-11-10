# Short onboarding: implementation and acceptance

This describes the current working-tree changes, not a published release or measured fresh-machine timing.

## What changed

- `AGENT_INSTALL.md` starts with the owner-facing explanation and AI-client choice, before commands. It explains files/commands, compatible web chats, separate connections to several PCs, access controls and Windows-user permissions (not a sandbox).
- One recommendation for connection; a normal first cloud-client setup can use ngrok. The owner can sign in to the provider while installation runs.
- `tools/Test-LocalMcp.ps1` performs the authenticated local protocol check and closes the session. It does not execute tools, enable MCP or count as a real client.
- `tools/Setup-Ngrok.ps1 -OwnerChoseNgrok` drives the existing local coordinator for a first local-only installation. Official HTTPS download, Windows publisher signature, protected key window, owner launch consent, assigned URL discovery, server-identity check and activation are in one helper call.
- Libraries: `tools/Onboarding.Common.ps1` and `tools/Onboarding.Ngrok.ps1`. The latter has a UTF-8 BOM deliberately: Windows PowerShell 5.1 must decode its Russian UI strings correctly.
- Progress follows the tray's RU/EN language. Scripts return safe phase/status results; the agent explains them in the owner's language.
- OAuth is preferred where supported; otherwise a separately named least-privilege token with expiry where supported. Legacy `MCP_TOKEN` is explained as a compatibility credential, not displayed or recommended by default.
- Installer, release workflow and manifest include all helpers; CI runs helper tests.

## Safety and recovery boundaries

The shortcut is not an updater or migration command. It refuses an existing public URL, pending operation, retained previous connection, another active guide or existing ngrok process. It does not enable MCP, approve windows, launch ngrok outside the tray, kill unrelated processes or rewrite live configuration.

Keys are entered only through the PC window. The returned protected config path is passed to the supervised launch plan, never the key. Local control and MCP credentials are read in memory and never returned. The ngrok API URL must refer to the exact local upstream and an expected ngrok HTTPS hostname; the coordinator then independently checks the server identity before sending MCP authorization.

On an error/ambiguous response the helper stops without blind retries or rollback. Inspect the original job and coordinator state; a draft may remain, or activation may already have happened. An error in the final progress/note step does not prove activation failed. Use the advanced guide for deliberate recovery.

`local_ready` and `awaiting_client` are intermediate outcomes. Only the chosen real AI client signing in, listing tools and completing an owner-approved harmless check finishes onboarding. Clear only the helper's returned task ID after that.

## Validation performed

| Suite | Passed checks |
| --- | ---: |
| Connection registry | 45 |
| CLI smoke | 8 |
| Native runtime | 80 |
| Isolated server, including the actual PowerShell local-check helper | 26 |
| Installer safety and package contract | 18 |
| Onboarding helper unit tests | 57 |
| **Total deterministic checks** | **234** |

Additional optional network test: `tests/onboarding-download.ps1` downloaded official ngrok into a unique temporary directory, extracted only the expected executable and verified its Windows publisher signature. It did not execute ngrok and removed its temporary directory. This is not an end-to-end tunnel test.

The helper suite covers owner-choice/Enable guards, existing state/process refusal, cancelled key entry, CAS changes, launch/check/activation errors, no automatic retry/rollback, JSON and SSE protocol parsing, session cleanup, missing credentials/tools, RU text decoding, publisher rejection, and ZIP traversal entries. `server-proof.mjs` also confirms the shipped local helper never manufactures real-client evidence or prints credentials.

The native UI source was not changed in this iteration. Native tests regenerated the existing render fixtures; this iteration does not claim a new visual redesign.

## Next clean-machine acceptance test

Use a newly packaged archive containing these helpers, not the older released ZIP. No installation or publication was performed for these changes.

1. Confirm a clean app state with the owner; do not delete a working setup automatically. Use a new agent chat and attach the archive with only a request to unpack and follow its instructions.
2. Before any install commands, verify that the agent explains the project and asks which AI to connect.
3. Choose a cloud AI and ngrok. Start the stopwatch at the initial request. Keep a separate record of owner absence, downloads, agent processing, required clicks, account/key entry and client sign-in. Required human steps remain part of end-to-end time.
4. Verify that the agent calls the shipped local check and ngrok helper, rather than generating their own code or exploring the source tree.
5. Check masked key entry, one first-public launch/activation consent, RU/EN progress, no secret output, and correct cancellation behavior.
6. Connect the actual selected AI client using OAuth or a suitable named token. Verify tools and an approved harmless operation. Only then stop the completion timer and clear progress.
7. Record friction and total time. A **3–4 minute goal is not yet a measured result or guarantee**. New-account registration and client/provider limitations can exceed it.

Tailscale, other provider onboarding, native no-agent wizard, legacy-token reissue UI and optional smart memory are not implemented by this change. No production restart/install, git commit/push or release publication is included.
