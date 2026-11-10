// Isolated runner for the older v0.4 server suites:
//   smoke-v04.mjs        token registry (scope, limits, expiry) and two-level confinement
//   phase3-v04.mjs       token rotation and revocation through server/tokens-cli.mjs
//   dcr-policy-test.mjs  client registration policy and the unknown-site warning
//   oauth-test.mjs       OAuth consent, PKCE, refresh rotation, reuse detection, revoke
// Every suite gets its own temp folder with fixtures, free ports, a fake tray and a
// clean environment, so a run never reaches an installed copy or a fixed port.
// Run: node tests/isolated-v04.mjs   (needs server/node_modules: npm ci --omit=dev)
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

const LEGACY = "legacy-test-123"; // the legacy MCP_TOKEN that smoke-v04.mjs expects
const base = fs.mkdtempSync(path.join(os.tmpdir(), "zeroide-iso-"));
const children = [];
const trays = [];
const junctions = [];
const watchdog = setTimeout(() => {
  console.error("TIMEOUT: the isolated run took longer than 4 minutes");
  for (const c of children) { try { c.kill(); } catch {} }
  process.exit(3);
}, 240000);

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const sha256 = (s) => crypto.createHash("sha256").update(s, "utf8").digest("hex");
const randomToken = () => "apw_" + crypto.randomBytes(24).toString("base64url");

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

function junction(target, link) {
  fs.symlinkSync(target, link, "junction");
  junctions.push(link);
}

function prepare(name) {
  const dir = path.join(base, name);
  const project = path.join(dir, "project");
  const jail = path.join(project, "jail");
  const outside = path.join(project, "outside");
  for (const d of [path.join(jail, "sub"), path.join(project, "jail2"), outside, path.join(dir, "cfg"), path.join(dir, "logs")]) fs.mkdirSync(d, { recursive: true });
  fs.writeFileSync(path.join(jail, "inside.txt"), "INSIDE-JAIL\n");
  fs.writeFileSync(path.join(jail, "sub", "nested.txt"), "NESTED\n");
  fs.writeFileSync(path.join(project, "jail2", "twin.txt"), "TWIN\n");
  fs.writeFileSync(path.join(outside, "secret.txt"), "TOP-SECRET\n");
  junction(outside, path.join(jail, "escape")); // leads out of the jail
  const tools = {};
  for (const t of ["journal_log", "job_status", "audit_log", "read_file", "journal_entry", "list_directory", "search_files", "run_command", "write_file", "edit_file", "job_output"]) tools[t] = { mode: "allow" };
  const permissions = { version: 1, defaultMode: "allow", approvalTimeoutSec: 60, redFlags: { enabled: true, elevateToAsk: true, patterns: [] }, tools, confinement: { enabled: false, root: null } };
  fs.writeFileSync(path.join(dir, "cfg", "permissions.json"), JSON.stringify(permissions, null, 2));
  const now = new Date().toISOString();
  const specs = {
    readjail: { scope: "read", root: jail },
    fulljail: { scope: "full", root: jail },
    plainfull: { scope: "full" },
    limited: { scope: "read", maxRequests: 2 },
    expired: { scope: "read", expiresAt: "2020-01-01T00:00:00.000Z" },
  };
  const plain = {};
  const tokens = [];
  for (const [label, s] of Object.entries(specs)) {
    const token = randomToken();
    const id = "tok_" + crypto.randomBytes(4).toString("hex");
    plain[label] = { id, token };
    tokens.push({ id, label, hash: sha256(token), scope: s.scope, root: s.root || null, source: "manual", createdAt: now, expiresAt: s.expiresAt || null, maxRequests: s.maxRequests || null, usedRequests: 0, revoked: false, rotatedFrom: null });
  }
  fs.writeFileSync(path.join(dir, "cfg", "tokens.json"), JSON.stringify({ version: 1, tokens }, null, 2));
  fs.writeFileSync(path.join(dir, "tokens-out.json"), JSON.stringify(plain, null, 2));
  fs.copyFileSync(serverSrc, path.join(dir, "server.mjs"));
  junction(deps, path.join(dir, "node_modules"));
  return dir;
}

