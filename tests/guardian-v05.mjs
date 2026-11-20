// v0.5 "guardian" regression tests for server/server.mjs.
// Self-protection of ZeroIDE secrets/state, the risk gate (allow -> ask, critical calls
// never covered by grants), change previews on the approval card, undo history instead
// of .bak files, edit_file edits[]/replaceAll, file_ops, read_image, system_info and
// user_message open links.
// Fully isolated like security-v04: temp copy of the server, fresh data, free ports,
// a fake tray that records every card and answers from a script, clean environment.
// Run: node tests/guardian-v05.mjs   (needs server/node_modules: npm ci --omit=dev)
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

const base = fs.mkdtempSync(path.join(os.tmpdir(), "zeroide-v05-"));
const children = [];
const results = [];
const watchdog = setTimeout(() => {
  console.error("TIMEOUT: the test run took longer than 4 minutes");
  for (const c of children) { try { c.kill(); } catch {} }
  process.exit(3);
}, 240000);

function check(name, ok, detail) {
  results.push({ name, ok: !!ok });
  console.log((ok ? "PASS " : "FAIL ") + name + (ok || detail === undefined ? "" : "  :: " + String(detail).slice(0, 500)));
}
const secret = (prefix) => prefix + crypto.randomBytes(18).toString("hex");
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const readIf = (p) => { try { return fs.readFileSync(p, "utf8"); } catch { return ""; } };
const jsonl = (p) => readIf(p).split(/\r?\n/).filter(Boolean).map((l) => { try { return JSON.parse(l); } catch { return null; } }).filter(Boolean);

function freePort() {
  return new Promise((resolve, reject) => {
    const s = net.createServer();
    s.on("error", reject);
    s.listen(0, "127.0.0.1", () => { const { port } = s.address(); s.close(() => resolve(port)); });
  });
}

function cleanEnv(extra) {
  const keep = ["SystemRoot", "windir", "PATH", "PATHEXT", "TEMP", "TMP", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "ComSpec", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "OS", "SystemDrive", "ProgramData", "ProgramFiles", "HOME", "LANG", "PSModulePath"];
  const env = {};
  for (const k of keep) if (process.env[k] !== undefined) env[k] = process.env[k];
  return Object.assign(env, extra);
}

// Fake tray: records approval cards and messages; decide(card) scripts the owner.
function startFakeTray(port) {
  const tray = { approvals: [], messages: [], decide: () => ({ decision: "allow_once" }) };
  const srv = http.createServer((req, res) => {
    const chunks = [];
    req.on("data", (d) => chunks.push(d));
    req.on("end", () => {
      let body = {};
      try { body = JSON.parse(Buffer.concat(chunks).toString("utf8") || "{}"); } catch {}
      res.writeHead(200, { "Content-Type": "application/json" });
      if (req.url.startsWith("/approval/request")) {
        tray.approvals.push(body);
        res.end(JSON.stringify(tray.decide(body)));
        return;
      }
      if (req.url.startsWith("/message/show")) tray.messages.push(body);
      res.end('{"ok":true}');
    });
  });
  return new Promise((resolve) => srv.listen(port, "127.0.0.1", () => resolve(Object.assign(tray, {
    close: () => new Promise((r) => { if (srv.closeAllConnections) srv.closeAllConnections(); srv.close(() => r()); }),
  }))));
}

const ALLOW_ALL = { run_command: { mode: "allow" }, write_file: { mode: "allow" }, edit_file: { mode: "allow" }, file_ops: { mode: "allow" }, read_file: { mode: "allow" }, read_image: { mode: "allow" } };

