import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";
const BASE = process.env.TEST_BASE;
const ROOT = process.env.TEST_ROOT;
const MODE_FILE = process.env.TEST_TRAY_MODE_FILE;
if (!BASE || !ROOT || !MODE_FILE) { console.error("Run through tests/isolated-v04.mjs (needs TEST_BASE, TEST_ROOT and TEST_TRAY_MODE_FILE)."); process.exit(2); }
let failures = 0;
const check = (n, c, x = "") => { if (c) console.log("PASS: " + n); else { failures++; console.log("FAIL: " + n + " " + x); } };
const j = async (res) => { try { return await res.json(); } catch { return {}; } };

// 1. metadata
let prm = await j(await fetch(ROOT + "/.well-known/oauth-protected-resource"));
check("PRM resource + AS", prm.resource === ROOT + "/mcp" && Array.isArray(prm.authorization_servers), JSON.stringify(prm).slice(0, 120));
let asm = await j(await fetch(ROOT + "/.well-known/oauth-authorization-server"));
check("AS metadata endpoints + S256", asm.token_endpoint === ROOT + "/oauth/token" && (asm.code_challenge_methods_supported || []).includes("S256"), JSON.stringify(asm).slice(0, 120));

// 2. DCR
let r = await fetch(ROOT + "/oauth/register", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ client_name: "Evil", redirect_uris: ["http://evil.example.com/cb"] }) });
check("DCR rejects a plain-http remote redirect", r.status === 400, "HTTP " + r.status);
r = await fetch(ROOT + "/oauth/register", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ client_name: "TestChat", redirect_uris: ["http://127.0.0.1:9999/cb"], grant_types: ["authorization_code", "refresh_token"], token_endpoint_auth_method: "none" }) });
const client = await j(r);
check("DCR registers public client", r.status === 201 && !!client.client_id, "HTTP " + r.status + " " + JSON.stringify(client).slice(0, 100));
const REDIRECT = "http://127.0.0.1:9999/cb";

async function authorizeFlow(scopeStr) {
  const verifier = crypto.randomBytes(32).toString("base64url");
  const challenge = crypto.createHash("sha256").update(verifier).digest("base64url");
  const u = new URL(ROOT + "/oauth/authorize");
  u.searchParams.set("client_id", client.client_id);
  u.searchParams.set("redirect_uri", REDIRECT);
  u.searchParams.set("response_type", "code");
  u.searchParams.set("code_challenge", challenge);
  u.searchParams.set("code_challenge_method", "S256");
  u.searchParams.set("state", "xyz123");
  if (scopeStr) u.searchParams.set("scope", scopeStr);
  const page = await (await fetch(u)).text();
  const m = page.match(/consent_[a-f0-9]+/);
  if (!m) throw new Error("no consent id in page: " + page.slice(0, 150));
  let status = {};
  for (let i = 0; i < 25; i++) {
    status = await j(await fetch(ROOT + "/oauth/authorize/status?id=" + m[0]));
    if (status.status !== "pending") break;
    await new Promise((res) => setTimeout(res, 400));
  }
  return { status, verifier };
}

async function mcpCall(token, name, args) {
  let sid = null, seq = 0;
  async function post(body, notif = false) {
    const headers = { "Content-Type": "application/json", Accept: "application/json, text/event-stream", "MCP-Protocol-Version": "2025-03-26", Authorization: "Bearer " + token };
    if (sid) headers["Mcp-Session-Id"] = sid;
    const res = await fetch(ROOT + "/mcp", { method: "POST", headers, body: JSON.stringify(body) });
    if (res.headers.get("mcp-session-id")) sid = res.headers.get("mcp-session-id");
    const text = await res.text();
    if (notif) return { status: res.status };
    if (res.status !== 200) return { httpStatus: res.status, text: text.slice(0, 140) };
    try { const jj = JSON.parse(text); if (jj.id === body.id) return jj; } catch {}
    for (const block of text.split(/\r?\n\r?\n/)) { const l = block.split(/\r?\n/).find(x => x.startsWith("data:")); if (l) { try { const jj = JSON.parse(l.slice(5).trim()); if (jj.id === body.id) return jj; } catch {} } }
    return { text: text.slice(0, 140) };
  }
  seq++; const init = await post({ jsonrpc: "2.0", id: seq, method: "initialize", params: { protocolVersion: "2025-03-26", capabilities: {}, clientInfo: { name: "ot", version: "1" } } });
  if (init.httpStatus) return { httpStatus: init.httpStatus, text: init.text };
  await post({ jsonrpc: "2.0", method: "notifications/initialized", params: {} }, true);
  seq++; const jj = await post({ jsonrpc: "2.0", id: seq, method: "tools/call", params: { name, arguments: args } });
  const c = (jj.result?.content || []).map(x => x.text || "").join("\n");
  return { isError: !!jj.result?.isError, text: c };
}

async function tokenRequest(params) {
  const res = await fetch(ROOT + "/oauth/token", { method: "POST", headers: { "Content-Type": "application/x-www-form-urlencoded" }, body: new URLSearchParams(params).toString() });
  return { status: res.status, body: await j(res) };
}