// Fake tray. The mode file decides approval answers: "allow" / "deny" for OAuth
// consent, "hold" keeps every approval request open (the card stays waiting).
// Other approvals are always denied as a timeout.
function startFakeTray(port, modeFile) {
  const held = [];
  const srv = http.createServer((req, res) => {
    const chunks = [];
    req.on("data", (d) => chunks.push(d));
    req.on("end", () => {
      if (!req.url.startsWith("/approval/request")) {
        res.writeHead(200, { "Content-Type": "application/json" });
        res.end('{"ok":true}');
        return;
      }
      let payload = {};
      try { payload = JSON.parse(Buffer.concat(chunks).toString("utf8")); } catch {}
      let mode = "deny";
      try { mode = fs.readFileSync(modeFile, "utf8").trim(); } catch {}
      if (mode === "hold") { held.push(res); return; }
      const out = payload.kind === "oauth_consent"
        ? (mode === "allow" ? { decision: "allow", lifetimeDays: 30 } : { decision: "deny", by: "user" })
        : { decision: "deny", by: "timeout" };
      const s = JSON.stringify(out);
      res.writeHead(200, { "Content-Type": "application/json", "Content-Length": String(Buffer.byteLength(s)) });
      res.end(s);
    });
  });
  return new Promise((resolve) => srv.listen(port, "127.0.0.1", () => {
    trays.push({
      close: () => new Promise((r) => {
        for (const h of held) { try { if (h.socket) h.socket.destroy(); } catch {} }
        if (srv.closeAllConnections) srv.closeAllConnections();
        srv.close(() => r());
      }),
    });
    resolve();
  }));
}

async function startServer(dir, lang, mode) {
  const port = await freePort();
  const trayPort = await freePort();
  const modeFile = path.join(dir, "tray-mode.txt");
  fs.writeFileSync(modeFile, mode);
  await startFakeTray(trayPort, modeFile);
  const env = cleanEnv({
    PORT: String(port),
    MCP_HOST: "127.0.0.1",
    MCP_CONFIG_FILE: path.join(dir, "config.env"),
    MCP_LOG_DIR: path.join(dir, "logs"),
    MCP_PERMISSIONS_FILE: path.join(dir, "cfg", "permissions.json"),
    MCP_TOKENS_FILE: path.join(dir, "cfg", "tokens.json"),
    MCP_OAUTH_FILE: path.join(dir, "cfg", "oauth.json"),
    MCP_TOKEN: LEGACY,
    PROJECT_DIR: path.join(dir, "project"),
    TRAY_CONTROL_PORT: String(trayPort),
    TRAY_CONTROL_SECRET: "ctl_" + crypto.randomBytes(18).toString("hex"),
    TRAY_HEARTBEAT_SEC: "120",
    TRAY_HEARTBEAT_MISSES: "100",
    MCP_LANG: lang,
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
  if (!up) throw new Error("server did not start. Output: " + out.slice(-800));
  return { url, modeFile, child };
}

function runScript(file, env) {
  return new Promise((resolve) => {
    const child = spawn(process.execPath, [path.join(here, file)], { cwd: repo, env: cleanEnv(env), stdio: ["ignore", "pipe", "pipe"], windowsHide: true });
    children.push(child);
    let out = "";
    child.stdout.on("data", (d) => { out += d; });
    child.stderr.on("data", (d) => { out += d; });
    child.on("close", (code) => resolve({ code, out }));
  });
}

const suites = [
  { name: "tokens-and-confinement", lang: "en", mode: "deny", scripts: ["smoke-v04.mjs", "phase3-v04.mjs"] },
  { name: "dcr-policy", lang: "ru", mode: "hold", scripts: ["dcr-policy-test.mjs"] },
  { name: "oauth-flow", lang: "en", mode: "allow", scripts: ["oauth-test.mjs"] },
];

let failed = 0;
let passedChecks = 0;
try {
  for (const s of suites) {
    const dir = prepare(s.name);
    const srv = await startServer(dir, s.lang, s.mode);
    for (const script of s.scripts) {
      const r = await runScript(script, { TEST_BASE: dir, TEST_ROOT: srv.url, TEST_TRAY_MODE_FILE: srv.modeFile });
      for (const line of r.out.split(/\r?\n/)) {
        if (!line.trim()) continue;
        if (line.startsWith("PASS")) passedChecks++;
        console.log("  [" + script + "] " + line);
      }
      console.log((r.code === 0 ? "PASS " : "FAIL ") + s.name + " / " + script + " (exit " + r.code + ")");
      if (r.code !== 0) failed++;
    }
    try { srv.child.kill(); } catch {}
    await sleep(250);
  }
} catch (e) {
  failed++;
  console.log("FAIL runner: " + ((e && e.message) || e));
} finally {
  for (const c of children) { try { c.kill(); } catch {} }
  for (const t of trays) { try { await t.close(); } catch {} }
  await sleep(300);
  for (const j of junctions) { try { fs.unlinkSync(j); } catch {} }
  const leftover = junctions.filter((j) => { try { fs.lstatSync(j); return true; } catch { return false; } });
  if (!failed && !leftover.length) { try { fs.rmSync(base, { recursive: true, force: true }); } catch {} }
  else console.log("Temp data kept for inspection: " + base);
}
clearTimeout(watchdog);
console.log(failed === 0 ? "ISOLATED-V04: ALL PASS (" + passedChecks + " checks)" : "ISOLATED-V04: " + failed + " FAILED SCRIPT(S), " + passedChecks + " checks passed");
process.exit(failed ? 1 : 0);