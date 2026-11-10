# Universal connections: agent setup, owner control

Status: approved product direction; phase 1 implementation on `feature/universal-connections`.
Production rollback point: `5177f605167921a78d72a3c361790b7d7bb2bd13`.
No production configuration or running tunnel is migrated by this branch.

## Product contract

The agent asks what the owner needs, explains viable alternatives, installs/configures the selected route and verifies it. The owner does not need to understand tunnel commands, DNS, API ports or provider tokens to use the Connections screen. A provider is an implementation detail, not a top-level app mode. No project-hosted gateway, mandatory domain purchase or project SaaS account.

The application controls the result: current MCP URL, power/ownership, reachability, address replacement, restart when supported, alternate routes and rollback. Agent installation still needs a local executor or an already-authorized connection; a public URL alone cannot bootstrap access to a disconnected PC.

### Main screen

1. `MCP connection URL` with a standard app Copy button; explicit empty/not-configured state.
2. Separate server and connection statuses; current route name, access scope, address lifetime, last verification time and vantage point. Never a single misleading Connected badge.
3. Capability-driven app buttons: Check connection / Change address / Restart / Change connection. Restart exists only for app-owned transports. Address discovery exists only if supported; it does not create provider domains.
4. A small auto-start-with-MCP setting for managed routes. External routes explicitly say that their lifecycle belongs to another program/service.
5. Collapsed Connection details / Help for this installation / Diagnostics. No provider tutorial by default. Help text is generated/updated by the configuring agent and treated as inert text, never executed or rendered as trusted commands.
6. Alternate saved profiles are secondary, not three competing installation wizards. Local access is always identifiable and retained. SSH is not an ordinary top-level choice; an existing SSH route is preserved as a profile.

Use existing Theme rendering and app button helpers, not unrelated HTML styles. UI edits follow runtime contracts and real capabilities. Provider names are editable labels, not a whitelist.

## Independent concepts

| Concept | Meaning |
|---|---|
| Access scope | Local / private network / public internet |
| Endpoint | Full MCP URL, including custom path and trailing slash |
| Ownership | Local server / managed child process / external service |
| Adapter | Optional built-in support for discovery/configuration, or generic launch |
| Address lifetime | Stable / temporary / unknown; stable does not mean unlimited/free |
| Verification | Timestamped evidence, what passed, vantage point and candidate binding |

Remote URLs must use HTTPS. Local URLs allow loopback HTTP/HTTPS only. Credentials, query strings and fragments in endpoint URLs are rejected in v1; this is a deliberate safety constraint, not an MCP protocol limitation. Do not synthesize `/mcp` for new profiles. The selected route may rewrite its public path to the local `/mcp`; OAuth endpoints require their own route mapping.

Any provider may supply an **external** URL without new application code. **Managed** custom transports additionally need an explicit executable path, argument array, working directory/config-file reference, and owner-approved launch plan. Generic transport support is not a claim that arbitrary providers support MCP streaming or OAuth.

## Profile schema and persistence

Implemented data types: ConnectionProfile, ConnectionLaunch, ConnectionCatalog, PreparedConnection, ConnectionHistory. Registry is a separate machine-local `connections-v2.json`, not an additional set of provider-specific TrayConfig fields.

- Schema version and monotonic revision; reject newer/unknown schemas and unknown JSON members.
- Atomic durable replacement, last-file backup and interprocess writer lock.
- One pending change with operation ID; prepare never replaces the active profile.
- Bounded profile/history counts; no reset-to-default on malformed data.
- Exact candidate fingerprint binds endpoint, ownership and launch plan.
- Launch approvals are NOT a writable boolean in profile JSON. Phase 2 must keep approval in a separately protected owner-controlled store bound to launch fingerprint; executable/config replacement must invalidate approval or require revalidation. No elevation by default.
- Provider credentials are stored in protected files/Windows secret storage, not command lines, profile JSON, stdout or audit logs. Basic inline-credential flag rejection is defense-in-depth, NOT a complete credential detector.
- Metadata is not executable. Executable invocation uses ProcessStartInfo.ArgumentList and UseShellExecute=false; never a shell string assembled from URL/provider output.

The phase-1 store's `CommitSelection` changes saved selection ONLY. It starts/stops no process. It accepts runtime-only verification evidence and is deliberately not exposed as a CLI activation command. A record constructor is not a cryptographic security boundary: phase 2 APIs must generate evidence in the verifier, never deserialize an agent-provided `verified: true` or verification record.

## Migration

Read legacy TrayConfig through an explicit allow-list. Copy saved endpoints and retain active/inactive choices, including old SSH. Do not copy ControlSecret, tokens or the entire legacy JSON.

Phase 1 imports an **inventory**, with external ownership for all pre-existing routes. This is intentional: an offline tool must not adopt/kill a live process or claim that the new registry already controls it. Existing TrayConfig and runtime are untouched. Import refuses to overwrite an initialized store. The later runtime migration may convert a known built-in route to managed ownership only after matching its exact configuration and verifying it; do not run two competing supervisors.

## Runtime coordinator (phase 2)

