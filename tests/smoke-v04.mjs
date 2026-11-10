#!/usr/bin/env node
/** v0.4 smoke: token registry (scope/limits/expiry/revoke/rotate) + two-level confinement. */
import fs from "node:fs";
import path from "node:path";

const BASE = process.env.TEST_BASE;
const ROOT = process.env.TEST_ROOT;
if (!BASE || !ROOT) { console.error("Run through tests/isolated-v04.mjs (needs TEST_BASE and TEST_ROOT)."); process.exit(2); }
const URL_ = ROOT + "/mcp";
const readJson = (p) => JSON.parse(fs.readFileSync(p, "utf8").replace(/^\uFEFF/, ""));
const TOKENS = readJson(path.join(BASE, "tokens-out.json"));
const PROJ = path.join(BASE, "project");
const JAIL = path.join(PROJ, "jail");
const LEGACY = "legacy-test-123";

let failures = 0;
function check(name, cond, extra = "") {
  if (cond) console.log("PASS: " + name);
  else { failures++; console.log("FAIL: " + name + (extra ? " -- " + extra : "")); }
}

const sessions = new Map();
let seq = 0;

async function rawPost(token, body, sessionId) {
  const headers = {
    "Content-Type": "application/json",
    Accept: "application/json, text/event-stream",
    "MCP-Protocol-Version": "2025-03-26",
    "User-Agent": "zeroide-mcp-smoke-v04/1.0",
  };
  if (token) headers["Authorization"] = "Bearer " + token;
  if (sessionId) headers["Mcp-Session-Id"] = sessionId;
  const res = await fetch(URL_, { method: "POST", headers, body: JSON.stringify(body) });
  const text = await res.text();
  return { res, text };
}

function parseRpc(text, id) {
  try { const j = JSON.parse(text); if (j && j.id === id) return j; } catch {}
  for (const block of text.split(/\r?\n\r?\n/)) {
    const line = block.split(/\r?\n/).find((l) => l.startsWith("data:"));
    if (!line) continue;
    try { const j = JSON.parse(line.slice(5).trim()); if (j && j.id === id) return j; } catch {}
  }
  return null;
}

async function client(token) {
  if (sessions.has(token)) return sessions.get(token);
  seq += 1;
  const { res, text } = await rawPost(token, { jsonrpc: "2.0", id: seq, method: "initialize", params: { protocolVersion: "2025-03-26", capabilities: {}, clientInfo: { name: "smoke-v04", version: "1.0" } } }, null);
  if (res.status !== 200) throw new Error("init failed HTTP " + res.status + " " + text.slice(0, 120));
  const sid = res.headers.get("mcp-session-id");
  await rawPost(token, { jsonrpc: "2.0", method: "notifications/initialized", params: {} }, sid);
  const c = { sid };
  sessions.set(token, c);
  return c;
}

async function call(token, name, args) {
  const c = await client(token);
  seq += 1;
  const { res, text } = await rawPost(token, { jsonrpc: "2.0", id: seq, method: "tools/call", params: { name, arguments: args } }, c.sid);
  if (res.status !== 200) return { httpError: res.status, text };
  const j = parseRpc(text, seq);
  if (!j) return { httpError: "parse", text: text.slice(0, 200) };
  const content = (j.result && j.result.content || []).map((x) => x.text || "").join("\n");
  return { isError: !!(j.result && j.result.isError), text: content, raw: j };
}

async function initExpect401(token, expectCode, label) {
  seq += 1;
  const { res, text } = await rawPost(token, { jsonrpc: "2.0", id: seq, method: "initialize", params: { protocolVersion: "2025-03-26", capabilities: {}, clientInfo: { name: "smoke-v04", version: "1.0" } } }, null);
  let code = null;
  try { code = JSON.parse(text).code; } catch {}
  check(label, res.status === 401 && code === expectCode, "got HTTP " + res.status + " code=" + code);
}

const T = (n) => TOKENS[n].token;

// ---------- Phase 1: global confinement OFF ----------
let r = await call(LEGACY, "list_directory", { path: JAIL });
check("legacy list jail", !r.isError && r.text.includes("inside.txt"), r.text.slice(0, 120));
r = await call(LEGACY, "run_command", { command: "echo legacy-ok" });
check("legacy run_command", !r.isError && r.text.includes("legacy-ok"), r.text.slice(0, 120));

