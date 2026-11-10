// Security regression tests for server/server.mjs (fixes from the 2026-11-10 audit).
// Fully isolated: the server is copied to a temp folder; every scenario gets fresh
// temp data, free ports, a fake tray and a CLEAN environment built from scratch,
// so a run from inside a live MCP session can never reach the installed copy's
// config, tokens, OAuth grants or logs.
// Run: node tests/security-v04.mjs   (needs server/node_modules: npm ci --omit=dev)
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import net from "node:net";
import http from "node:http";
import crypto from "node:crypto";
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, "..");
const serverSrc = path.join(repo, "server", "server.mjs");
const deps = path.join(repo, "server", "node_modules");
if (!fs.existsSync(path.join(deps, "express"))) {
  console.error("server/node_modules is missing: run npm ci --omit=dev in server/");
  process.exit(2);
}

const base = fs.mkdtempSync(path.join(os.tmpdir(), "zeroide-sec-"));
const children = [];
const results = [];
const watchdog = setTimeout(() => {
  console.error("TIMEOUT: the test run took longer than 5 minutes");
  for (const c of children) { try { c.kill(); } catch {} }
  process.exit(3);
}, 300000);

function check(name, ok, detail) {
  results.push({ name, ok: !!ok });
  console.log((ok ? "PASS " : "FAIL ") + name + (ok || detail === undefined ? "" : "  :: " + String(detail).slice(0, 400)));
}
const secret = (prefix) => prefix + crypto.randomBytes(18).toString("hex");
const sha256 = (s) => crypto.createHash("sha256").update(Buffer.from(s, "utf8")).digest("hex");
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const readIf = (p) => { try { return fs.readFileSync(p, "utf8"); } catch { return ""; } };

function freePort() {
  return new Promise((resolve, reject) => {
    const s = net.createServer();
    s.on("error", reject);
    s.listen(0, "127.0.0.1", () => { const { port } = s.address(); s.close(() => resolve(port)); });
  });
}

// Only what Windows and Node need; nothing named MCP_*, TRAY_*, PORT or PROJECT_DIR leaks in.
function cleanEnv(extra) {
  const keep = ["SystemRoot", "windir", "PATH", "PATHEXT", "TEMP", "TMP", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "ComSpec", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "OS", "SystemDrive", "ProgramData", "ProgramFiles", "HOME", "LANG"];
  const env = {};
  for (const k of keep) if (process.env[k] !== undefined) env[k] = process.env[k];
  return Object.assign(env, extra);
}

// Fake tray: answers health checks; approval requests are held open, so OAuth
// consent cards stay "waiting for the owner".
function startFakeTray(port) {
  const held = [];
  const srv = http.createServer((req, res) => {
    req.resume();
    if (req.url.startsWith("/approval/request")) { held.push(res); return; }
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end('{"ok":true}');
  });
  return new Promise((resolve) => srv.listen(port, "127.0.0.1", () => resolve({
    close: () => new Promise((r) => {
      for (const h of held) { try { if (h.socket) h.socket.destroy(); } catch {} }
      if (srv.closeAllConnections) srv.closeAllConnections();
      srv.close(() => r());
    }),
  })));
}