Capabilities: inspect / prepare / start candidate / discover URL / probe / commit selection / retire previous / restart owned process / cancel / restore previous. External route provides inspect and probe; it has no fabricated start/stop support.

State machine:

`draft -> prepared -> waiting-for-owner|starting -> discovering -> verifying -> ready-to-switch -> activating -> active`

Failures become `failed` with old route retained. Cancellation tears down only candidate resources owned by that operation. Crash recovery consults a durable operation journal before touching processes. Process IDs alone do not prove ownership (PID reuse): retain exact executable/config/start-time and owner job/handle. Never kill by executable name or adopt unrelated user processes.

### Safe handover

1. Prepare against expected registry revision; no effect on the working route.
2. Resolve required owner login/permission; stage private credentials.
3. Start only the candidate's owned resources, when parallel operation is supported.
4. Discover and validate the real URL; do not trust arbitrary stdout as commands.
5. Verify server identity and MCP initialize with current auth policy.
6. Show old/new address, possible OAuth reauthorization, and explicit owner/agent-approved switch.
7. Persist selection atomically; reconnect/check intended client; retire old owned route only after acknowledgement or explicit owner decision.

An existing ngrok account/domain, exclusive port or system service may NOT support two routes at once. Detect such cases; report an interruption and obtain confirmation instead of promising zero downtime. Keep rollback instructions and old configuration. A timeout must not silently disable the only functioning route. Never auto-delete provider domains, user accounts or external services.

Rollback metadata is not rollback runtime: restoration must re-establish and verify the old route. Restoring an expired temporary URL alone is not a successful recovery.

## Verification and security gates

Separate evidence levels:

- local server alive;
- owned process running / external ownership;
- DNS/TLS/HTTP reached from this PC;
- identity challenge proves route reaches THIS MCP server;
- authorized MCP initialize succeeds;
- intended remote client actually connected.

A generic `/health` response, HTTP 200, HTML login/interstitial or an open process is not sufficient. Do not mark client reachability from a local check. A 401 can mean routing worked, not successful authorization.

Before production use of arbitrary transports:

- Require the control secret on ALL `/local/*` endpoints, including status. Headers, Host and loopback alone cannot distinguish a rewriting tunnel from a genuine local client.
- Keep administrative control on a separate loopback listener; never tunnel control port 39012. Apply auth to new routes before reading request bodies; reject cross-origin browser requests and bound request sizes/timeouts.
- Public health is always minimal. No machine paths, task history, command text, token counts or configuration dump.
- Add bounded, expiring identity challenge proof using a dedicated server instance key. Do not publish that key or reuse the MCP Bearer token as a challenge secret.
- No network calls during profile validation/import/prepare. Verification sends no authorization to an arbitrary newly supplied host until the owner approves that endpoint. No automatic redirects, proxy bypass surprises, credential forwarding across hosts, or probing `/local/*` through a public URL.
- No automatic public polling that consumes provider request quotas; local adapter polling is bounded. User/agent initiated checks have explicit timeouts and byte caps.
- Validation evidence is bound to operation ID, full candidate fingerprint and freshness. Changes invalidate old evidence.

## OAuth and multiple routes

A route includes more than its MCP URL: authorization discovery, issuer/canonical public base and routing for registration/authorize/token must remain consistent. Custom path prefixes need explicit metadata and well-known endpoint routing; do not assume MCP path equals OAuth base. Switching hosts can require reauthorization; do not reuse/send client secrets or Bearer tokens to a new host automatically.

Phase 2 must preserve the current issuer until the candidate OAuth mapping is validated. Some issuer configurations prevent parallel OAuth migration; obtain explicit interruption consent if required. No speculative claim that adding a URL makes OAuth work.

## Implemented runtime and agent interface

See [AGENT_CONNECTIONS.md](AGENT_CONNECTIONS.md) for the implemented API and exact operational contract. The generic runtime, protected owner launch consent, identity-bound MCP verifier, native owner control panel, external-service ownership and retained-route handover are now integrated. Existing ngrok/Cloudflare/legacy installations keep their current runtime until an explicit handover.

The earlier sections describe the design target, not a claim that every release qualification item is complete. Implemented mutations use revisions and pending operation IDs; after an interrupted call reload state, rather than assuming an exactly-once queue. Remote client success is deliberately not inferred or automatically acknowledged. Retiring a retained old route and rollback are owner UI actions. Arbitrary-provider address discovery is performed by the installer agent.

## Validation

- Registry: 45 tests; offline CLI: 8 checks.
- Native runtime suite: network identity, redirect/credential safeguards, MCP sessions, local API authorization, generic owned/external lifecycle, approvals, URL edits, rollback and RU/EN render states.
- Real server suite: 22 checks for local-data protection, per-boot challenge and authenticated MCP initialize/session cleanup.
- Native windows are rendered and visually inspected at 1180×800 and compact 1000×680, including dialogs and help/local/error/off/external states.

## Release qualification still required

Fresh-machine installation, reboot/crash fault injection, each provider's real end-to-end handover and OAuth issuer/prefix migration are release gates, not established by the generic process fixture. Cloudflare and Tailscale public access remain unconfirmed on the development PC. The private development branch is not the announced open-source v1.0 release.