async function startServer(name, opts = {}) {
  const dir = path.join(base, name);
  const project = path.join(dir, "project");
  const logs = path.join(dir, "logs");
  fs.mkdirSync(project, { recursive: true });
  fs.mkdirSync(logs, { recursive: true });
  fs.copyFileSync(serverSrc, path.join(dir, "server.mjs"));
  fs.symlinkSync(deps, path.join(dir, "node_modules"), "junction");
  const token = secret("tok_");
  fs.writeFileSync(path.join(dir, "config.env"), "MCP_TOKEN=" + token + "\r\n", "utf8");
  if (opts.permissions) fs.writeFileSync(path.join(dir, "permissions.json"), JSON.stringify(opts.permissions, null, 2), "utf8");
  const port = await freePort();
  const trayPort = await freePort();
  const tray = await startFakeTray(trayPort);
  const env = cleanEnv({
    PORT: String(port),
    MCP_HOST: "127.0.0.1",
    MCP_CONFIG_FILE: path.join(dir, "config.env"),
    MCP_LOG_DIR: logs,
    MCP_PERMISSIONS_FILE: path.join(dir, "permissions.json"),
    MCP_TOKENS_FILE: path.join(dir, "tokens.json"),
    MCP_OAUTH_FILE: path.join(dir, "oauth.json"),
    MCP_OAUTH_ENABLED: "0",
    PROJECT_DIR: project,
    TRAY_CONTROL_PORT: String(trayPort),
    TRAY_CONTROL_SECRET: secret("ctl_"),
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
  const srv = { dir, url, project, logs, token, tray, history: path.join(dir, "history"), output: () => out };
  srv.stop = async () => { try { child.kill(); } catch {} await sleep(250); await tray.close(); };
  const init = await rpc(srv, { jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "2025-03-26", capabilities: {}, clientInfo: { name: "guardian-v05", version: "1" } } });
  srv.session = init.headers.get("mcp-session-id");
  if (init.status !== 200 || !srv.session) throw new Error(name + ": initialize failed " + init.status + " " + init.text.slice(0, 300));
  await rpc(srv, { jsonrpc: "2.0", method: "notifications/initialized" });
  srv.initResult = init.json && init.json.result;
  let nextId = 10;
  srv.call = async (tool, args) => {
    const r = await rpc(srv, { jsonrpc: "2.0", id: nextId++, method: "tools/call", params: { name: tool, arguments: args || {} } });
    const res = r.json && r.json.result;
    const content = (res && Array.isArray(res.content)) ? res.content : [];
    return { status: r.status, isError: !!(res && res.isError), content, text: content.length ? content.map((c) => c.text || "").join("\n") : r.text };
  };
  srv.list = async () => {
    const r = await rpc(srv, { jsonrpc: "2.0", id: nextId++, method: "tools/list", params: {} });
    return (r.json && r.json.result && r.json.result.tools) || [];
  };
  return srv;
}

async function rpc(srv, body) {
  const headers = { "Content-Type": "application/json", Accept: "application/json, text/event-stream", Authorization: "Bearer " + srv.token };
  if (srv.session) headers["mcp-session-id"] = srv.session;
  const r = await fetch(srv.url + "/mcp", { method: "POST", headers, body: JSON.stringify(body) });
  const text = await r.text();
  let json = null;
  try {
    if ((r.headers.get("content-type") || "").includes("text/event-stream")) {
      const data = text.split(/\r?\n/).filter((l) => l.startsWith("data:")).map((l) => l.slice(5).trim()).filter(Boolean);
      json = data.length ? JSON.parse(data[data.length - 1]) : null;
    } else if (text) json = JSON.parse(text);
  } catch {}
  return { status: r.status, headers: r.headers, text, json };
}

const PNG_1X1 = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==", "base64");
function makeBmp(w, h) {
  const row = Math.ceil((w * 3) / 4) * 4;
  const size = 54 + row * h;
  const b = Buffer.alloc(size);
  b.write("BM", 0, "ascii"); b.writeUInt32LE(size, 2); b.writeUInt32LE(54, 10);
  b.writeUInt32LE(40, 14); b.writeInt32LE(w, 18); b.writeInt32LE(h, 22); b.writeUInt16LE(1, 26); b.writeUInt16LE(24, 28);
  b.writeUInt32LE(row * h, 34);
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) { const o = 54 + y * row + x * 3; b[o] = x % 256; b[o + 1] = 120; b[o + 2] = 200; }
  return b;
}