async function startServer(name, opts = {}) {
  const dir = path.join(base, name);
  const project = path.join(dir, "project");
  const logs = path.join(dir, "logs");
  fs.mkdirSync(project, { recursive: true });
  fs.mkdirSync(logs, { recursive: true });
  fs.copyFileSync(serverSrc, path.join(dir, "server.mjs"));
  fs.symlinkSync(deps, path.join(dir, "node_modules"), "junction");
  if (opts.tokensFile !== undefined) fs.writeFileSync(path.join(dir, "tokens.json"), opts.tokensFile, "utf8");
  const port = await freePort();
  const trayPort = await freePort();
  const tray = await startFakeTray(trayPort);
  const control = secret("ctl_");
  const env = cleanEnv({
    PORT: String(port),
    MCP_HOST: "127.0.0.1",
    MCP_CONFIG_FILE: path.join(dir, "config.env"),
    MCP_LOG_DIR: logs,
    MCP_PERMISSIONS_FILE: path.join(dir, "permissions.json"),
    MCP_TOKENS_FILE: path.join(dir, "tokens.json"),
    MCP_OAUTH_FILE: path.join(dir, "oauth.json"),
    PROJECT_DIR: project,
    TRAY_CONTROL_PORT: String(trayPort),
    TRAY_CONTROL_SECRET: control,
    TRAY_HEARTBEAT_SEC: "120",
    TRAY_HEARTBEAT_MISSES: "100",
    MCP_LANG: "en",
    ...(opts.env || {}),
  });
  const child = spawn(process.execPath, [path.join(dir, "server.mjs")], { cwd: dir, env, stdio: ["ignore", "pipe", "pipe"], windowsHide: true });
  children.push(child);
  let out = "";
  child.stdout.on("data", (d) => { out += d; });
  child.stderr.on("data", (d) => { out += d; });
  const url = "http://127.0.0.1:" + port;
  let up = false;
  for (let i = 0; i < 100 && !up && child.exitCode === null; i++) {
    try { const r = await fetch(url + "/health"); await r.arrayBuffer(); up = r.ok; } catch {}
    if (!up) await sleep(150);
  }
  if (!up) throw new Error(name + ": server did not start. Output: " + out.slice(-800));
  return {
    dir, url, project, logs, control,
    output: () => out,
    async stop() { try { child.kill(); } catch {} await sleep(250); await tray.close(); },
  };
}

const INIT = { jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "2025-03-26", capabilities: {}, clientInfo: { name: "security-v04", version: "1" } } };

async function rpc(srv, body, o = {}) {
  const headers = { "Content-Type": "application/json", Accept: "application/json, text/event-stream", ...(o.headers || {}) };
  if (o.token) headers.Authorization = "Bearer " + o.token;
  if (o.session) headers["mcp-session-id"] = o.session;
  const r = await fetch(srv.url + "/mcp" + (o.query || ""), { method: "POST", headers, body: typeof body === "string" ? body : JSON.stringify(body) });
  const text = await r.text();
  let json = null;
  try {
    if ((r.headers.get("content-type") || "").includes("text/event-stream")) {
      const data = text.split(/\r?\n/).filter((l) => l.startsWith("data:")).map((l) => l.slice(5).trim()).filter(Boolean);
      json = data.length ? JSON.parse(data[data.length - 1]) : null;
    } else if (text) {
      json = JSON.parse(text);
    }
  } catch {}
  return { status: r.status, headers: r.headers, text, json };
}

async function openSession(srv, token, o = {}) {
  const r = await rpc(srv, INIT, { token, ...o });
  const session = r.headers.get("mcp-session-id");
  if (r.status === 200 && session) await rpc(srv, { jsonrpc: "2.0", method: "notifications/initialized" }, { token, session, ...o });
  return { r, session };
}

async function callTool(srv, token, session, name, args, id) {
  const r = await rpc(srv, { jsonrpc: "2.0", id, method: "tools/call", params: { name, arguments: args } }, { token, session });
  const res = r.json && r.json.result;
  const text = res && Array.isArray(res.content) ? res.content.map((c) => c.text || "").join("\n") : r.text;
  return { status: r.status, isError: !!(res && res.isError), text };
}

const postJson = (srv, p, body) => fetch(srv.url + p, { method: "POST", headers: { "Content-Type": "application/json" }, body: typeof body === "string" ? body : JSON.stringify(body) });

function findSecret(dir, needle) {
  const stack = [dir];
  while (stack.length) {
    const d = stack.pop();
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      if (e.name === "node_modules" || e.name === "server.mjs") continue;
      const p = path.join(d, e.name);
      if (e.isDirectory()) stack.push(p);
      else if (e.isFile()) { try { if (fs.readFileSync(p).includes(needle)) return p; } catch {} }
    }
  }
  return null;
}