// 3-6. full allow flow
fs.writeFileSync(MODE_FILE, "allow");
let flow = await authorizeFlow("mcp.readwrite");
check("consent approved via tray", flow.status.status === "approved", JSON.stringify(flow.status).slice(0, 120));
const codeUrl = new URL(flow.status.redirect);
const code = codeUrl.searchParams.get("code");
check("code + state returned", !!code && codeUrl.searchParams.get("state") === "xyz123", flow.status.redirect.slice(0, 120));
let bad = await tokenRequest({ grant_type: "authorization_code", code, client_id: client.client_id, redirect_uri: REDIRECT, code_verifier: "wrong-verifier-wrong-verifier-wrong-12" });
check("wrong PKCE verifier rejected", bad.status === 400 && bad.body.error === "invalid_grant", JSON.stringify(bad.body).slice(0, 100));
// note: the failed PKCE attempt must NOT burn the code in our implementation order? code is marked used before PKCE check -> use a fresh flow for the good exchange
flow = await authorizeFlow("mcp.readwrite");
const code2 = new URL(flow.status.redirect).searchParams.get("code");
let good = await tokenRequest({ grant_type: "authorization_code", code: code2, client_id: client.client_id, redirect_uri: REDIRECT, code_verifier: flow.verifier });
check("token exchange ok", good.status === 200 && !!good.body.access_token && !!good.body.refresh_token, JSON.stringify(good.body).slice(0, 120));
let reuse = await tokenRequest({ grant_type: "authorization_code", code: code2, client_id: client.client_id, redirect_uri: REDIRECT, code_verifier: flow.verifier });
check("code reuse rejected", reuse.status === 400 && reuse.body.error === "invalid_grant");
const access1 = good.body.access_token, refresh1 = good.body.refresh_token;

// 7. access token works on /mcp with scope limits
let mc = await mcpCall(access1, "read_file", { path: path.join(BASE, "project", "jail", "inside.txt") });
check("oauth access reads", !mc.isError && mc.text.includes("INSIDE-JAIL"), (mc.text || "").slice(0, 110));
mc = await mcpCall(access1, "run_command", { command: "echo hi" });
check("oauth readwrite blocks exec by scope", mc.isError && mc.text.includes("TOKEN_SCOPE_DENIED"), (mc.text || "").slice(0, 110));

// 8. refresh rotation + reuse detection
let rot = await tokenRequest({ grant_type: "refresh_token", refresh_token: refresh1, client_id: client.client_id });
check("refresh rotates", rot.status === 200 && !!rot.body.access_token && rot.body.refresh_token !== refresh1, JSON.stringify(rot.body).slice(0, 90));
const access2 = rot.body.access_token, refresh2 = rot.body.refresh_token;
let reuseOld = await tokenRequest({ grant_type: "refresh_token", refresh_token: refresh1, client_id: client.client_id });
check("old refresh reuse rejected", reuseOld.status === 400, "HTTP " + reuseOld.status);
let afterKill = await tokenRequest({ grant_type: "refresh_token", refresh_token: refresh2, client_id: client.client_id });
check("family dead after reuse (new refresh invalid)", afterKill.status === 400, "HTTP " + afterKill.status);
mc = await mcpCall(access2, "read_file", { path: path.join(BASE, "project", "jail", "inside.txt") });
check("family access token revoked after reuse", mc.httpStatus === 401, JSON.stringify(mc).slice(0, 110));

// 9. deny flow
fs.writeFileSync(MODE_FILE, "deny");
flow = await authorizeFlow("mcp.read");
check("consent denied propagates", flow.status.status === "denied" && new URL(flow.status.redirect).searchParams.get("error") === "access_denied", JSON.stringify(flow.status).slice(0, 130));
fs.writeFileSync(MODE_FILE, "allow");

// 10. revoke endpoint
flow = await authorizeFlow("mcp.read");
const code3 = new URL(flow.status.redirect).searchParams.get("code");
let g3 = await tokenRequest({ grant_type: "authorization_code", code: code3, client_id: client.client_id, redirect_uri: REDIRECT, code_verifier: flow.verifier });
check("second flow tokens ok", g3.status === 200 && !!g3.body.access_token);
await fetch(ROOT + "/oauth/revoke", { method: "POST", headers: { "Content-Type": "application/x-www-form-urlencoded" }, body: new URLSearchParams({ token: g3.body.access_token }).toString() });
mc = await mcpCall(g3.body.access_token, "read_file", { path: path.join(BASE, "project", "jail", "inside.txt") });
check("revoked access -> 401", mc.httpStatus === 401, JSON.stringify(mc).slice(0, 110));

// 11. audit trail
const auditText = fs.readFileSync(path.join(BASE, "logs", "audit.jsonl"), "utf8");
check("audit has oauth.consent_allowed", auditText.includes("oauth.consent_allowed"));
check("audit has oauth.consent_denied", auditText.includes("oauth.consent_denied"));
check("audit has oauth.refresh_reuse_detected", auditText.includes("oauth.refresh_reuse_detected"));

console.log(failures === 0 ? "OAUTH-TEST: ALL PASS" : "OAUTH-TEST: " + failures + " FAILURES");
process.exit(failures ? 1 : 0);