async function main() {
  // ---------------------------------------------------------------- G1 self-protection
  {
    const s = await startServer("g1-self", { permissions: { version: 1, tools: { write_file: { mode: "allow" }, file_ops: { mode: "allow" } } } });
    const cfg = await s.call("read_file", { path: path.join(s.dir, "config.env") });
    check("G1 read_file config.env is refused (PROTECTED_PATH)", cfg.isError && /PROTECTED_PATH/.test(cfg.text), cfg.text);
    check("G1 the refusal never contains the token", !cfg.text.includes(s.token));
    const tok = await s.call("read_file", { path: path.join(s.dir, "tokens.json") });
    check("G1 read_file tokens.json is refused", tok.isError && /PROTECTED_PATH/.test(tok.text), tok.text);
    const tgPath = path.join(s.dir, "telegram.json");
    fs.writeFileSync(tgPath, JSON.stringify({ enabled: true, botToken: "123456789:AAFakeBotTokenForTest1234567890", chatId: "999999" }));
    const tg = await s.call("read_file", { path: tgPath });
    check("G1 read_file telegram.json is refused (bot token protected)", tg.isError && /PROTECTED_PATH/.test(tg.text) && !tg.text.includes("AAFakeBotToken"), tg.text);
    const rel = await s.call("read_file", { path: "..\\config.env" });
    check("G1 a relative path to config.env is refused too", rel.isError && /PROTECTED_PATH/.test(rel.text), rel.text);
    const grep = await s.call("search_files", { query: s.token, path: s.dir });
    check("G1 search_files never finds the token inside protected files", !grep.isError && /matches_total: 0/.test(grep.text) && !grep.text.includes("config.env:"), grep.text);
    const perms = await s.call("read_file", { path: path.join(s.dir, "permissions.json") });
    check("G1 permissions.json stays readable", !perms.isError && /"tools"/.test(perms.text), perms.text);
    const wperm = await s.call("write_file", { path: path.join(s.dir, "permissions.json"), content: '{"defaultMode":"allow"}' });
    check("G1 write_file permissions.json is refused (agents cannot loosen their own rules)", wperm.isError && /PROTECTED_PATH/.test(wperm.text), wperm.text);
    const wlog = await s.call("write_file", { path: path.join(s.logs, "audit.jsonl"), content: "" });
    check("G1 wiping the audit log through write_file is refused", wlog.isError && /PROTECTED_PATH/.test(wlog.text), wlog.text);
    const del = await s.call("file_ops", { op: "delete", path: path.join(s.logs, "audit.jsonl") });
    check("G1 file_ops cannot delete the audit log", del.isError && /PROTECTED_PATH/.test(del.text), del.text);
    const cp = await s.call("file_ops", { op: "copy", path: path.join(s.dir, "config.env"), to: path.join(s.project, "leak.txt") });
    check("G1 file_ops cannot copy config.env out", cp.isError && /PROTECTED_PATH/.test(cp.text) && !fs.existsSync(path.join(s.project, "leak.txt")), cp.text);
    const ls = await s.call("list_directory", { path: s.dir });
    check("G1 list_directory of the server folder still works", !ls.isError && /config\.env/.test(ls.text), ls.text.slice(0, 200));
    check("G1 config.env and permissions.json are untouched", readIf(path.join(s.dir, "config.env")).includes(s.token) && !readIf(path.join(s.dir, "permissions.json")).includes('"defaultMode":"allow"}'));
    const audit = jsonl(path.join(s.logs, "audit.jsonl"));
    check("G1 refusals are audited as self.protect", audit.filter((e) => e.event === "self.protect").length >= 6, audit.map((e) => e.event || e.tool).join(","));
    check("G1 the audit never stores the token", !readIf(path.join(s.logs, "audit.jsonl")).includes(s.token));
    await s.stop();
  }
  {
    const s = await startServer("g1b-self-optout", { env: { MCP_ALLOW_SELF_ACCESS: "1" } });
    const cfg = await s.call("read_file", { path: path.join(s.dir, "config.env") });
    check("G1b MCP_ALLOW_SELF_ACCESS=1 lifts the rule for owners who want it", !cfg.isError && cfg.text.includes("MCP_TOKEN="), cfg.text.slice(0, 200));
    await s.stop();
  }

  // ---------------------------------------------------------------- G2 risk gate
  {
    const s = await startServer("g2-risk", { permissions: { version: 1, approvalTimeoutSec: 30, tools: { ...ALLOW_ALL } } });
    const tray = s.tray;
    const low = await s.call("run_command", { command: "Write-Output guardian-low", note: "echo" });
    check("G2 an allowed low-risk command runs without a card", !low.isError && /guardian-low/.test(low.text) && tray.approvals.length === 0, low.text.slice(0, 300));

    const high = await s.call("run_command", { command: "Write-Output 'schtasks /create /tn demo /tr notepad'", note: "risk probe" });
    const c1 = tray.approvals[0] || {};
    check("G2 an allowed HIGH-risk command is escalated to a card", tray.approvals.length === 1 && !high.isError, JSON.stringify(c1).slice(0, 300) + " | " + high.text.slice(0, 200));
    check("G2 the card carries the assessment (level, headline, reasons, escalated=rule)", c1.assessment && c1.assessment.level === "high" && c1.assessment.escalated === "rule" && Array.isArray(c1.assessment.reasons) && c1.assessment.reasons.length >= 1 && /High risk/.test(c1.assessment.headline || ""), JSON.stringify(c1.assessment));
    check("G2 a high-risk card still offers a grant", c1.assessment && c1.assessment.grantable === true);

    // The owner answers a CRITICAL card with a 10-minute tool grant: it becomes allow_once.
    tray.decide = () => ({ decision: "allow_grant", grantMinutes: 10, grantScope: "tool" });
    const crit = await s.call("run_command", { command: "Write-Output 'Format-Volume -DriveLetter Z'", note: "critical probe" });
    const c2 = tray.approvals[1] || {};
    check("G2 a critical card says critical and is not grantable", c2.assessment && c2.assessment.level === "critical" && c2.assessment.grantable === false, JSON.stringify(c2.assessment));
    check("G2 the critical command ran once after the click", !crit.isError && /Format-Volume/.test(crit.text), crit.text.slice(0, 200));
    const audit1 = jsonl(path.join(s.logs, "audit.jsonl"));
    check("G2 the grant on a critical card was refused (audited)", audit1.some((e) => e.event === "risk.grant_refused") && !audit1.some((e) => e.event === "approval.grant"), audit1.map((e) => e.event).filter(Boolean).join(","));
    tray.decide = () => ({ decision: "allow_once" });
    await s.call("run_command", { command: "Write-Output 'schtasks /create /tn demo2'", note: "after refused grant" });
    check("G2 no grant was stored, so the next high-risk call asks again", tray.approvals.length === 3, tray.approvals.length);

    // A real tool grant from a high card does not cover a later critical call.
    tray.decide = () => ({ decision: "allow_grant", grantMinutes: 10, grantScope: "tool" });
    await s.call("run_command", { command: "Write-Output 'schtasks /create /tn demo3'", note: "grant" });
    const before = tray.approvals.length;
    await s.call("run_command", { command: "Write-Output 'schtasks /create /tn demo4'", note: "covered by grant" });
    check("G2 the tool grant covers the next high-risk call", tray.approvals.length === before, tray.approvals.length + " vs " + before);
    tray.decide = () => ({ decision: "deny", by: "user" });
    const marker = path.join(s.project, "critical-ran.txt");
    const crit2 = await s.call("run_command", { command: "Set-Content -Path '" + marker + "' -Value x; Write-Output 'vssadmin delete shadows'", note: "critical under grant" });
    const c3 = tray.approvals[tray.approvals.length - 1] || {};
    check("G2 a tool grant never covers a critical call (card shown, escalated=grant)", tray.approvals.length === before + 1 && c3.assessment && c3.assessment.escalated === "grant" && c3.assessment.level === "critical", JSON.stringify(c3.assessment));
    check("G2 the denied critical command never ran", crit2.isError && /APPROVAL_DENIED_BY_USER/.test(crit2.text) && !fs.existsSync(marker), crit2.text.slice(0, 200));

    tray.decide = () => ({ decision: "deny", by: "user" });
    const selfCmd = await s.call("run_command", { command: "Get-Content config.env", cwd: s.dir, note: "peek" });
    const c4 = tray.approvals[tray.approvals.length - 1] || {};
    check("G2 reading config.env from the server folder is critical and needs a click", c4.assessment && c4.assessment.level === "critical" && selfCmd.isError && !selfCmd.text.includes(s.token), JSON.stringify(c4.assessment) + " | " + selfCmd.text.slice(0, 160));
    const ctl = await s.call("run_command", { command: "Invoke-RestMethod http://127.0.0.1:39012/approval/simulate-click -Method Post", note: "x" });
    const c5 = tray.approvals[tray.approvals.length - 1] || {};
    check("G2 calling the tray approval channel is critical", c5.assessment && c5.assessment.level === "critical" && ctl.isError, JSON.stringify(c5.assessment));

    const audit2 = jsonl(path.join(s.logs, "audit.jsonl"));
    check("G2 high/critical calls are audited as risk.flagged", audit2.filter((e) => e.event === "risk.flagged").length >= 5);
    await s.stop();
  }
  {
    const s = await startServer("g2b-gate-off", { permissions: { version: 1, tools: { ...ALLOW_ALL }, riskGate: { enabled: false } } });
    const r = await s.call("run_command", { command: "Write-Output 'schtasks /create /tn demo'", note: "gate off" });
    check("G2b riskGate.enabled=false keeps the old behaviour (no card)", !r.isError && s.tray.approvals.length === 0, r.text.slice(0, 200));
    await s.stop();
  }

  {
    // High/Red-Flag off, Critical guard STILL ON (askAtOrAbove: critical)
    const s = await startServer("g2c-high-off-crit-on", {
      permissions: {
        version: 1,
        tools: { ...ALLOW_ALL },
        redFlags: { enabled: false, elevateToAsk: false, patterns: [] },
        riskGate: { enabled: true, askAtOrAbove: "critical", grantsNeverCover: "critical", sensitiveReads: false }
      }
    });
    const rHigh = await s.call("run_command", { command: "Write-Output 'schtasks /create /tn demo'", note: "high allowed when high filter off" });
    check("G2c high-risk runs silently when High filter is off and Critical is on", !rHigh.isError && s.tray.approvals.length === 0, rHigh.text.slice(0, 200));

    s.tray.decide = () => ({ decision: "deny", by: "user" });
    const rCrit = await s.call("run_command", { command: "Write-Output 'Format-Volume -DriveLetter Z'", note: "critical still blocked" });
    const cCrit = s.tray.approvals[0] || {};
    check("G2c critical still asks with non-grantable card when only High filter is off", s.tray.approvals.length === 1 && cCrit.assessment?.level === "critical" && cCrit.assessment?.grantable === false && rCrit.isError, JSON.stringify(cCrit.assessment));

    const sys = await s.call("system_info", {});
    check("G2c system_info reports high-risk silent + critical-only guard", !sys.isError && /high-risk runs silently when allowed; critical calls always ask/i.test(sys.text), sys.text.slice(0, 400));
    await s.stop();
  }

  {
    // Full autonomy: BOTH High/Red-Flag and Critical disabled (self-protection still blocks protected paths)
    const s = await startServer("g2d-full-autonomy", {
      permissions: {
        version: 1,
        tools: { ...ALLOW_ALL },
        redFlags: { enabled: false, elevateToAsk: false, patterns: [] },
        riskGate: { enabled: false, askAtOrAbove: "none", grantsNeverCover: "none", sensitiveReads: false }
      }
    });
    const rCrit = await s.call("run_command", { command: "Write-Output 'Format-Volume -DriveLetter Z'", note: "full autonomy allows critical" });
    check("G2d full autonomy runs critical command with zero cards when tool is allowed", !rCrit.isError && s.tray.approvals.length === 0 && /Format-Volume/.test(rCrit.text), rCrit.text.slice(0, 200));

    const rSelf = await s.call("read_file", { path: path.join(s.dir, "config.env") });
    check("G2d self-protection (PROTECTED_PATH) stays active even in full autonomy mode", rSelf.isError && /PROTECTED_PATH/.test(rSelf.text), rSelf.text.slice(0, 200));

    const sys = await s.call("system_info", {});
    check("G2d system_info reports risk check off (full autonomy)", !sys.isError && /off \(full autonomy/i.test(sys.text), sys.text.slice(0, 400));
    await s.stop();
  }

  // ------------------------------------------- G3 previews, history, edits[], replaceAll
  {
    const s = await startServer("g3-edits", { permissions: { version: 1, tools: { write_file: { mode: "ask" }, edit_file: { mode: "ask" } } } });
    const tray = s.tray;
    const f = path.join(s.project, "notes.txt");
    const w = await s.call("write_file", { path: f, content: "one\ntwo\nthree\n", note: "create" });
    const card = tray.approvals[0] || {};
    check("G3 write_file card has a preview of a new file (+3/-0)", card.preview && card.preview.exists === false && card.preview.added === 3 && card.preview.removed === 0 && /\+one/.test(card.preview.text || ""), JSON.stringify(card.preview));
    check("G3 write_file result reports a change_id", !w.isError && /change_id: chg_/.test(w.text), w.text);
    check("G3 cards name the agent slot", typeof card.agent === "string" && card.agent.length > 0, JSON.stringify(card.agent));

    const e = await s.call("edit_file", { path: f, edits: [{ oldString: "one", newString: "ONE" }, { oldString: "three", newString: "THREE" }], note: "two edits" });
    const card2 = tray.approvals[1] || {};
    check("G3 edits[] applies all replacements in one approved change", !e.isError && readIf(f) === "ONE\ntwo\nTHREE\n" && tray.approvals.length === 2, e.text + " | " + JSON.stringify(readIf(f)));
    check("G3 the edit card previews +2/-2", card2.preview && card2.preview.added === 2 && card2.preview.removed === 2, JSON.stringify(card2.preview));

    const bad = await s.call("edit_file", { path: f, edits: [{ oldString: "two", newString: "2" }, { oldString: "missing", newString: "x" }], note: "bad" });
    check("G3 edits[] is all-or-nothing: a missing fragment changes nothing", bad.isError && /edit #2 of 2/.test(bad.text) && readIf(f) === "ONE\ntwo\nTHREE\n", bad.text);
    check("G3 an edit that cannot apply is answered without bothering the owner", tray.approvals.length === 2, tray.approvals.length);

    const g = path.join(s.project, "repeat.txt");
    fs.writeFileSync(g, "x x x", "utf8");
    const amb = await s.call("edit_file", { path: g, oldString: "x", newString: "y" });
    check("G3 an ambiguous oldString is refused", amb.isError && /3 times/.test(amb.text), amb.text);
    const all = await s.call("edit_file", { path: g, oldString: "x", newString: "y", replaceAll: true });
    check("G3 replaceAll replaces every occurrence", !all.isError && readIf(g) === "y y y", all.text);

    const bakFiles = fs.readdirSync(s.project).filter((n) => /\.bak/.test(n));
    check("G3 no .bak files are left next to project files", bakFiles.length === 0, bakFiles.join(","));
    const idx = jsonl(path.join(s.history, "index.jsonl"));
    const created = idx.find((x) => x.op === "create" && x.path === f);
    const modified = idx.find((x) => x.op === "modify" && x.path === f && x.tool === "edit_file");
    check("G3 history/index.jsonl records create and modify", created && modified && modified.edits === 2, JSON.stringify(idx).slice(0, 400));
    check("G3 the modify entry keeps the previous bytes for undo", modified && modified.backup && readIf(modified.backup) === "one\ntwo\nthree\n", modified && modified.backup);
    check("G3 history entries carry agent and note", modified && modified.note === "two edits" && typeof modified.agent === "string", JSON.stringify(modified));
    const nob = await s.call("write_file", { path: g, content: "z", backup: false });
    const nobEntry = jsonl(path.join(s.history, "index.jsonl")).filter((x) => x.path === g && x.tool === "write_file").pop();
    check("G3 backup=false records the change without a copy", !nob.isError && nobEntry && !nobEntry.backup, JSON.stringify(nobEntry));
    await s.stop();
  }

  // ---------------------------------------------------------------- G4 file_ops
  {
    const s = await startServer("g4-fileops", { permissions: { version: 1, tools: { ...ALLOW_ALL } } });
    const sub = path.join(s.project, "old-build");
    fs.mkdirSync(path.join(sub, "inner"), { recursive: true });
    fs.writeFileSync(path.join(sub, "a.txt"), "A", "utf8");
    fs.writeFileSync(path.join(sub, "inner", "b.txt"), "B", "utf8");
    const noRec = await s.call("file_ops", { op: "delete", path: sub });
    check("G4 deleting a folder needs recursive: true", noRec.isError && /FOLDER_NEEDS_RECURSIVE|recursive: true/.test(noRec.text) && fs.existsSync(sub), noRec.text);
    const del = await s.call("file_ops", { op: "delete", path: sub, recursive: true, note: "cleanup" });
    const idx = jsonl(path.join(s.history, "index.jsonl"));
    const entry = idx.find((x) => x.op === "delete" && x.path === sub);
    check("G4 delete moves the folder to history (recoverable)", !del.isError && !fs.existsSync(sub) && entry && entry.files === 2 && fs.existsSync(path.join(entry.backup, "inner", "b.txt")), del.text + " | " + JSON.stringify(entry));
    check("G4 a medium-risk delete under 'allow' needs no card", s.tray.approvals.length === 0, s.tray.approvals.length);
    const root = await s.call("file_ops", { op: "delete", path: s.project, recursive: true });
    check("G4 file_ops refuses to delete the project root", root.isError && /REFUSED/.test(root.text) && fs.existsSync(s.project), root.text);
    const drive = await s.call("file_ops", { op: "delete", path: path.parse(s.project).root, recursive: true });
    check("G4 file_ops refuses to delete a drive", drive.isError && /REFUSED|PROTECTED/.test(drive.text), drive.text);

    const a = path.join(s.project, "a.txt"), b = path.join(s.project, "b.txt");
    fs.writeFileSync(a, "new A", "utf8");
    fs.writeFileSync(b, "old B", "utf8");
    const clash = await s.call("file_ops", { op: "copy", path: a, to: b });
    check("G4 copy onto an existing file is refused without overwrite", clash.isError && /DESTINATION_EXISTS|already exists/.test(clash.text) && readIf(b) === "old B", clash.text);
    const over = await s.call("file_ops", { op: "copy", path: a, to: b, overwrite: true });
    const ow = jsonl(path.join(s.history, "index.jsonl")).find((x) => x.op === "overwrite" && x.path === b);
    check("G4 overwrite keeps the replaced version in history", !over.isError && readIf(b) === "new A" && ow && readIf(ow.backup) === "old B", over.text + " | " + JSON.stringify(ow));
    const mv = await s.call("file_ops", { op: "move", path: a, to: path.join(s.project, "moved", "a2.txt") });
    check("G4 move creates the destination folder and removes the source", !mv.isError && !fs.existsSync(a) && readIf(path.join(s.project, "moved", "a2.txt")) === "new A", mv.text);
    const mk = await s.call("file_ops", { op: "mkdir", path: "deep\\er\\folder" });
    check("G4 mkdir creates nested folders relative to the project", !mk.isError && fs.statSync(path.join(s.project, "deep", "er", "folder")).isDirectory(), mk.text);
    const shellDel = await s.call("run_command", { command: "Remove-Item -Recurse -Force .\\deep", note: "shell delete" });
    const sc = s.tray.approvals[s.tray.approvals.length - 1] || {};
    check("G4 a shell recursive delete is flagged high and waits for the owner", s.tray.approvals.length === 1 && sc.assessment && sc.assessment.level === "high" && !shellDel.isError, s.tray.approvals.length + " " + JSON.stringify(sc.assessment) + " | " + shellDel.text.slice(0, 1500) + " | cards: " + s.tray.approvals.map((c) => c.tool + ":" + (c.assessment && c.assessment.level)).join(","));
    await s.stop();
  }

  // ------------------------------------------------- G5 read_image, system_info, open, list
  {
    const s = await startServer("g5-tools", { permissions: { version: 1, tools: { ...ALLOW_ALL } } });
    const png = path.join(s.project, "dot.png");
    fs.writeFileSync(png, PNG_1X1);
    const img = await s.call("read_image", { path: png });
    const imgPart = img.content.find((c) => c.type === "image");
    check("G5 read_image returns the picture itself", !img.isError && imgPart && imgPart.mimeType === "image/png" && Buffer.from(imgPart.data, "base64").equals(PNG_1X1), img.text);
    check("G5 read_image describes size and format", /1×1/.test(img.text) && /png/.test(img.text), img.text);
    if (process.platform === "win32") {
      const bmp = path.join(s.project, "wide.bmp");
      fs.writeFileSync(bmp, makeBmp(2000, 40));
      const big = await s.call("read_image", { path: bmp, maxSize: 500 });
      const bigPart = big.content.find((c) => c.type === "image");
      check("G5 a large BMP is downscaled to PNG on the PC", !big.isError && bigPart && bigPart.mimeType === "image/png" && /2000×40 → 500×10/.test(big.text), big.text);
    }
    const txt = path.join(s.project, "not-image.txt");
    fs.writeFileSync(txt, "hello", "utf8");
    const notImg = await s.call("read_image", { path: txt });
    check("G5 read_image refuses non-images", notImg.isError && /not a supported image/.test(notImg.text), notImg.text);
    const ri = await s.call("read_image", { path: path.join(s.dir, "config.env") });
    check("G5 read_image is covered by self-protection", ri.isError && /PROTECTED_PATH/.test(ri.text), ri.text);

    const si = await s.call("system_info", {});
    check("G5 system_info reports machine, tools and the ZeroIDE setup", !si.isError && /== Machine ==/.test(si.text) && /== ZeroIDE MCP ==/.test(si.text) && /0\.5\.0/.test(si.text) && si.text.includes(s.project) && /undo history/.test(si.text) && /node: v\d+/.test(si.text), si.text.slice(0, 600));
    check("G5 system_info lists what runs without asking", /run without asking: [^\n]*run_command/.test(si.text), si.text);

    const report = path.join(s.project, "report.html");
    fs.writeFileSync(report, "<h1>ok</h1>", "utf8");
    const m1 = await s.call("user_message", { text: "Report is ready", open: report });
    const p1 = s.tray.messages[s.tray.messages.length - 1] || {};
    check("G5 user_message open attaches a file button", !m1.isError && p1.open && p1.open.kind === "file" && p1.open.target === report && p1.open.reveal === false && /Open/.test(m1.text), JSON.stringify(p1) + " | " + m1.text);
    await sleep(700);
    await s.call("user_message", { text: "Docs", open: "https://example.com/docs" });
    const p2 = s.tray.messages[s.tray.messages.length - 1] || {};
    check("G5 user_message open accepts http(s) links", p2.open && p2.open.kind === "url" && p2.open.target === "https://example.com/docs", JSON.stringify(p2));
    await sleep(700);
    const cmdFile = path.join(s.project, "run.cmd");
    fs.writeFileSync(cmdFile, "@echo off", "utf8");
    await s.call("user_message", { text: "Script", open: cmdFile });
    const p3 = s.tray.messages[s.tray.messages.length - 1] || {};
    check("G5 scripts and programs are only revealed in Explorer", p3.open && p3.open.reveal === true, JSON.stringify(p3));
    await sleep(700);
    const m4 = await s.call("user_message", { text: "Config", open: path.join(s.dir, "config.env") });
    const p4 = s.tray.messages[s.tray.messages.length - 1] || {};
    check("G5 protected files cannot be offered as an Open button", !p4.open && /ignored/.test(m4.text), JSON.stringify(p4) + " | " + m4.text);
    await sleep(700);
    const m5 = await s.call("user_message", { text: "Bad", open: "file:///C:/Windows/notepad.exe" });
    check("G5 non-http URL schemes are ignored", !(s.tray.messages[s.tray.messages.length - 1] || {}).open && /ignored/.test(m5.text), m5.text);

    const tools = await s.list();
    const names = tools.map((t) => t.name);
    check("G5 tools/list includes read_image, file_ops and system_info (19 tools)", ["read_image", "file_ops", "system_info"].every((n) => names.includes(n)) && names.length === 19, names.join(","));
    const ann = Object.fromEntries(tools.map((t) => [t.name, t.annotations || {}]));
    check("G5 annotations: file_ops destructive, read_image/system_info read-only", ann.file_ops.destructiveHint === true && ann.read_image.readOnlyHint === true && ann.system_info.readOnlyHint === true, JSON.stringify([ann.file_ops, ann.read_image, ann.system_info]));
    const instr = (s.initResult && s.initResult.instructions) || "";
    check("G5 server instructions teach file_ops, system_info and open", /file_ops/.test(instr) && /system_info/.test(instr) && /`open`/.test(instr) && /edits\[\]/.test(instr), instr.slice(-400));
    check("G5 serverInfo reports 0.5.0", s.initResult && s.initResult.serverInfo && s.initResult.serverInfo.version === "0.5.0", JSON.stringify(s.initResult && s.initResult.serverInfo));
    await s.stop();
  }

  // ---------------------------------------------------- G6 confined token sees less
  {
    const s = await startServer("g6-confined", { permissions: { version: 1, tools: { ...ALLOW_ALL }, confinement: { enabled: true, root: "" } } });
    // Confinement comes from permissions.confinement.root; set it to the project folder.
    const pf = path.join(s.dir, "permissions.json");
    const p = JSON.parse(readIf(pf));
    p.confinement = { enabled: true, root: s.project };
    fs.writeFileSync(pf, JSON.stringify(p, null, 2), "utf8");
    await sleep(50);
    const si = await s.call("system_info", {});
    const confinedOk = /folder confinement: /.test(si.text);
    check("G6 system_info works under folder confinement", !si.isError && confinedOk, si.text.slice(0, 300));
    const out = await s.call("file_ops", { op: "copy", path: path.join(s.project, "..", "server.mjs"), to: path.join(s.project, "copy.mjs") });
    check("G6 file_ops cannot reach outside the confined folder", out.isError && /PATH_OUTSIDE_ROOT/.test(out.text), out.text);
    await s.stop();
  }
}

main()
  .catch((e) => { check("unexpected error: " + (e && e.stack ? e.stack.split("\n").slice(0, 3).join(" | ") : e), false); })
  .finally(async () => {
    clearTimeout(watchdog);
    for (const c of children) { try { c.kill(); } catch {} }
    await sleep(300);
    try { fs.rmSync(base, { recursive: true, force: true }); } catch {}
    const failed = results.filter((r) => !r.ok).length;
    console.log("\n" + (results.length - failed) + "/" + results.length + " PASS" + (failed ? ", " + failed + " FAIL" : ""));
    process.exit(failed ? 1 : 0);
  });