async function main() {
  // S1: nothing configured and OAuth off. Before the fix this was "open mode".
  {
    const s = await startServer("s1-no-credentials", { env: { MCP_OAUTH_ENABLED: "0" } });
    const { r } = await openSession(s, null);
    check("S1 no credentials + OAuth off: initialize is rejected (was open mode)", r.status === 401, r.status + " " + r.text);
    check("S1 error code NO_CREDENTIALS_CONFIGURED", r.json && r.json.code === "NO_CREDENTIALS_CONFIGURED", r.text);
    const sse = await fetch(s.url + "/sse");
    await sse.arrayBuffer().catch(() => {});
    check("S1 GET /sse is rejected too", sse.status === 401, sse.status);
    const anyTok = await rpc(s, INIT, { token: "anything" });
    check("S1 a made-up token is rejected", anyTok.status === 401, anyTok.status);
    check("S1 startup warns about missing credentials", /no credentials configured/i.test(s.output()), s.output().slice(-300));
    let last = 0;
    let n = 0;
    for (n = 1; n <= 1300; n++) {
      const h = await fetch(s.url + "/health");
      await h.arrayBuffer();
      last = h.status;
      if (last === 429) break;
    }
    check("S1 the global rate limit now also covers early routes like /health", last === 429 && n > 1100, "status " + last + " after " + n + " requests");
    await s.stop();
  }

  // S2: no token configured, OAuth on (default): discovery, body limits, OAuth limits.
  {
    const s = await startServer("s2-oauth-only");
    const { r } = await openSession(s, null);
    check("S2 OAuth on, no token sent: 401 NO_TOKEN", r.status === 401 && r.json && r.json.code === "NO_TOKEN", r.status + " " + r.text);
    check("S2 the 401 carries WWW-Authenticate with resource_metadata", /resource_metadata=/.test(r.headers.get("www-authenticate") || ""), r.headers.get("www-authenticate"));
    const prm = await fetch(s.url + "/.well-known/oauth-protected-resource");
    await prm.arrayBuffer();
    check("S2 OAuth discovery stays public", prm.status === 200, prm.status);
    const bigBody = JSON.stringify({ jsonrpc: "2.0", id: 1, method: "initialize", params: { pad: "x".repeat(2 * 1024 * 1024) } });
    let bigStatus;
    const t0 = Date.now();
    try { bigStatus = (await rpc(s, bigBody)).status; } catch (e) { bigStatus = "closed:" + ((e.cause && e.cause.code) || e.message); }
    check("S2 2 MB unauthenticated POST /mcp is refused before parsing (401)", bigStatus === 401 || String(bigStatus).startsWith("closed:"), bigStatus + " in " + (Date.now() - t0) + " ms");
    const regBig = await postJson(s, "/oauth/register", { client_name: "x", redirect_uris: ["https://client.example/cb"], pad: "y".repeat(100 * 1024) });
    const regBigText = await regBig.text();
    check("S2 100 KB /oauth/register: 413 JSON payload_too_large", regBig.status === 413 && /payload_too_large/.test(regBigText), regBig.status + " " + regBigText.slice(0, 200));
    const regBad = await postJson(s, "/oauth/register", "{bad json");
    const regBadText = await regBad.text();
    check("S2 malformed JSON: 400 invalid_json", regBad.status === 400 && /invalid_json/.test(regBadText), regBad.status + " " + regBadText.slice(0, 200));
    check("S2 error bodies carry no stack trace or local paths", !/node_modules|\bat\s+\S+\s+\(|[A-Za-z]:\\|<pre>/i.test(regBigText + regBadText), (regBigText + regBadText).slice(0, 300));
    const reg = await postJson(s, "/oauth/register", { client_name: "Security Test", redirect_uris: [1, 2, 3, 4, 5, 6, 7].map((i) => "https://client.example/cb" + i), grant_types: ["authorization_code", "refresh_token"], response_types: ["code"], token_endpoint_auth_method: "none" });
    const client = await reg.json().catch(() => ({}));
    check("S2 dynamic client registration works", (reg.status === 201 || reg.status === 200) && !!client.client_id, reg.status + " " + JSON.stringify(client).slice(0, 200));
    const authz = (i) => fetch(s.url + "/oauth/authorize?" + new URLSearchParams({ response_type: "code", client_id: client.client_id || "", redirect_uri: "https://client.example/cb" + i, code_challenge: "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", code_challenge_method: "S256", state: "st" + i }), { redirect: "manual" });
    const codes = [];
    for (let i = 1; i <= 6; i++) { const a = await authz(i); await a.text(); codes.push(a.status); }
    check("S2 at most 5 different consent cards wait at once (the 6th gets 429)", codes.slice(0, 5).every((c) => c === 200) && codes[5] === 429, codes.join(","));
    const dup = await authz(1);
    await dup.text();
    check("S2 a repeated request still joins its waiting card (dedupe)", dup.status === 200, dup.status);
    const regStatus = [];
    for (let i = 0; i < 25; i++) {
      const x = await postJson(s, "/oauth/register", { client_name: "flood" + i, redirect_uris: ["https://client.example/f" + i], token_endpoint_auth_method: "none" });
      await x.text();
      regStatus.push(x.status);
      if (x.status === 429) break;
    }
    const firstLimited = regStatus.indexOf(429);
    check("S2 /oauth/register is rate-limited (20 per 15 min)", firstLimited >= 0 && firstLimited + 3 <= 20, regStatus.join(","));
    await s.stop();
  }

  // S3: legacy config.env token: auth edge cases, ?token=, tool metadata, search_files, limits.
  {
    const T1 = secret("legacy_");
    const s = await startServer("s3-legacy-token", { env: { MCP_TOKEN: T1 } });
    const wrong = await rpc(s, INIT, { token: "wrong-" + T1.slice(0, 4) });
    check("S3 wrong token: 401 UNKNOWN_TOKEN", wrong.status === 401 && wrong.json && wrong.json.code === "UNKNOWN_TOKEN", wrong.status + " " + wrong.text);
    const empty = await rpc(s, INIT, { headers: { Authorization: "Bearer " } });
    check("S3 empty Bearer: 401", empty.status === 401, empty.status);
    const viaQuery = await rpc(s, INIT, { query: "?token=" + encodeURIComponent(T1) });
    check("S3 ?token= is refused by default: 401 QUERY_TOKEN_DISABLED", viaQuery.status === 401 && viaQuery.json && viaQuery.json.code === "QUERY_TOKEN_DISABLED", viaQuery.status + " " + viaQuery.text);
    check("S3 the refusal explains how to send the token", !!(viaQuery.json && /Authorization: Bearer/.test(viaQuery.json.message || "")), viaQuery.text);
    const lower = await rpc(s, INIT, { headers: { Authorization: "bearer " + T1 } });
    check("S3 the Bearer scheme is case-insensitive", lower.status === 200, lower.status);
    const both = await rpc(s, INIT, { token: T1, query: "?token=garbage" });
    check("S3 a valid header wins over a junk ?token=", both.status === 200, both.status);
    const { r, session } = await openSession(s, T1);
    check("S3 legacy Bearer token: initialize OK", r.status === 200 && !!session, r.status + " " + r.text.slice(0, 200));

    const list = await rpc(s, { jsonrpc: "2.0", id: 2, method: "tools/list", params: {} }, { token: T1, session });
    const tools = (list.json && list.json.result && list.json.result.tools) || [];
    check("S3 tools/list returns the tools", tools.length >= 12, tools.length + " " + list.text.slice(0, 200));
    const expectAnn = {
      user_message: { readOnlyHint: true, destructiveHint: false },
      task_status: { readOnlyHint: true, destructiveHint: false },
      take_screenshot: { readOnlyHint: true, destructiveHint: false },
      run_command: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: true },
      job_status: { readOnlyHint: true },
      job_output: { readOnlyHint: true },
      read_file: { readOnlyHint: true },
      list_directory: { readOnlyHint: true },
      search_files: { readOnlyHint: true },
      write_file: { readOnlyHint: false, destructiveHint: true, idempotentHint: true },
      edit_file: { readOnlyHint: false, destructiveHint: true, idempotentHint: false },
      audit_log: { readOnlyHint: true },
      journal_entry: { readOnlyHint: false },
      journal_log: { readOnlyHint: true },
    };
    for (const [name, want] of Object.entries(expectAnn)) {
      const t = tools.find((x) => x.name === name);
      const got = (t && t.annotations) || {};
      const bad = Object.entries(want).filter(([k, v]) => got[k] !== v).map(([k, v]) => k + "=" + got[k] + " (want " + v + ")");
      check("S3 annotations of " + name, !!t && bad.length === 0, t ? bad.join("; ") : "tool missing");
    }
    const vps = tools.filter((t) => /\bVPS\b|root access/i.test(t.description || "")).map((t) => t.name);
    check("S3 no tool description talks about a VPS or root access", vps.length === 0, vps.join(","));
    const rc = tools.find((t) => t.name === "run_command");
    const shellWord = process.platform === "win32" ? /PowerShell/ : /bash/;
    check("S3 run_command description names the shell, the user's rights and approvals", !!rc && shellWord.test(rc.description) && /rights/.test(rc.description) && /approval/.test(rc.description), rc && rc.description.slice(0, 300));

    const P = s.project;
    fs.mkdirSync(path.join(P, "docs", "sub"), { recursive: true });
    fs.writeFileSync(path.join(P, "docs", "five.txt"), ["needle 1", "x", "needle 2", "needle 3", "needle 4", "needle 5"].join("\n"), "utf8");
    fs.writeFileSync(path.join(P, "docs", "sub", "cyr.txt"), "первая строка\nвторая: иголка в стоге\n", "utf8");
    fs.writeFileSync(path.join(P, "docs", "bom.txt"), "\ufeffиголка в начале\r\nконец строки end\r\n", "utf8");
    fs.writeFileSync(path.join(P, "docs", "utf16.txt"), Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from("строка UTF-16: иголка\r\n", "utf16le")]));
    fs.writeFileSync(path.join(P, "docs", "bin.dat"), Buffer.from([0x69, 0x67, 0x00, 0x01, 0x02]));
    const sf = (args, id) => callTool(s, T1, session, "search_files", { note: "security test", ...args }, id);
    let x = await sf({ query: "needle", path: path.join(P, "docs", "five.txt") }, 10);
    check("S3 search_files on a single file finds its matches (was 0)", !x.isError && /matches_total: 5/.test(x.text), x.text.slice(0, 300));
    check("S3 a single-file search is labelled as such", /single file|один файл/.test(x.text), x.text.slice(0, 200));
    x = await sf({ query: "needle", path: path.join(P, "docs", "five.txt"), include: "*.md" }, 11);
    check("S3 an explicit file is searched even if include does not match it", /matches_total: 5/.test(x.text), x.text.slice(0, 300));
    x = await sf({ query: "needle", path: path.join(P, "docs"), maxResults: 2 }, 12);
    check("S3 maxResults caps the output and still reports the true total", /matches_total: 5/.test(x.text) && /showing: 2/.test(x.text), x.text.slice(0, 300));
    x = await sf({ query: "иголка", path: path.join(P, "docs") }, 13);
    check("S3 folder search finds UTF-8 Cyrillic, UTF-8 with BOM and UTF-16LE files", /cyr\.txt/.test(x.text) && /bom\.txt/.test(x.text) && /utf16\.txt/.test(x.text) && /matches_total: 3/.test(x.text), x.text.slice(0, 500));
    x = await sf({ query: "^иголка", regex: true, path: path.join(P, "docs", "bom.txt") }, 14);
    check("S3 the BOM is stripped (regex ^ matches the first line)", /matches_total: 1/.test(x.text), x.text.slice(0, 300));
    x = await sf({ query: "end$", regex: true, path: path.join(P, "docs", "bom.txt") }, 15);
    check("S3 CRLF line ends do not break $ in a regex", /matches_total: 1/.test(x.text), x.text.slice(0, 300));
    x = await sf({ query: "needle", path: path.join(P, "docs"), include: "*.txt" }, 16);
    check("S3 folder search with include still works", /matches_total: 5/.test(x.text) && /files_with_matches: 1/.test(x.text), x.text.slice(0, 300));
    x = await sf({ query: "needle", path: path.join(P, "docs", "missing.txt") }, 17);
    check("S3 a missing path is a clear error, not '0 matches'", x.isError && /path not found|путь не найден/.test(x.text), x.text.slice(0, 300));

    const ping = await rpc(s, { jsonrpc: "2.0", id: 20, method: "ping", params: { pad: "z".repeat(1024 * 1024) } }, { token: T1, session });
    check("S3 an authenticated 1 MB MCP request is accepted (the large limit applies after auth)", ping.status === 200, ping.status + " " + ping.text.slice(0, 200));
    const st = await fetch(s.url + "/local/status", { headers: { "X-ZeroIDE-Control": s.control } });
    const stj = await st.json().catch(() => ({}));
    check("S3 /local/status reports the auth mode", st.status === 200 && !!stj.auth && stj.auth.failClosed === true && stj.auth.queryTokenAllowed === false && stj.auth.legacyToken === true, st.status + " " + JSON.stringify(stj.auth));

    const fl = [];
    for (let i = 0; i < 80; i++) {
      const y = await rpc(s, INIT, { token: "bad-" + i });
      fl.push(y.status);
      if (y.status === 429) break;
    }
    check("S3 repeated failed authentication is rate-limited (429)", fl.includes(429), "after " + fl.length + " attempts, last " + fl[fl.length - 1]);
    const after = await rpc(s, INIT, { token: T1 });
    check("S3 during the lockout the same address waits, even with a valid token (documented)", after.status === 429, after.status);

    const auditText = readIf(path.join(s.logs, "audit.jsonl"));
    check("S3 a refused ?token= is audited without its value", /auth\.query_token_rejected/.test(auditText) && !auditText.includes(T1), auditText.slice(-300));
    const leak = findSecret(s.dir, T1);
    check("S3 the token never appears in files or server output", !leak && !s.output().includes(T1), leak || "server output");
    await s.stop();
  }

  // S4: explicit, temporary opt-in for old clients.
  {
    const T1 = secret("legacy_");
    const s = await startServer("s4-query-optin", { env: { MCP_TOKEN: T1, MCP_ALLOW_QUERY_TOKEN: "1" } });
    const { r, session } = await openSession(s, null, { query: "?token=" + encodeURIComponent(T1) });
    check("S4 MCP_ALLOW_QUERY_TOKEN=1: ?token= works for old clients", r.status === 200 && !!session, r.status + " " + r.text.slice(0, 200));
    const wrongQ = await rpc(s, INIT, { query: "?token=nope" });
    check("S4 a wrong ?token= is still rejected", wrongQ.status === 401, wrongQ.status);
    check("S4 startup prints a deprecation warning", /MCP_ALLOW_QUERY_TOKEN=1/.test(s.output()), s.output().slice(-300));
    const auditText = readIf(path.join(s.logs, "audit.jsonl"));
    check("S4 query-token use is audited without the token", /auth\.query_token_used/.test(auditText) && !auditText.includes(T1), auditText.slice(-300));
    const leak = findSecret(s.dir, T1);
    check("S4 the token never appears in files or server output", !leak && !s.output().includes(T1), leak || "server output");
    await s.stop();
  }

  // S5: registry tokens only (no legacy token, OAuth off).
  {
    const good = secret("apt_");
    const revoked = secret("apt_");
    const expired = secret("apt_");
    const now = new Date().toISOString();
    const tokensFile = JSON.stringify({ version: 1, tokens: [
      { id: "tok_good", label: "good", hash: sha256(good), scope: "read", createdAt: now },
      { id: "tok_rev", label: "revoked", hash: sha256(revoked), scope: "read", createdAt: now, revoked: true },
      { id: "tok_exp", label: "expired", hash: sha256(expired), scope: "read", createdAt: now, expiresAt: "2020-01-01T00:00:00Z" },
    ] }, null, 2);
    const s = await startServer("s5-registry", { env: { MCP_OAUTH_ENABLED: "0" }, tokensFile });
    const ok = await rpc(s, INIT, { token: good });
    check("S5 an active registry token works without a legacy token", ok.status === 200, ok.status + " " + ok.text.slice(0, 200));
    const rv = await rpc(s, INIT, { token: revoked });
    check("S5 revoked token: 401 TOKEN_REVOKED", rv.status === 401 && rv.json && rv.json.code === "TOKEN_REVOKED", rv.text);
    const ex = await rpc(s, INIT, { token: expired });
    check("S5 expired token: 401 TOKEN_EXPIRED", ex.status === 401 && ex.json && ex.json.code === "TOKEN_EXPIRED", ex.text);
    const none = await rpc(s, INIT, {});
    check("S5 no token sent: 401 NO_TOKEN", none.status === 401 && none.json && none.json.code === "NO_TOKEN", none.text);
    await s.stop();
  }

  // S6: corrupt tokens.json and nothing else configured. Before the fix: open mode.
  {
    const s = await startServer("s6-corrupt-tokens", { env: { MCP_OAUTH_ENABLED: "0" }, tokensFile: "{ this is not json" });
    const r = await rpc(s, INIT, {});
    check("S6 corrupt tokens.json + nothing else: rejected (was open mode)", r.status === 401, r.status + " " + r.text.slice(0, 200));
    const r2 = await rpc(s, INIT, { token: "guess" });
    check("S6 corrupt tokens.json: a guessed token is rejected", r2.status === 401, r2.status);
    await s.stop();
  }

  // S7: corrupt tokens.json must not lock out the legacy token.
  {
    const T1 = secret("legacy_");
    const s = await startServer("s7-corrupt-tokens-legacy", { env: { MCP_TOKEN: T1, MCP_OAUTH_ENABLED: "0" }, tokensFile: "{ broken" });
    const r = await rpc(s, INIT, { token: T1 });
    check("S7 corrupt tokens.json does not lock out the legacy token", r.status === 200, r.status);
    await s.stop();
  }
}

function cleanup(keep) {
  for (const c of children) { try { c.kill(); } catch {} }
  for (const d of fs.readdirSync(base)) {
    const j = path.join(base, d, "node_modules");
    try { fs.lstatSync(j); } catch { continue; }
    try { fs.rmdirSync(j); } catch { try { fs.unlinkSync(j); } catch {} }
    let still = true;
    try { fs.lstatSync(j); } catch { still = false; }
    if (still) { console.log("note: temp folder kept (a junction could not be removed safely): " + base); return; }
  }
  if (keep) { console.log("note: temp folder kept for inspection: " + base); return; }
  try { fs.rmSync(base, { recursive: true, force: true }); } catch (e) { console.log("note: temp cleanup failed: " + (e.code || e.message)); }
}

main()
  .catch((e) => check("test harness", false, e && e.stack))
  .finally(async () => {
    await sleep(300);
    const failed = results.filter((r) => !r.ok);
    cleanup(failed.length > 0);
    clearTimeout(watchdog);
    console.log("\n" + (results.length - failed.length) + "/" + results.length + " checks passed");
    process.exit(failed.length ? 1 : 0);
  });