r = await call(T("readjail"), "read_file", { path: "inside.txt" });
check("readjail relative path resolves to its root", !r.isError && r.text.includes("INSIDE-JAIL"), r.text.slice(0, 140));
r = await call(T("readjail"), "read_file", { path: path.join(JAIL, "inside.txt") });
check("readjail absolute inside", !r.isError && r.text.includes("INSIDE-JAIL"), r.text.slice(0, 140));
r = await call(T("readjail"), "read_file", { path: path.join(JAIL, "..", "outside", "secret.txt") });
check("readjail .. escape denied", r.isError && r.text.includes("PATH_OUTSIDE_ROOT"), r.text.slice(0, 140));
r = await call(T("readjail"), "read_file", { path: path.join(JAIL, "escape", "secret.txt") });
check("readjail junction escape denied", r.isError && r.text.includes("PATH_OUTSIDE_ROOT"), r.text.slice(0, 140));
r = await call(T("readjail"), "read_file", { path: path.join(PROJ, "jail2", "twin.txt") });
check("readjail prefix-twin denied", r.isError && r.text.includes("PATH_OUTSIDE_ROOT"), r.text.slice(0, 140));
r = await call(T("readjail"), "read_file", { path: "C:\\Windows\\win.ini" });
check("readjail absolute outside denied", r.isError && r.text.includes("PATH_OUTSIDE_ROOT"), r.text.slice(0, 140));
r = await call(T("readjail"), "read_file", { path: path.join(JAIL, "inside.txt") + ":stream" });
check("readjail ADS denied", r.isError && r.text.includes("PATH_OUTSIDE_ROOT"), r.text.slice(0, 140));
r = await call(T("readjail"), "write_file", { path: path.join(JAIL, "x.txt"), content: "x" });
check("readjail write blocked by scope", r.isError && r.text.includes("TOKEN_SCOPE_DENIED"), r.text.slice(0, 140));
r = await call(T("readjail"), "run_command", { command: "echo hi" });
check("readjail exec blocked by scope", r.isError && r.text.includes("TOKEN_SCOPE_DENIED"), r.text.slice(0, 140));

r = await call(T("fulljail"), "run_command", { command: "echo hi" });
check("fulljail exec blocked by confinement", r.isError && r.text.includes("TOOL_DISABLED_BY_CONFINEMENT"), r.text.slice(0, 140));
r = await call(T("fulljail"), "write_file", { path: path.join(JAIL, "new.txt"), content: "made-by-fulljail" });
check("fulljail write inside ok", !r.isError, r.text.slice(0, 140));
check("fulljail file landed", fs.existsSync(path.join(JAIL, "new.txt")) && fs.readFileSync(path.join(JAIL, "new.txt"), "utf8").includes("made-by-fulljail"));

r = await call(T("plainfull"), "run_command", { command: "echo plain-ok" });
check("plainfull exec ok", !r.isError && r.text.includes("plain-ok"), r.text.slice(0, 120));
r = await call(T("plainfull"), "read_file", { path: path.join(PROJ, "outside", "secret.txt") });
check("plainfull reads outside (no jail)", !r.isError && r.text.includes("TOP-SECRET"), r.text.slice(0, 120));

r = await call(T("limited"), "read_file", { path: path.join(JAIL, "inside.txt") });
check("limited call 1 ok", !r.isError, r.text.slice(0, 120));
r = await call(T("limited"), "read_file", { path: path.join(JAIL, "inside.txt") });
check("limited call 2 ok", !r.isError, r.text.slice(0, 120));
r = await call(T("limited"), "read_file", { path: path.join(JAIL, "inside.txt") });
check("limited call 3 exhausted", r.isError && r.text.includes("TOKEN_EXHAUSTED"), r.text.slice(0, 140));

await initExpect401("apw_unknown_garbage_token", "UNKNOWN_TOKEN", "unknown token -> 401 UNKNOWN_TOKEN");
await initExpect401(T("expired"), "TOKEN_EXPIRED", "expired token -> 401 TOKEN_EXPIRED");
await initExpect401("", "NO_TOKEN", "no token -> 401 NO_TOKEN");

// ---------- Phase 2: global confinement ON (root = jail) ----------
const permFile = path.join(BASE, "cfg", "permissions.json");
const perms = readJson(permFile);
perms.confinement = { enabled: true, root: JAIL };
fs.writeFileSync(permFile, JSON.stringify(perms, null, 2), "utf8");
await new Promise((res) => setTimeout(res, 300));

sessions.delete(LEGACY);
r = await call(LEGACY, "read_file", { path: path.join(PROJ, "outside", "secret.txt") });
check("global jail: legacy outside denied", r.isError && r.text.includes("PATH_OUTSIDE_ROOT"), r.text.slice(0, 140));
r = await call(LEGACY, "run_command", { command: "echo hi" });
check("global jail: legacy exec disabled", r.isError && r.text.includes("TOOL_DISABLED_BY_CONFINEMENT"), r.text.slice(0, 140));
sessions.delete(T("plainfull"));
r = await call(T("plainfull"), "read_file", { path: path.join(JAIL, "inside.txt") });
check("global jail: plainfull inherits root", !r.isError && r.text.includes("INSIDE-JAIL"), r.text.slice(0, 140));
sessions.delete(T("readjail"));
r = await call(T("readjail"), "read_file", { path: "inside.txt" });
check("global+token jail: readjail still works", !r.isError && r.text.includes("INSIDE-JAIL"), r.text.slice(0, 140));

console.log(failures === 0 ? "SMOKE-V04: ALL PASS" : "SMOKE-V04: " + failures + " FAILURES");
process.exit(failures === 0 ? 0 : 1);
