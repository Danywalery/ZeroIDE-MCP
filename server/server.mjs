#!/usr/bin/env node
/**
 * ZeroIDE MCP server (Windows + Linux)
 * Platform layer: PowerShell runner + process-tree kill + Node search +
 * internal dry-run diff on Windows; original bash/grep/diff path on Linux.
 *
 * Tools: run_command, job_status, job_output, read_file, read_image,
 *        list_directory, search_files, write_file, edit_file, file_ops,
 *        system_info, audit_log, journal_entry, journal_log, user_message,
 *        task_status, await_owner, agent_message, take_screenshot
 *
 * v0.5 ("guardian"): self-protection of ZeroIDE secrets and state, a risk
 * engine that escalates dangerous calls to an approval card (critical ones
 * can never be pre-approved by a grant), diff previews on the card, an undo
 * history (history/index.jsonl + trash) instead of scattered .bak files.
 *
 * Notes:
 *  - Tool annotations describe the real behaviour (audit 2026-11-10: v1 marked
 *    write_file as read-only and job_status as writing).
 *  - Default command timeout intentionally kept at 600000 ms (owner decision).
 */
import express from "express";
import rateLimit from "express-rate-limit";
import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { SSEServerTransport } from "@modelcontextprotocol/sdk/server/sse.js";
import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";
import { spawn, spawnSync } from "node:child_process";
import fs from "node:fs";
import http from "node:http";
import os from "node:os";
import path from "node:path";
import crypto from "node:crypto";
import zlib from "node:zlib";
import { fileURLToPath } from "node:url";
let LANG = "en";
function tr(en, ru) { return LANG === "ru" ? ru : en; }

/* --------------------- config file (zeroide-mcp) -------------------- */
// Standalone runs (no systemd EnvironmentFile, no tray env) read KEY=VALUE
// pairs from config.env next to this file, or from MCP_CONFIG_FILE.
// Real environment variables always win.
(function loadConfigFile() {
  try {
    const cfgPath =
      process.env.MCP_CONFIG_FILE || new URL("./config.env", import.meta.url);
    const text = fs.readFileSync(cfgPath, "utf8");
    for (const line of text.split(/\r?\n/)) {
      const m = line.match(/^\s*([A-Z0-9_]+)\s*=\s*(.*)\s*$/);
      if (m && process.env[m[1]] === undefined) process.env[m[1]] = m[2];
    }
  } catch {}
})();

// Language: MCP_LANG=ru selects Russian strings, anything else (or unset) = English (default).
// The tray passes MCP_LANG when it starts the server; config.env MCP_LANG also works (real env wins).
LANG = (process.env.MCP_LANG || "en").toLowerCase().startsWith("ru") ? "ru" : "en";
const IS_WIN = process.platform === "win32";
const TOKEN = process.env.MCP_TOKEN || "";
// ?token= in the URL is retired (audit 2026-11-10): URLs end up in tunnel and
// proxy logs and in browser history. MCP_ALLOW_QUERY_TOKEN=1 is a temporary,
// explicit opt-in for old clients only; the default is the Bearer header.
const ALLOW_QUERY_TOKEN = process.env.MCP_ALLOW_QUERY_TOKEN === "1";
const PORT = parseInt(process.env.PORT || "39001", 10);
// Windows port defaults to loopback only: the reverse tunnel is the front door.
const HOST = process.env.MCP_HOST || (IS_WIN ? "127.0.0.1" : "0.0.0.0");
const PROJECT_DIR = process.env.PROJECT_DIR || "/opt/agent";
const LOG_DIR = process.env.MCP_LOG_DIR || "/opt/agent-mcp/logs";
const AUDIT_FILE = path.join(LOG_DIR, "audit.jsonl");
const JOURNAL_FILE = path.join(LOG_DIR, "journal.jsonl");
const PERMISSIONS_FILE =
  process.env.MCP_PERMISSIONS_FILE ||
  path.join(path.dirname(LOG_DIR), "permissions.json");
const TOKENS_FILE =
  process.env.MCP_TOKENS_FILE ||
  path.join(path.dirname(PERMISSIONS_FILE), "tokens.json");
const OAUTH_FILE =
  process.env.MCP_OAUTH_FILE ||
  path.join(path.dirname(PERMISSIONS_FILE), "oauth.json");
const OAUTH_ENABLED = process.env.MCP_OAUTH_ENABLED !== "0";
const TRAY_CONTROL_PORT = parseInt(
  process.env.TRAY_CONTROL_PORT || "39012",
  10
);
const TRAY_CONTROL_SECRET = process.env.TRAY_CONTROL_SECRET || "";
const SERVER_VERSION = "0.5.0"; // keep equal to server/package.json and agent-manifest.json

const AUDIT_MAX_BYTES = 5 * 1024 * 1024; // lowered from 20 MB on 15.08 — at 20 MB rotation never triggered
const AUDIT_MAX_GENERATIONS = 5;
const NUDGE_THRESHOLD = 15; // mutating calls without journal_entry, after which we add a reminder
const MUTATING_TOOLS = new Set(["run_command", "write_file", "edit_file", "file_ops"]);
const READ_TOOLS = new Set(["read_file", "read_image", "system_info", "list_directory", "search_files", "job_status", "job_output", "audit_log", "journal_log", "user_message", "task_status", "agent_message", "await_owner"]);
const WRITE_TOOLS = new Set(["write_file", "edit_file", "file_ops", "journal_entry"]);
const EXEC_TOOLS = new Set(["run_command"]);
const FILE_TOOLS = new Set(["read_file", "read_image", "list_directory", "search_files", "write_file", "edit_file", "file_ops"]);
// Tools whose target path is written to (self-protection checks them as writes).
const WRITE_PATH_TOOLS = new Set(["write_file", "edit_file", "file_ops"]);
const DEFAULT_MAX_OUTPUT = 30 * 1024;
// Wait ceiling in job_status: Notion drops the connection at 60 s (verified:
// sleep 50 passes, sleep 61 breaks), keeping headroom for network and response.
const MAX_JOB_WAIT_SEC = 50;
const HARD_CAP = 50 * 1024 * 1024;
const DEFAULT_TIMEOUT_MS = 600000;
const DEDUPE_TTL_MS = 6 * 3600 * 1000;
const JOB_TTL_MS = 6 * 3600 * 1000;
const KEEPALIVE_MS = 20000;
const DEFAULT_READ_MAX_BYTES = 200 * 1024;
const EXCLUDE_DIRS = [
  "node_modules",
  ".git",
  "dist",
  "build",
  ".next",
  "coverage",
  ".turbo",
  ".cache",
];

function resolvePath(p) {
  if (!p) return PROJECT_DIR;
  return path.isAbsolute(p) ? p : path.join(PROJECT_DIR, p);
}

function sha256(buf) {
  return crypto.createHash("sha256").update(buf).digest("hex");
}

function fileSha(fullPath) {
  try {
    return sha256(fs.readFileSync(fullPath));
  } catch {
    return null;
  }
}

function ts() {
  return new Date().toISOString();
}

/* ------------------------------- audit log ------------------------------- */

// Secrets that landed in the command text in full — mask before writing to disk, not after.
const SECRET_PATTERNS = [
  // token=... / password=... / api_key=... / secret=... в любом регистре
  /((?:token|password|passwd|pwd|secret|api[_-]?key|access[_-]?key|private[_-]?key)\s*[=:]\s*)(['"]?)([^\s'"&]+)\2/gi,
  // Authorization: Bearer xxx / Basic xxx
  /(Authorization\s*:?\s*(?:Bearer|Basic)\s+)([A-Za-z0-9._~+/=-]+)/gi,
  // environment variables like FOO_KEY=..., FOO_TOKEN=..., FOO_SECRET=...
  /([A-Z][A-Z0-9_]*(?:KEY|TOKEN|SECRET|PASSWORD)[A-Z0-9_]*\s*=\s*)(['"]?)([^\s'"&]+)\2/g,
  // PEM private key blocks
  /(-----BEGIN [A-Z ]*PRIVATE KEY-----)[\s\S]*?(-----END [A-Z ]*PRIVATE KEY-----)/g,
];

function redactSecrets(text) {
  if (typeof text !== "string" || !text) return text;
  let out = text;
  out = out.replace(SECRET_PATTERNS[0], (_m, p1) => p1 + "***REDACTED***");
  out = out.replace(SECRET_PATTERNS[1], (_m, p1) => p1 + "***REDACTED***");
  out = out.replace(SECRET_PATTERNS[2], (_m, p1) => p1 + "***REDACTED***");
  out = out.replace(
    SECRET_PATTERNS[3],
    (_m, begin, end) => begin + "\n***REDACTED***\n" + end
  );
  return out;
}

// seq — a monotonic entry number, independent of file/rotation.
// Needed so journal_entry can refer to a range of audit entries without timestamp references.
let auditSeqCounter = 0;
let journalSeqCounter = 0;

function initSeqCounters() {
  const scan = (file) => {
    let max = 0;
    if (!fs.existsSync(file)) return max;
    try {
      const lines = fs.readFileSync(file, "utf8").split("\n").filter(Boolean);
      for (const line of lines) {
        try {
          const e = JSON.parse(line);
          if (typeof e.seq === "number" && e.seq > max) max = e.seq;
        } catch {}
      }
    } catch {}
    return max;
  };
  auditSeqCounter = Math.max(scan(AUDIT_FILE), scan(AUDIT_FILE + ".1"));
  journalSeqCounter = scan(JOURNAL_FILE);
}

function nextAuditSeq() {
  auditSeqCounter += 1;
  return auditSeqCounter;
}

function nextJournalSeq() {
  journalSeqCounter += 1;
  return journalSeqCounter;
}

// Size-based rotation with compression and a limited number of generations (was: a single
// uncompressed .1 generation, 20 MB threshold — never triggered in practice).
function rotateAuditFile() {
  try {
    for (let i = AUDIT_MAX_GENERATIONS; i >= 1; i--) {
      const src = AUDIT_FILE + "." + i + ".gz";
      if (!fs.existsSync(src)) continue;
      if (i >= AUDIT_MAX_GENERATIONS) fs.unlinkSync(src);
      else fs.renameSync(src, AUDIT_FILE + "." + (i + 1) + ".gz");
    }
    const raw = fs.readFileSync(AUDIT_FILE);
    fs.writeFileSync(AUDIT_FILE + ".1.gz", zlib.gzipSync(raw));
    fs.writeFileSync(AUDIT_FILE, "");
  } catch {}
}

// Counter of mutating calls (run_command/write_file/edit_file) since the last journal entry.
// Global, not per session: in practice every tool call through this
// MCP server arrives with a new session (verified 15.08 on the live server — session
// changes from call to call even within one conversation), so grouping
// by session works neither for the reminder nor for auto-attaching the range in journal_entry.
let mutatingSinceJournal = 0;
let lastJournalToSeq = 0;

function audit(entry) {
  try {
    fs.mkdirSync(LOG_DIR, { recursive: true });
    try {
      const st = fs.statSync(AUDIT_FILE);
      if (st.size > AUDIT_MAX_BYTES) rotateAuditFile();
    } catch {}
    const safeEntry = { ...entry };
    if (typeof safeEntry.command === "string")
      safeEntry.command = redactSecrets(safeEntry.command);
    fs.appendFileSync(
      AUDIT_FILE,
      JSON.stringify({ seq: nextAuditSeq(), ts: ts(), ...safeEntry }) + "\n"
    );
    if (entry && MUTATING_TOOLS.has(entry.tool)) {
      mutatingSinceJournal += 1;
    }
  } catch {}
}

// All audit entries (except journal_entry itself) with seq > sinceSeq are attached to
// journal_entry automatically. Globally across the server, not per session (see above).
function readAuditSince(sinceSeq) {
  const result = { fromSeq: null, toSeq: null, count: 0 };
  try {
    if (!fs.existsSync(AUDIT_FILE)) return result;
    const lines = fs.readFileSync(AUDIT_FILE, "utf8").split("\n").filter(Boolean);
    for (const line of lines) {
      let e;
      try {
        e = JSON.parse(line);
      } catch {
        continue;
      }
      if (e.tool === "journal_entry") continue;
      if (typeof e.seq !== "number" || e.seq <= sinceSeq) continue;
      if (result.fromSeq === null) result.fromSeq = e.seq;
      result.toSeq = e.seq;
      result.count++;
    }
  } catch {}
  return result;
}

function initJournalState() {
  try {
    if (!fs.existsSync(JOURNAL_FILE)) return;
    const lines = fs.readFileSync(JOURNAL_FILE, "utf8").split("\n").filter(Boolean);
    if (!lines.length) return;
    const last = JSON.parse(lines[lines.length - 1]);
    if (last.auditRange && typeof last.auditRange.toSeq === "number") {
      lastJournalToSeq = last.auditRange.toSeq;
    }
  } catch {}
}

initSeqCounters();
initJournalState();

/* ----------------- unified diff (zeroide-mcp, no external diff) ------ */

// Small LCS-based unified diff for dryRun previews. Inputs are capped so a
// huge file cannot blow up the O(n*m) table; the cap is reported in output.
function makeUnifiedDiff(oldText, newText, label) {
  const MAXL = 3000;
  const a = String(oldText ?? "").split("\n");
  const b = String(newText ?? "").split("\n");
  if (a.length > MAXL || b.length > MAXL) {
    return tr("(file too large for diff preview: ", "(файл слишком большой для diff-превью: ") + a.length + " -> " + b.length + tr(" lines)", " строк)");
  }
  const n = a.length, m = b.length;
  const dp = Array.from({ length: n + 1 }, () => new Uint32Array(m + 1));
  for (let i = n - 1; i >= 0; i--) {
    for (let j = m - 1; j >= 0; j--) {
      dp[i][j] = a[i] === b[j] ? dp[i + 1][j + 1] + 1 : Math.max(dp[i + 1][j], dp[i][j + 1]);
    }
  }
  const out = ["--- " + label + " (current)", "+++ " + label + " (new)"];
  let i = 0, j = 0;
  while (i < n && j < m) {
    if (a[i] === b[j]) { out.push(" " + a[i]); i++; j++; }
    else if (dp[i + 1][j] >= dp[i][j + 1]) { out.push("-" + a[i]); i++; }
    else { out.push("+" + b[j]); j++; }
  }
  while (i < n) out.push("-" + a[i++]);
  while (j < m) out.push("+" + b[j++]);
  if (out.length === 2) return tr("(no changes)", "(изменений нет)");
  return out.join("\n");
}

function globToRegExp(glob) {
  const esc = String(glob).replace(/[.+^${}()|[\]\\]/g, "\\$&").replace(/\*/g, ".*").replace(/\?/g, ".");
  return new RegExp("^" + esc + "$", "i");
}

/* ------------------------------- truncation ------------------------------ */

function clampText(text, maxBytes) {
  const buf = Buffer.from(text || "", "utf8");
  if (buf.length <= maxBytes) {
    return { text: text || "", truncated: false, totalBytes: buf.length };
  }
  const half = Math.max(1024, Math.floor(maxBytes / 2));
  const head = buf.subarray(0, half).toString("utf8");
  const tail = buf.subarray(buf.length - half).toString("utf8");
  const omitted = buf.length - half * 2;
  const marker =
    tr("\n\n... [TRUNCATED: omitted ", "\n\n... [ОБРЕЗАНО: пропущено ") +
    omitted +
    tr(" bytes out of ", " байт из ") +
    buf.length +
    tr(". Showing start and end. The full output is kept: page it with job_output({ jobId }) using the job_id below] ...\n\n", ". Показаны начало и конец. Полный вывод сохранён: листай его через job_output({ jobId }) по job_id ниже] ...\n\n");
  return { text: head + marker + tail, truncated: true, totalBytes: buf.length };
}

/* ------------------------------ dedupe cache ----------------------------- */

const dedupe = new Map();

function dedupeGet(id) {
  if (!id) return null;
  const e = dedupe.get(id);
  if (!e) return null;
  if (Date.now() - e.ts > DEDUPE_TTL_MS) {
    dedupe.delete(id);
    return null;
  }
  return e;
}

function dedupeSet(id, promise) {
  if (!id) return;
  dedupe.set(id, { ts: Date.now(), promise });
}

setInterval(() => {
  const now = Date.now();
  for (const [k, v] of dedupe) if (now - v.ts > DEDUPE_TTL_MS) dedupe.delete(k);
  for (const [k, v] of jobs) if (now - v.createdAt > JOB_TTL_MS) jobs.delete(k);
}, 600000).unref();

/* --------------------------------- jobs ---------------------------------- */

const jobs = new Map();

/* ------------------------------ shell runner ----------------------------- */

// Kill the whole process tree. POSIX keeps the original SIGKILL of the shell
// (bash -c execs simple commands, and job commands are shell-led). Windows
// needs taskkill /T or grandchildren (compilers, dev servers) survive.
function killTree(child) {
  try {
    if (IS_WIN && child.pid) {
      spawn("taskkill", ["/PID", String(child.pid), "/T", "/F"], { stdio: "ignore" });
    } else if (child.pid) {
      // POSIX: the shell was spawned detached (own process group), so a group
      // kill takes the whole tree. Killing only the shell leaves orphaned
      // grandchildren holding the stdio pipes open, and the call would block
      // until they exit on their own.
      try { process.kill(-child.pid, "SIGKILL"); } catch { child.kill("SIGKILL"); }
    } else {
      child.kill("SIGKILL");
    }
  } catch {}
}

let winShellCache = null;
function detectWinShell() {
  if (winShellCache) return winShellCache;
  const forced = process.env.MCP_SHELL;
  if (forced) return (winShellCache = { exe: forced, kind: "powershell" });
  for (const exe of ["pwsh.exe", "powershell.exe"]) {
    try {
      const r = spawnSync(exe, ["-NoProfile", "-Command", "$PSVersionTable.PSVersion.ToString()"], { encoding: "utf8", timeout: 8000 });
      if (r.status === 0) return (winShellCache = { exe, kind: "powershell" });
    } catch {}
  }
  return (winShellCache = { exe: "powershell.exe", kind: "powershell" });
}

function startShell({ command, cwd, timeoutMs }) {
  const state = {
    command,
    cwd: cwd ? resolvePath(cwd) : PROJECT_DIR,
    status: "running",
    exitCode: null,
    signal: null,
    timedOut: false,
    error: null,
    startedAt: Date.now(),
    finishedAt: null,
    stdout: "",
    stderr: "",
    stdoutBytes: 0,
    stderrBytes: 0,
  };

  let child;
  if (IS_WIN) {
    const shell = detectWinShell();
    // Force UTF-8 in and out (the classic Windows console-encoding trap) and
    // translate the command outcome into a process exit code: native
    // $LASTEXITCODE wins, a failed cmdlet pipeline maps to 1.
    const preamble =
      "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " +
      "$OutputEncoding = [System.Text.Encoding]::UTF8; " +
      "$ErrorActionPreference = 'Continue'; ";
    const wrapped =
      preamble +
      command +
      "\n$__apOk = $?; $__apCode = $LASTEXITCODE; " +
      "if ($__apOk) { if ($null -ne $__apCode) { exit $__apCode } else { exit 0 } } " +
      "else { if ($null -ne $__apCode -and $__apCode -ne 0) { exit $__apCode } else { exit 1 } }";
    child = spawn(shell.exe, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", wrapped], {
      cwd: state.cwd,
      env: process.env,
      windowsHide: true,
    });
  } else {
    child = spawn("/bin/bash", ["-c", "set -o pipefail\n" + command], {
      cwd: state.cwd,
      env: process.env,
      detached: true,
    });
  }

  const outChunks = [];
  const errChunks = [];

  child.stdout.on("data", (d) => {
    state.stdoutBytes += d.length;
    if (state.stdoutBytes <= HARD_CAP) outChunks.push(d);
  });
  child.stderr.on("data", (d) => {
    state.stderrBytes += d.length;
    if (state.stderrBytes <= HARD_CAP) errChunks.push(d);
  });

  const timer = setTimeout(() => {
    state.timedOut = true;
    killTree(child);
  }, timeoutMs || DEFAULT_TIMEOUT_MS);

  const finish = () => {
    clearTimeout(timer);
    state.finishedAt = Date.now();
    state.stdout = Buffer.concat(outChunks).toString("utf8");
    state.stderr = Buffer.concat(errChunks).toString("utf8");
  };

  const promise = new Promise((resolve) => {
    child.on("error", (err) => {
      finish();
      state.status = "failed";
      state.error = String((err && err.message) || err);
      state.exitCode = -1;
      resolve(state);
    });
    child.on("close", (code, signal) => {
      finish();
      state.exitCode = code === null ? -1 : code;
      state.signal = signal || null;
      state.status = state.timedOut ? "timeout" : "completed";
      resolve(state);
    });
  });

  return { state, child, promise };
}

// A finished foreground run whose output was clipped is kept as a job, so the
// omitted middle stays reachable through job_output (paging) instead of being
// lost. Only clipped runs are kept: the job map holds full buffers in memory.
function keepAsJob(state) {
  if (state.jobId) return state.jobId;
  const jobId = crypto.randomUUID().slice(0, 8);
  state.jobId = jobId;
  jobs.set(jobId, {
    id: jobId,
    state,
    promise: Promise.resolve(state),
    createdAt: Date.now(),
  });
  return jobId;
}

function formatRun(state, maxOut, note) {
  const so = clampText(state.stdout, maxOut);
  const se = clampText(state.stderr, Math.min(maxOut, 32 * 1024));
  const lines = [];
  if (note) lines.push(note);
  if ((so.truncated || se.truncated) && state.status !== "running") {
    lines.push("job_id: " + keepAsJob(state));
    lines.push(
      tr("full_output: job_output({ jobId, stream, offsetBytes, maxBytes }) pages the complete stdout/stderr",
         "full_output: job_output({ jobId, stream, offsetBytes, maxBytes }) листает полный stdout/stderr")
    );
  }
  lines.push("exit_code: " + state.exitCode);
  lines.push("status: " + state.status);
  if (state.signal) lines.push("signal: " + state.signal);
  lines.push("timed_out: " + state.timedOut);
  lines.push(
    "duration_ms: " + ((state.finishedAt || Date.now()) - state.startedAt)
  );
  lines.push("cwd: " + state.cwd);
  lines.push(
    "stdout_bytes: " + state.stdoutBytes + (so.truncated ? tr(" (truncated)", " (обрезано)") : "")
  );
  lines.push(
    "stderr_bytes: " + state.stderrBytes + (se.truncated ? tr(" (truncated)", " (обрезано)") : "")
  );
  if (state.error) lines.push("spawn_error: " + state.error);
  lines.push("--- stdout ---");
  lines.push(so.text || tr("(empty)", "(пусто)"));
  lines.push("--- stderr ---");
  lines.push(se.text || tr("(empty)", "(пусто)"));
  return lines.join("\n");
}

function textResult(text, isError) {
  const r = { content: [{ type: "text", text }] };
  if (isError) r.isError = true;
  return r;
}

function errResult(e) {
  return textResult("ERROR: " + ((e && e.message) || String(e)), true);
}

// Wraps a tool handler: if enough have accumulated for this session
// NUDGE_THRESHOLD mutating calls without journal_entry, it appends
// a reminder to the end of the text response — regardless of which
// tool is called next.
function wrapWithNudge(handler) {
  return async (...args) => {
    const result = await handler(...args);
    try {
      if (
        mutatingSinceJournal >= NUDGE_THRESHOLD &&
        result &&
        Array.isArray(result.content)
      ) {
        for (const item of result.content) {
          if (item.type === "text") {
            item.text +=
              tr("\n\n⚠️ reminder: ", "\n\n⚠️ напоминание: ") +
              mutatingSinceJournal +
              tr(" mutating calls (run_command/write_file/edit_file) without a journal entry. ", " мутирующих вызовов (run_command/write_file/edit_file) без записи в журнал. ") +
              tr("If this is the end of a finished chunk of work — call journal_entry before continuing.", "Если это конец законченного куска работы — вызови journal_entry, прежде чем продолжать.");
            break;
          }
        }
      }
    } catch {}
    return result;
  };
}

/* --------------------- file search (zeroide-mcp, Node) --------------- */

// Windows has no grep. This walk-and-scan implementation mirrors the grep
// branch output format: file:line:text matches, true totals, context lines.
function nodeSearchFiles({ query, root, include, regex, ignoreCase, contextLines, cap, includeIgnored }) {
  const mask = include ? globToRegExp(include) : null;
  let pattern = null;
  if (regex) {
    pattern = new RegExp(query, ignoreCase ? "i" : "");
  }
  const needle = ignoreCase ? String(query).toLowerCase() : String(query);
  const ctx = Math.max(0, Math.min(Number(contextLines) || 0, 20));
  const matchLines = [];
  const filesSet = new Set();
  let totalMatches = 0;

  // Text decoding: UTF-8 (BOM stripped) and UTF-16 with a BOM (Windows
  // PowerShell 5.1 writes UTF-16LE by default). Other files with NUL bytes
  // are treated as binary and skipped.
  const decodeText = (buf) => {
    if (buf.length >= 2 && buf[0] === 0xff && buf[1] === 0xfe) return buf.subarray(2).toString("utf16le");
    if (buf.length >= 2 && buf[0] === 0xfe && buf[1] === 0xff) {
      return Buffer.from(buf.subarray(2, 2 + ((buf.length - 2) & ~1))).swap16().toString("utf16le");
    }
    if (buf.subarray(0, 8000).includes(0)) return null;
    const s = buf.toString("utf8");
    return s.charCodeAt(0) === 0xfeff ? s.slice(1) : s;
  };
  const searchOne = (abs) => {
    // Never surface ZeroIDE's own credentials through search results.
    if (selfProtection(abs, "read") === "secret") return;
    let buf;
    try { buf = fs.readFileSync(abs); } catch { return; }
    const text = decodeText(buf);
    if (text === null) return;
    const lines = text.split(/\r?\n/);
    for (let idx = 0; idx < lines.length; idx++) {
      const line = lines[idx];
      const hit = pattern
        ? pattern.test(line)
        : (ignoreCase ? line.toLowerCase() : line).includes(needle);
      if (!hit) continue;
      totalMatches++;
      filesSet.add(abs);
      if (matchLines.length < cap) {
        if (ctx > 0) {
          for (let k = Math.max(0, idx - ctx); k < idx; k++)
            matchLines.push(abs + "-" + (k + 1) + "-" + lines[k]);
        }
        matchLines.push(abs + ":" + (idx + 1) + ":" + line);
        if (ctx > 0) {
          for (let k = idx + 1; k <= Math.min(lines.length - 1, idx + ctx); k++)
            matchLines.push(abs + "-" + (k + 1) + "-" + lines[k]);
        }
      }
    }
  };
  const walk = (dir) => {
    let items;
    try { items = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
    for (const item of items) {
      const abs = path.join(dir, item.name);
      if (item.isDirectory()) {
        if (!includeIgnored && EXCLUDE_DIRS.includes(item.name)) continue;
        walk(abs);
      } else if (item.isFile()) {
        if (mask && !mask.test(item.name)) continue;
        searchOne(abs);
      }
    }
  };
  // path may point to a single file (audit 2026-11-10: it used to return 0
  // matches because only directories were walked). An explicit file is
  // searched even if it does not match include.
  let rootStat = null;
  try { rootStat = fs.statSync(root); } catch {}
  if (!rootStat) throw new Error(tr("path not found: ", "путь не найден: ") + root);
  const singleFile = rootStat.isFile();
  if (singleFile) searchOne(root);
  else walk(root);
  return [
    "query: " + query + (regex ? "  (regex)" : "  (literal)"),
    "root: " + root + (singleFile ? tr("  (single file)", "  (один файл)") : ""),
    include ? "include: " + include + (singleFile ? tr("  (not applied: path is a single file)", "  (не применяется: путь указывает на один файл)") : "") : null,
    "matches_total: " + totalMatches,
    "files_with_matches: " + filesSet.size,
    "showing: " + Math.min(totalMatches, cap) +
      (totalMatches > cap ? tr(" (truncated at maxResults=", " (обрезано по maxResults=") + cap + tr(", increase maxResults or narrow the query)", ", увеличь maxResults или сузь запрос)") : ""),
    totalMatches === 0 ? tr("note: no matches (this is not an error)", "note: совпадений нет (это не ошибка)") : null,
    "--- matches (file:line:text) ---",
    matchLines.slice(0, cap).join("\n") || tr("(no matches)", "(нет совпадений)"),
  ].filter(Boolean).join("\n");
}

/* ========== v0.5 · self-protection, risk assessment, file history, helpers ========== */

const SERVER_DIR = path.dirname(fileURLToPath(import.meta.url));
// A real installation keeps tray-config.json / ZeroIDE-MCP.exe next to the server folder.
// Source checkouts and test copies have neither, so they are not treated as one.
const INSTALL_ROOT = (() => {
  const parent = path.dirname(SERVER_DIR);
  try {
    if (fs.existsSync(path.join(parent, "tray-config.json")) || fs.existsSync(path.join(parent, "ZeroIDE-MCP.exe"))) return parent;
  } catch {}
  return null;
})();
const ALLOW_SELF_ACCESS = process.env.MCP_ALLOW_SELF_ACCESS === "1";
const HISTORY_DIR = process.env.MCP_HISTORY_DIR || path.join(path.dirname(LOG_DIR), "history");
const HISTORY_INDEX = path.join(HISTORY_DIR, "index.jsonl");
const HISTORY_RETENTION_DAYS = Math.max(1, parseInt(process.env.MCP_HISTORY_DAYS || "30", 10) || 30);
const HISTORY_MAX_FILE_BYTES = 25 * 1024 * 1024; // bigger files are recorded without a copy
const BACKUP_MODE = ["history", "sidecar", "both"].includes(process.env.MCP_BACKUP_MODE) ? process.env.MCP_BACKUP_MODE : "history";
const TRASH_MAX_FILES = 20000;
const TRASH_MAX_BYTES = 2 * 1024 * 1024 * 1024;

// ---- Self-protection ------------------------------------------------------------
// ZeroIDE's own credentials (the MCP token, the tray control secret, the token and
// OAuth stores) are never readable through file tools, and its settings, journals,
// history and program files are never writable through them. Otherwise any client,
// even a read-only one, could lift the full token from config.env, approve its own
// requests through the tray control channel or loosen permissions.json.
// MCP_ALLOW_SELF_ACCESS=1 in config.env lifts the rule for owners who want that.
function canonicalLoose(p) {
  const abs = path.resolve(String(p || ""));
  try { return foldCase(fs.realpathSync.native(abs)); } catch {}
  try { return foldCase(path.join(fs.realpathSync.native(path.dirname(abs)), path.basename(abs))); } catch {}
  return foldCase(abs);
}
function insideDir(dirCanon, targetCanon) {
  if (!dirCanon || !targetCanon) return false;
  if (targetCanon === dirCanon) return true;
  return targetCanon.startsWith(dirCanon.endsWith(path.sep) ? dirCanon : dirCanon + path.sep);
}
let selfProtectCache = null;
function selfProtectTables() {
  if (selfProtectCache) return selfProtectCache;
  const secrets = new Set(
    [
      path.join(SERVER_DIR, "config.env"),
      process.env.MCP_CONFIG_FILE || "",
      TOKENS_FILE,
      OAUTH_FILE,
      path.join(path.dirname(PERMISSIONS_FILE), "telegram.json"),
      INSTALL_ROOT ? path.join(INSTALL_ROOT, "tray-config.json") : "",
      INSTALL_ROOT ? path.join(INSTALL_ROOT, "telegram.json") : "",
    ]
      .filter(Boolean)
      .map(canonicalLoose)
  );
  const stateFiles = new Set([PERMISSIONS_FILE].map(canonicalLoose));
  const stateDirs = [LOG_DIR, HISTORY_DIR].map(canonicalLoose);
  if (INSTALL_ROOT) stateDirs.push(canonicalLoose(INSTALL_ROOT));
  selfProtectCache = { secrets, stateFiles, stateDirs, root: INSTALL_ROOT ? canonicalLoose(INSTALL_ROOT) : null };
  return selfProtectCache;
}
// null = fine; "secret" = neither read nor write; "state" = read-only for agents.
function selfProtection(fullPath, intent) {
  if (ALLOW_SELF_ACCESS || !fullPath) return null;
  const t = selfProtectTables();
  const c = canonicalLoose(fullPath);
  if (t.secrets.has(c)) return "secret";
  if (t.root && insideDir(t.root, c) && /^(tunnel_key.*|.*\.(token|key|pem))$/i.test(path.basename(c))) return "secret";
  if (intent === "write" && (t.stateFiles.has(c) || t.stateDirs.some((d) => insideDir(d, c)))) return "state";
  return null;
}
function protectedPathResult(tool, fullPath, kind) {
  return textResult(
    [
      kind === "secret"
        ? tr("DENIED [PROTECTED_PATH]: this file holds ZeroIDE MCP credentials (tokens, control secret). Agents never get them through MCP tools.", "ОТКАЗ [PROTECTED_PATH]: в этом файле лежат учётные данные ZeroIDE MCP (токены, секрет управления). Агенты не получают их через инструменты MCP.")
        : tr("DENIED [PROTECTED_PATH]: this is ZeroIDE MCP's own configuration, journal, history or program folder. Agents cannot change it through MCP tools; the owner changes settings in the tray.", "ОТКАЗ [PROTECTED_PATH]: это собственная конфигурация, журнал, история или папка программы ZeroIDE MCP. Агент не может менять её через инструменты MCP; настройки меняет владелец в трее."),
      "tool: " + tool,
      "path: " + fullPath,
      tr("action: Do NOT try other tools or commands to reach this file. If the task really needs it, tell the owner what and why.", "action: НЕ пытайся добраться до файла другими инструментами или командами. Если задаче это действительно нужно, объясни владельцу, что и зачем."),
    ].join("\n"),
    true
  );
}

// ---- Risk assessment -------------------------------------------------------------
// A transparent, rule-based reading of what a call can do. It explains the approval
// card in plain words and powers two safety nets (permissions.json -> riskGate):
// an "allow" rule no longer passes a high-risk call silently, and a tool-wide
// temporary grant never covers a critical one: the owner sees that exact call first.
const RISK_ORDER = { low: 0, medium: 1, high: 2, critical: 3 };
const riskAtLeast = (level, min) => (RISK_ORDER[level] ?? 0) >= (RISK_ORDER[min] ?? 99);
const riskRule = (level, group, code, en, ru) => ({ level, group, code, en, ru });
const TRAY_CONTROL_RE = new RegExp("(127[.]0[.]0[.]1|localhost)[ ]*:[ ]*" + TRAY_CONTROL_PORT + "(?![0-9])", "i");
const COMMAND_RISK_RULES = [
  [/simulate-click|X-ZeroIDE-Control|tray-config\.json|\btokens\.json\b|\boauth\.json\b|ZeroIDE-MCP[\\/]+server[\\/]+config\.env/i,
    riskRule("critical", "self", "zeroide_self", "Touches ZeroIDE MCP's own secrets or approval channel.", "Затрагивает секреты или канал подтверждений самого ZeroIDE MCP.")],
  [/\b(Remove-Item|Clear-Content|Set-Content|Out-File|Move-Item|Rename-Item|del|erase)\b[^\n]*?(audit\.jsonl|journal\.jsonl|permissions\.json|ZeroIDE-MCP[\\/]+(logs|history|server)\b)/i,
    riskRule("high", "self", "zeroide_tamper", "Changes ZeroIDE MCP's own journals, history or permissions.", "Меняет журналы, историю или права самого ZeroIDE MCP.")],
  [/\b(Format-Volume|Clear-Disk|Initialize-Disk|diskpart|cipher(\.exe)?\s+\/w|format(\.com)?\s+[a-z]:)/i,
    riskRule("critical", "disk", "disk_wipe", "Can erase a whole disk or partition.", "Может стереть целый диск или раздел.")],
  [/\b(vssadmin(\.exe)?\s+delete|wbadmin(\.exe)?\s+delete|shadowcopy\s+delete|bcdedit)/i,
    riskRule("critical", "backup", "backup_wipe", "Deletes restore points or changes how Windows boots.", "Удаляет точки восстановления или меняет загрузку Windows.")],
  [/(Set-MpPreference\b[^\n]*-Disable|Add-MpPreference\b[^\n]*-Exclusion|advfirewall\s+set\s+[^\n]*state\s+off|Set-NetFirewallProfile\b[^\n]*-Enabled\s+(\$?false|0)|\bsc(\.exe)?\s+(stop|config|delete)\s+WinDefend)/i,
    riskRule("critical", "defense", "defense_off", "Weakens Windows protection (Defender or firewall).", "Ослабляет защиту Windows (Defender или брандмауэр).")],
  [/\b(mimikatz|sekurlsa|lsass|reg(\.exe)?\s+save\s+hklm\\+(sam|system|security)|vaultcmd|cmdkey(\.exe)?\s+\/list)\b/i,
    riskRule("critical", "creds", "credential_dump", "Looks like an attempt to extract saved passwords or credentials.", "Похоже на попытку извлечь сохранённые пароли или учётные данные.")],
  [/\b(GetAsyncKeyState|SetWindowsHookEx|GetKeyboardState)\b/i,
    riskRule("critical", "spy", "keylogger", "Can record keystrokes.", "Может записывать нажатия клавиш.")],
  [/(Invoke-WebRequest|\biwr\b|\bcurl\b|\bwget\b|Invoke-RestMethod|\birm\b|DownloadString|DownloadFile|Net\.WebClient|Start-BitsTransfer)[\s\S]*(\|\s*(iex|Invoke-Expression|powershell|pwsh|cmd)\b|\biex\b|Invoke-Expression)|(\biex\b|Invoke-Expression)[\s\S]*(DownloadString|Invoke-WebRequest|\biwr\b|Invoke-RestMethod|\birm\b)/i,
    riskRule("critical", "net", "download_exec", "Downloads code from the internet and runs it right away.", "Скачивает код из интернета и сразу запускает его.")],
  [/(localgroup\s+(administrators|администраторы)\b[^\n]*\/add|Add-LocalGroupMember\b[^\n]*(Administrators|Администраторы))/i,
    riskRule("critical", "accounts", "admin_grant", "Gives an account administrator rights.", "Выдаёт учётной записи права администратора.")],
  [/\b(Remove-Item|rm|ri|del|erase|rd|rmdir)\b[^\n|;&]*?(\s-r(ecurse)?\b|\s\/s\b|\s-rf\b)/i,
    riskRule("high", "delete", "delete_recursive", "Deletes folders together with everything inside.", "Удаляет папки вместе со всем содержимым.")],
  [/\b(Remove-Item|rm|ri|del|erase)\b[^\n|;&]*?(\s-Force\b|\s\/[fq]\b)/i,
    riskRule("high", "delete", "delete_force", "Force-deletes files, including hidden and read-only ones.", "Принудительно удаляет файлы, включая скрытые и защищённые от записи.")],
  [/\b(Remove-Item|ri|rm|del|erase|rd|rmdir)\b/i,
    riskRule("medium", "delete", "delete", "Deletes files or folders.", "Удаляет файлы или папки.")],
  [/\bgit\s+(push\b[^\n]*(\s--force\b|\s-f\b|\s--mirror\b|\s--delete\b)|reset\s+--hard|clean\s+-[a-z]*[fdx]|checkout\s+--\s|branch\s+-D\b|filter-branch|reflog\s+expire)/i,
    riskRule("high", "git", "git_destructive", "Can permanently discard Git history or uncommitted work.", "Может безвозвратно стереть историю Git или незакоммиченные изменения.")],
  [/\bgit\s+push\b/i,
    riskRule("medium", "git", "git_push", "Publishes commits to a remote repository.", "Публикует коммиты во внешний репозиторий.")],
  [/(reg(\.exe)?\s+(add|delete|import|restore|load)\b[^\n]*(HKLM|HKEY_LOCAL_MACHINE)|(Set-ItemProperty|New-ItemProperty|Remove-ItemProperty|New-Item|Remove-Item)\b[^\n|;]*(HKLM:|HKEY_LOCAL_MACHINE))/i,
    riskRule("high", "registry", "registry_system", "Changes system-wide registry settings.", "Меняет системные параметры реестра.")],
  [/(reg(\.exe)?\s+(add|delete|import|restore|load)\b|(Set-ItemProperty|New-ItemProperty|Remove-ItemProperty)\b[^\n|;]*HK(CU|LM|CR|U))/i,
    riskRule("medium", "registry", "registry_user", "Changes registry settings.", "Меняет параметры реестра.")],
  [/(CurrentVersion\\+Run(Once)?\b|Start Menu\\+Programs\\+Startup|shell:startup|\\Winlogon\b|Image File Execution Options)/i,
    riskRule("high", "persist", "autorun", "Makes something start automatically with Windows.", "Добавляет автозапуск вместе с Windows.")],
  [/(schtasks(\.exe)?\s+\/create|Register-ScheduledTask|New-ScheduledTask)/i,
    riskRule("high", "persist", "scheduled_task", "Creates a scheduled task that runs later, possibly repeatedly.", "Создаёт задачу планировщика, которая запустится позже, возможно многократно.")],
  [/(New-Service|\bsc(\.exe)?\s+(create|config|delete)\b|Set-Service\b|Stop-Service\b|Remove-Service\b)/i,
    riskRule("high", "service", "service_change", "Creates, stops or reconfigures a Windows service.", "Создаёт, останавливает или перенастраивает службу Windows.")],
  [/(Stop-Computer|Restart-Computer|\bshutdown(\.exe)?\b|\blogoff(\.exe)?\b|SetSuspendState|LockWorkStation)/i,
    riskRule("high", "power", "power", "Shuts down, restarts, locks or logs off the PC.", "Выключает, перезагружает, блокирует ПК или завершает сеанс.")],
  [/(-Verb\s+RunAs\b|\brunas(\.exe)?\s)/i,
    riskRule("high", "elevate", "elevation", "Asks Windows for administrator rights.", "Запрашивает у Windows права администратора.")],
  [/Set-ExecutionPolicy\b(?![^\n]*-Scope\s+Process)/i,
    riskRule("high", "policy", "execution_policy", "Changes the PowerShell script execution policy.", "Меняет политику запуска сценариев PowerShell.")],
  [/\s-e(nc|ncodedcommand)?\s+[A-Za-z0-9+\/=]{16,}/i,
    riskRule("high", "obfuscation", "encoded_command", "Runs an encoded command whose text cannot be reviewed here.", "Запускает закодированную команду: её текст здесь не прочитать.")],
  [/(net(\.exe)?\s+user\b[^\n]*\/(add|delete)|New-LocalUser|Remove-LocalUser|Set-LocalUser|Disable-LocalUser)/i,
    riskRule("high", "accounts", "accounts", "Creates or changes Windows user accounts.", "Создаёт или меняет учётные записи Windows.")],
  [/drivers[\\/]+etc[\\/]+hosts\b/i,
    riskRule("high", "network", "hosts_file", "Touches the hosts file, which can redirect websites.", "Затрагивает файл hosts, через который можно подменять сайты.")],
  [/(New-NetFirewallRule|Set-NetFirewallRule|netsh(\.exe)?\s+(advfirewall|firewall)\s+(add|set)\b)/i,
    riskRule("high", "network", "firewall_rule", "Changes firewall rules.", "Меняет правила брандмауэра.")],
  [/(Enable-PSRemoting|\bwinrm\s|WSMan:|fDenyTSConnections|OpenSSH\.Server|Start-Service\s+sshd)/i,
    riskRule("high", "remote", "remote_access", "Turns on remote access to this PC.", "Включает удалённый доступ к этому ПК.")],
  [/(\btakeown(\.exe)?\b|\bicacls(\.exe)?\b[^\n]*\/(grant|setowner|reset)|Set-Acl\b)/i,
    riskRule("high", "acl", "permissions_change", "Changes file ownership or access rights.", "Меняет владельца файлов или права доступа к ним.")],
  [/(wevtutil(\.exe)?\s+cl\b|Clear-EventLog|Remove-EventLog|Clear-History)/i,
    riskRule("high", "traces", "clear_logs", "Clears Windows event logs or command history.", "Очищает журналы событий Windows или историю команд.")],
  [/([\\/]\.ssh[\\/]|\bid_(rsa|ed25519|ecdsa|dsa)\b|Login Data|[\\/]Cookies\b|Local State|\.kdbx\b|wallet\.dat|\.aws[\\/]credentials|\.kube[\\/]config|\.git-credentials|\.docker[\\/]config\.json|Microsoft[\\/](Credentials|Protect)\b|Telegram Desktop[\\/]tdata)/i,
    riskRule("high", "secrets", "secret_files", "Accesses stored keys, passwords or browser data.", "Обращается к ключам, паролям или данным браузера.")],
  [/(CopyFromScreen|\bBitBlt\b|PrintWindow)/i,
    riskRule("high", "spy", "screen_capture", "Captures the screen without the screenshot approval card.", "Делает снимок экрана в обход карточки подтверждения скриншота.")],
  [/(Stop-Process|taskkill(\.exe)?|\bkill\s)[^\n]*\b(explorer|svchost|winlogon|csrss|lsass|wininit|services|smss|dwm)\b/i,
    riskRule("high", "process", "process_system", "Stops critical Windows processes.", "Останавливает важные процессы Windows.")],
  [/(Stop-Process|taskkill(\.exe)?|\bkill\s)/i,
    riskRule("medium", "process", "process_kill", "Stops running programs.", "Останавливает запущенные программы.")],
  [/((Invoke-WebRequest|Invoke-RestMethod|\biwr\b|\birm\b)\b[^\n|;]*-Method\s+(Post|Put|Patch)|\bcurl(\.exe)?\b[^\n|;]*(\s-d\b|--data|\s-F\b|--form|--upload-file|\s-T\b)|Send-MailMessage|\b(scp|sftp|ftp)(\.exe)?\s)/i,
    riskRule("medium", "net", "upload", "Sends data from this PC over the network.", "Отправляет данные с этого ПК по сети.")],
  [/(Invoke-WebRequest|\biwr\b|\bcurl(\.exe)?\s|\bwget\s|Invoke-RestMethod|\birm\b|Start-BitsTransfer|DownloadFile|DownloadString)/i,
    riskRule("low", "net", "download", "Downloads data from the internet.", "Скачивает данные из интернета.")],
  [/(winget(\.exe)?\s+(install|upgrade|uninstall)|choco(\.exe)?\s+(install|upgrade|uninstall)|scoop\s+install|msiexec|Install-Module|Install-Package|Add-AppxPackage|npm(\.cmd)?\s+(i|install)\s[^\n]*(-g\b|--global)|pip3?(\.exe)?\s+install|Enable-WindowsOptionalFeature|\bdism(\.exe)?\s)/i,
    riskRule("medium", "install", "install", "Installs, updates or removes software.", "Устанавливает, обновляет или удаляет программы.")],
  [/(Get-Clipboard|Set-Clipboard|\bclip(\.exe)?\b|Clipboard\]::)/i,
    riskRule("medium", "privacy", "clipboard", "Reads or changes the clipboard.", "Читает или меняет буфер обмена.")],
  [/(\[Environment\]::SetEnvironmentVariable\([^\n)]*(Machine|User)|\bsetx(\.exe)?\s)/i,
    riskRule("medium", "env", "env_permanent", "Permanently changes environment variables.", "Навсегда меняет переменные среды.")],
  [/(\biex\b|Invoke-Expression|\[System\.Reflection\.Assembly\]::Load|Add-Type\b[^\n]*DllImport)/i,
    riskRule("medium", "dynamic", "dynamic_code", "Runs code generated on the fly.", "Выполняет код, собранный на лету.")],
];
// A delete aimed at a drive root, the Windows folder or a whole user profile.
const ROOTISH_DELETE = /\b(Remove-Item|rm|ri|del|erase|rd|rmdir)\b[^\n|;&]*?(\s|['"=])([A-Za-z]:\\?|[A-Za-z]:\\(Windows|Users|Program Files( \(x86\))?|ProgramData)(\\[^\\\s'"]+)?|\$env:(USERPROFILE|HOMEPATH|SystemRoot|windir|ProgramFiles|ProgramData|APPDATA|LOCALAPPDATA)|~)[\\\/]?\*?(?=['"]?(\s|$|;|\|))/i;
const SENSITIVE_READ = /([\\/]\.ssh[\\/](id_[^\\/]*|[^\\/]*_key|config)$|[\\/](Login Data|Cookies|Web Data|Local State)$|\.kdbx$|wallet\.dat$|[\\/]\.aws[\\/]credentials$|[\\/]\.kube[\\/]config$|[\\/]\.git-credentials$|[\\/]\.docker[\\/]config\.json$|[\\/]Microsoft[\\/](Credentials|Protect)[\\/]|Telegram Desktop[\\/]tdata)/i;

function assessWritePath(fullPath) {
  const out = [];
  const p = String(fullPath || "");
  if (/^[a-z]:\\(windows|program files( \(x86\))?)(\\|$)/i.test(p) || /^[a-z]:\\programdata\\microsoft\\/i.test(p))
    out.push(riskRule("critical", "location", "system_folder", "Writes into a Windows system or program folder.", "Пишет в системную папку Windows или Program Files."));
  if (/\\start menu\\programs\\startup\\/i.test(p))
    out.push(riskRule("high", "persist", "startup_folder", "Puts a file into the Startup folder (runs at every sign-in).", "Кладёт файл в автозагрузку (запускается при каждом входе)."));
  if (/\\(windowspowershell|powershell)\\[^\\]*profile\.ps1$/i.test(p))
    out.push(riskRule("high", "persist", "ps_profile", "Changes a PowerShell profile that runs in every new shell.", "Меняет профиль PowerShell, который выполняется в каждом новом окне."));
  if (/\\drivers\\etc\\hosts$/i.test(p))
    out.push(riskRule("high", "network", "hosts_file", "Changes the hosts file, which can redirect websites.", "Меняет файл hosts, через который можно подменять сайты."));
  if (/[\\/]\.ssh[\\/]/i.test(p))
    out.push(riskRule("high", "secrets", "ssh_dir", "Changes SSH keys or SSH settings.", "Меняет SSH-ключи или настройки SSH."));
  if (/[\\/]\.git[\\/]hooks[\\/]/i.test(p))
    out.push(riskRule("high", "persist", "git_hook", "Adds a Git hook that runs code on Git commands.", "Добавляет Git-хук, который запускает код при командах Git."));
  if (/\.(exe|dll|sys|scr|com|msi|lnk|reg)$/i.test(p))
    out.push(riskRule("high", "exec", "binary_file", "Creates or replaces a program, shortcut or registry file.", "Создаёт или заменяет программу, ярлык или файл реестра."));
  else if (/\.(bat|cmd|ps1|psm1|vbs|vbe|jse|wsf|hta)$/i.test(p))
    out.push(riskRule("medium", "exec", "script_file", "Creates or changes a script that can be run.", "Создаёт или меняет сценарий, который можно запустить."));
  if (p && !insideDir(canonicalLoose(PROJECT_DIR), canonicalLoose(p)))
    out.push(riskRule("medium", "location", "outside_project", "Outside the project folder.", "Вне папки проекта."));
  return out;
}
function assessWriteContent(fullPath, content) {
  const out = [];
  try {
    const st = fs.statSync(fullPath);
    if (st.isFile()) {
      const newBytes = Buffer.byteLength(String(content ?? ""), "utf8");
      const pct = st.size > 0 ? Math.round((newBytes / st.size) * 100) : 100;
      if (st.size > 0 && newBytes === 0) out.push(riskRule("high", "content", "erase_content", "Erases the whole content of an existing file.", "Стирает всё содержимое существующего файла."));
      else if (st.size > 4096 && newBytes < st.size * 0.2) out.push(riskRule("medium", "content", "shrink", "Replaces most of the file: the new version is " + pct + "% of the current size.", "Заменяет большую часть файла: новая версия — " + pct + "% от текущего размера."));
    }
  } catch {}
  if (/-----BEGIN [A-Z ]*PRIVATE KEY-----/.test(String(content || ""))) out.push(riskRule("medium", "secrets", "private_key", "Writes a private key to disk.", "Записывает на диск закрытый ключ."));
  return out;
}
function measureTree(root, capFiles) {
  let files = 0, bytes = 0;
  const stack = [root];
  while (stack.length && files < capFiles) {
    const dir = stack.pop();
    let items = [];
    try { items = fs.readdirSync(dir, { withFileTypes: true }); } catch { continue; }
    for (const it of items) {
      const abs = path.join(dir, it.name);
      if (it.isDirectory()) stack.push(abs);
      else { files++; try { bytes += fs.statSync(abs).size; } catch {} if (files >= capFiles) break; }
    }
  }
  return { files, bytes };
}
function assessFileOp(args, fullPath, toPath) {
  const out = [];
  if (args.op === "delete") {
    out.push(riskRule("medium", "delete", "trash_delete", "Deletes; the files go to ZeroIDE history and can be restored.", "Удаляет; файлы попадают в историю ZeroIDE, их можно восстановить."));
    try { if (fs.statSync(fullPath).isDirectory() && measureTree(fullPath, 501).files > 500) out.push(riskRule("high", "delete", "big_delete", "Deletes a folder with more than 500 files.", "Удаляет папку, в которой больше 500 файлов.")); } catch {}
  } else if ((args.op === "move" || args.op === "copy") && args.overwrite) {
    out.push(riskRule("medium", "overwrite", "overwrite", "May replace an existing file (its previous version is kept).", "Может заменить существующий файл (прежняя версия сохраняется)."));
  }
  if (args.op !== "copy") out.push(...assessWritePath(fullPath));
  if (toPath) out.push(...assessWritePath(toPath));
  return out;
}
function assessRisk(tool, args) {
  let rules = [];
  try {
    if (tool === "run_command") {
      const text = String(args.command || "");
      for (const [re, rule] of COMMAND_RISK_RULES) if (re.test(text)) rules.push(rule);
      if (ROOTISH_DELETE.test(text)) rules.push(riskRule("critical", "delete", "delete_root", "Deletes a whole drive, the Windows folder or a user profile.", "Удаляет целый диск, папку Windows или профиль пользователя."));
      if (TRAY_CONTROL_RE.test(text)) rules.push(riskRule("critical", "self", "zeroide_control", "Talks to the ZeroIDE tray control port, where approvals are decided.", "Обращается к управляющему порту трея ZeroIDE, где решаются подтверждения."));
      // The same secrets reached through the server folder (by path or as the working folder).
      const lower = text.toLowerCase();
      const cwdFull = typeof args.cwd === "string" && args.cwd ? resolvePath(args.cwd) : PROJECT_DIR;
      const selfDirs = [SERVER_DIR, INSTALL_ROOT].filter(Boolean);
      const nearSelf = selfDirs.some((d) => lower.includes(d.toLowerCase()) || insideDir(canonicalLoose(d), canonicalLoose(cwdFull)));
      if ((nearSelf && /config\.env|tokens\.json|oauth\.json|tray-config\.json|tunnel_key/i.test(text)) || /\b(MCP_TOKEN|TRAY_CONTROL_SECRET)\b/.test(text))
        rules.push(riskRule("critical", "self", "zeroide_self", "Touches ZeroIDE MCP's own secrets or approval channel.", "Затрагивает секреты или канал подтверждений самого ZeroIDE MCP."));
    } else if (tool === "write_file") {
      const full = resolvePath(args.path);
      rules = [...assessWritePath(full), ...assessWriteContent(full, args.content)];
    } else if (tool === "edit_file") {
      rules = assessWritePath(resolvePath(args.path));
    } else if (tool === "file_ops") {
      rules = assessFileOp(args, resolvePath(args.path), args.to ? resolvePath(args.to) : "");
    } else if (tool === "read_file" || tool === "read_image" || tool === "search_files") {
      const full = resolvePath(args.path);
      if (!/\.pub$/i.test(full) && SENSITIVE_READ.test(full)) rules.push(riskRule("high", "secrets", "secret_read", "Reads stored keys, passwords or browser data.", "Читает ключи, пароли или данные браузера."));
    }
  } catch {}
  // One reason per group, the strongest one: "deletes recursively" does not also say "deletes".
  const best = new Map();
  for (const r of rules) {
    const cur = best.get(r.group);
    if (!cur || RISK_ORDER[r.level] > RISK_ORDER[cur.level]) best.set(r.group, r);
  }
  const picked = [...best.values()].sort((a, b) => RISK_ORDER[b.level] - RISK_ORDER[a.level]);
  return { level: picked.length ? picked[0].level : "low", reasons: picked.slice(0, 6).map((r) => ({ code: r.code, level: r.level, text: tr(r.en, r.ru) })) };
}
function riskHeadline(level) {
  return level === "critical" ? tr("Critical risk: check every word", "Критический риск: проверьте каждое слово")
    : level === "high" ? tr("High risk: read before allowing", "Высокий риск: прочитайте перед разрешением")
    : level === "medium" ? tr("Worth a look", "Стоит проверить")
    : tr("Low risk", "Низкий риск");
}

// ---- Edits and change previews --------------------------------------------------
// Applies one edit (oldString/newString) or several (edits[]) in memory, all or nothing.
function prepareEdit(fullPath, args) {
  if (!fs.existsSync(fullPath)) return { ok: false, code: "not_found" };
  const buf = fs.readFileSync(fullPath);
  const text = buf.toString("utf8");
  const list = Array.isArray(args.edits) && args.edits.length
    ? args.edits
    : typeof args.oldString === "string" ? [{ oldString: args.oldString, newString: args.newString ?? "", replaceAll: args.replaceAll === true }] : [];
  if (!list.length) return { ok: false, code: "no_edits", buf, text };
  let updated = text;
  let replaced = 0;
  for (let i = 0; i < list.length; i++) {
    const e = list[i] || {};
    const oldS = String(e.oldString ?? "");
    const newS = String(e.newString ?? "");
    if (!oldS) return { ok: false, code: "empty_old", index: i, buf, text };
    const parts = updated.split(oldS);
    const n = parts.length - 1;
    if (n === 0) return { ok: false, code: "not_found_text", index: i, buf, text };
    if (n > 1 && e.replaceAll !== true) return { ok: false, code: "ambiguous", index: i, count: n, buf, text };
    updated = parts.join(newS);
    replaced += n;
  }
  return { ok: true, buf, text, updated, replaced, edits: list.length };
}
function editFailureResult(fullPath, prep, total) {
  const which = total > 1 ? tr(" (edit #", " (правка №") + ((prep.index ?? 0) + 1) + tr(" of ", " из ") + total + tr("; no edit was applied)", "; ни одна правка не применена)") : "";
  if (prep.code === "not_found") return textResult(tr("ERROR: file not found: ", "ERROR: файл не найден: ") + fullPath, true);
  if (prep.code === "no_edits") return textResult(tr("ERROR: pass oldString and newString, or a non-empty edits array.", "ERROR: передай oldString и newString или непустой массив edits."), true);
  if (prep.code === "empty_old") return textResult(tr("DENIED: oldString is empty", "ОТКАЗ: oldString пустой") + which + ".\npath: " + fullPath, true);
  if (prep.code === "ambiguous")
    return textResult(
      [
        tr("DENIED: oldString occurs ", "ОТКАЗ: oldString встречается ") + prep.count + tr(" times — edit cancelled to avoid changing the wrong place", " раз — правка отменена, чтобы не изменить не то место") + which + ".",
        "path: " + fullPath,
        tr("hint: widen oldString so the fragment becomes unique, or pass replaceAll: true for that edit.", "hint: расширь oldString так, чтобы фрагмент стал уникальным, или передай replaceAll: true для этой правки."),
      ].join("\n"),
      true
    );
  return textResult(
    [
      tr("DENIED: oldString not found in the file — nothing changed", "ОТКАЗ: oldString не найден в файле — ничего не изменено") + which + ".",
      "path: " + fullPath,
      tr("hint: check spaces, indents and line breaks; the text must match character for character.", "hint: проверь пробелы, отступы и переносы строк; текст должен совпадать посимвольно."),
    ].join("\n"),
    true
  );
}
function compactDiff(lines, ctx, maxLines) {
  const keep = new Array(lines.length).fill(false);
  lines.forEach((l, i) => {
    if (l[0] === "+" || l[0] === "-") for (let k = Math.max(0, i - ctx); k <= Math.min(lines.length - 1, i + ctx); k++) keep[k] = true;
  });
  const out = [];
  let gap = false;
  for (let i = 0; i < lines.length && out.length < maxLines; i++) {
    if (keep[i]) { out.push(lines[i]); gap = false; }
    else if (!gap) { out.push("…"); gap = true; }
  }
  return out.join("\n");
}
function diffSummary(oldText, newText, exists) {
  const diff = makeUnifiedDiff(oldText, newText, "file");
  if (!diff.startsWith("--- ")) return { exists, added: 0, removed: 0, text: diff };
  const lines = diff.split("\n").slice(2);
  let added = 0, removed = 0;
  for (const l of lines) { if (l[0] === "+") added++; else if (l[0] === "-") removed++; }
  return { exists, added, removed, text: clampText(compactDiff(lines, 2, 140), 8 * 1024).text };
}
// What the owner sees on the card for a write or an edit: +/- counts and the changed lines.
function buildChangePreview(tool, args) {
  try {
    const full = resolvePath(args.path);
    if (tool === "write_file") {
      let oldText = "", exists = false;
      try {
        const st = fs.statSync(full);
        if (st.isFile()) {
          exists = true;
          if (st.size > 2 * 1024 * 1024) return { exists, added: 0, removed: 0, text: tr("(file too large for a preview)", "(файл слишком большой для превью)") };
          oldText = fs.readFileSync(full, "utf8");
        }
      } catch {}
      return diffSummary(oldText, String(args.content ?? ""), exists);
    }
    if (tool === "edit_file") {
      const prep = prepareEdit(full, args);
      return prep.ok ? diffSummary(prep.text, prep.updated, true) : null;
    }
  } catch {}
  return null;
}

// ---- File history (undo) -----------------------------------------------------------
// The previous bytes of every file an agent overwrites, edits, replaces or deletes are
// kept in HISTORY_DIR (%LOCALAPPDATA%\ZeroIDE-MCP\history by default) instead of a .bak
// next to the file: projects stay clean and the owner restores any version from
// Settings -> Changes. index.jsonl is written by the server only; the tray records its own
// restores in owner.jsonl. MCP_BACKUP_MODE=sidecar|both brings back the .bak copies.
function historyNewId() {
  return "chg_" + Date.now().toString(36) + crypto.randomBytes(3).toString("hex");
}
function historyDay() {
  return new Date().toISOString().slice(0, 10);
}
function historyKeepCopy(fullPath, id) {
  try {
    const st = fs.statSync(fullPath);
    if (!st.isFile() || st.size > HISTORY_MAX_FILE_BYTES) return null;
    const dir = path.join(HISTORY_DIR, historyDay());
    fs.mkdirSync(dir, { recursive: true });
    const safe = path.basename(fullPath).replace(/[<>:"/\\|?*\u0000-\u001f]/g, "_").slice(-90);
    const dest = path.join(dir, id + "__" + safe);
    fs.copyFileSync(fullPath, dest);
    return dest;
  } catch {
    return null;
  }
}
// Keeps the current bytes of fullPath (when it exists) before a change.
function keepBeforeChange(fullPath, wantBackup) {
  const id = historyNewId();
  if (wantBackup === false || !fs.existsSync(fullPath)) return { id, backup: null, sidecar: null };
  const backup = BACKUP_MODE === "sidecar" ? null : historyKeepCopy(fullPath, id);
  let sidecar = null;
  if (BACKUP_MODE !== "history") {
    try {
      sidecar = fullPath + ".bak." + new Date().toISOString().replace(/[:.]/g, "").slice(0, 15);
      fs.copyFileSync(fullPath, sidecar);
    } catch { sidecar = null; }
  }
  return { id, backup, sidecar };
}
let historyPruneScheduled = false;
function historyAppend(entry) {
  try {
    fs.mkdirSync(HISTORY_DIR, { recursive: true });
    fs.appendFileSync(HISTORY_INDEX, JSON.stringify({ v: 1, ts: new Date().toISOString(), ...entry }) + "\n", "utf8");
  } catch {}
  if (!historyPruneScheduled) {
    historyPruneScheduled = true;
    const t = setTimeout(pruneHistory, 15000);
    if (t.unref) t.unref();
  }
}
function pruneHistory() {
  try {
    const cutoff = Date.now() - HISTORY_RETENTION_DAYS * 86400000;
    const cutDay = new Date(cutoff).toISOString().slice(0, 10);
    for (const base of [HISTORY_DIR, path.join(HISTORY_DIR, "trash")]) {
      let names = [];
      try { names = fs.readdirSync(base); } catch {}
      for (const name of names) {
        if (/^\d{4}-\d{2}-\d{2}$/.test(name) && name < cutDay) {
          try { fs.rmSync(path.join(base, name), { recursive: true, force: true }); } catch {}
        }
      }
    }
    if (fs.existsSync(HISTORY_INDEX)) {
      const lines = fs.readFileSync(HISTORY_INDEX, "utf8").split("\n").filter(Boolean);
      const kept = lines.filter((l) => { try { return Date.parse(JSON.parse(l).ts) >= cutoff; } catch { return false; } });
      if (kept.length < lines.length) {
        const tmp = HISTORY_INDEX + ".tmp-" + process.pid;
        fs.writeFileSync(tmp, kept.join("\n") + (kept.length ? "\n" : ""), "utf8");
        fs.renameSync(tmp, HISTORY_INDEX);
      }
    }
  } catch {}
}
function movePath(src, dst) {
  try {
    fs.renameSync(src, dst);
  } catch (e) {
    if (!e || e.code !== "EXDEV") throw e;
    fs.cpSync(src, dst, { recursive: true, force: false, errorOnExist: true, preserveTimestamps: true });
    fs.rmSync(src, { recursive: true, force: true });
  }
}
// Places file_ops never deletes, even into history.
function deleteGuard(fullPath) {
  const c = canonicalLoose(fullPath);
  const home = canonicalLoose(os.homedir());
  const forbidden = [home, canonicalLoose(PROJECT_DIR)];
  for (const name of ["Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos", "AppData", "OneDrive"]) forbidden.push(canonicalLoose(path.join(os.homedir(), name)));
  for (const v of [process.env.SystemRoot, process.env.ProgramFiles, process.env["ProgramFiles(x86)"], process.env.ProgramData]) if (v) forbidden.push(canonicalLoose(v));
  if (/^[a-z]:\\?$/i.test(path.resolve(fullPath)) || path.parse(path.resolve(fullPath)).root === path.resolve(fullPath)) return tr("REFUSED: file_ops never deletes a whole drive.", "ОТКАЗ: file_ops никогда не удаляет диск целиком.");
  if (forbidden.includes(c)) return tr("REFUSED: this folder is too important to delete through file_ops (profile, a main user folder, Windows, Program Files or the project root). Delete the items inside instead.", "ОТКАЗ: эту папку нельзя удалить через file_ops (профиль, основная папка пользователя, Windows, Program Files или корень проекта). Удаляй содержимое по отдельности.");
  return null;
}
// Cheap checks that make file_ops fail anyway: answered before an approval card is shown,
// so the owner is never asked about something that cannot happen.
function fileOpsPrecheck(args) {
  try {
    const op = args.op;
    if (op === "mkdir") return null;
    const src = resolvePath(args.path);
    const head = (msg) => [msg, "op: " + op, "path: " + src].join("\n");
    if (!fs.existsSync(src)) return head(tr("ERROR: path not found.", "ОШИБКА: путь не найден."));
    if (op === "delete" || op === "move") { const g = deleteGuard(src); if (g) return head(g); }
    const isDir = fs.statSync(src).isDirectory();
    if ((op === "delete" || op === "copy") && isDir && !args.recursive) return head(tr("ERROR: this is a folder; pass recursive: true.", "ОШИБКА: это папка; передай recursive: true."));
    if ((op === "copy" || op === "move") && !args.to) return head(tr("ERROR: pass `to` with the destination path.", "ОШИБКА: передай `to` — путь назначения."));
    if ((op === "copy" || op === "move") && fs.existsSync(resolvePath(args.to)) && !args.overwrite) return head(tr("DENIED: the destination already exists — nothing changed. Pass overwrite: true to replace it.", "ОТКАЗ: назначение уже существует — ничего не изменено. Передай overwrite: true, чтобы заменить."));
  } catch {}
  return null;
}

// ---- Images ---------------------------------------------------------------------------
const IMAGE_PASSTHROUGH_BYTES = 1536 * 1024;
const IMAGE_MAX_SOURCE_BYTES = 64 * 1024 * 1024;
function sniffImage(buf) {
  if (buf.length >= 24 && buf[0] === 0x89 && buf[1] === 0x50 && buf[2] === 0x4e && buf[3] === 0x47) return { type: "png", mime: "image/png", width: buf.readUInt32BE(16), height: buf.readUInt32BE(20) };
  if (buf.length >= 4 && buf[0] === 0xff && buf[1] === 0xd8 && buf[2] === 0xff) return { type: "jpeg", mime: "image/jpeg", ...jpegSize(buf) };
  if (buf.length >= 10 && buf.toString("ascii", 0, 3) === "GIF") return { type: "gif", mime: "image/gif", width: buf.readUInt16LE(6), height: buf.readUInt16LE(8) };
  if (buf.length >= 12 && buf.toString("ascii", 0, 4) === "RIFF" && buf.toString("ascii", 8, 12) === "WEBP") return { type: "webp", mime: "image/webp", width: 0, height: 0 };
  if (buf.length >= 26 && buf[0] === 0x42 && buf[1] === 0x4d) return { type: "bmp", mime: "image/bmp", width: buf.readInt32LE(18), height: Math.abs(buf.readInt32LE(22)) };
  if (buf.length >= 8 && buf[0] === 0 && buf[1] === 0 && buf[2] === 1 && buf[3] === 0) return { type: "ico", mime: "image/x-icon", width: buf[6] || 256, height: buf[7] || 256 };
  return null;
}
function jpegSize(buf) {
  let i = 2;
  while (i + 9 < buf.length) {
    if (buf[i] !== 0xff) { i++; continue; }
    const marker = buf[i + 1];
    if (marker === 0xd8 || marker === 0x01 || marker === 0xff || (marker >= 0xd0 && marker <= 0xd7)) { i += marker === 0xff ? 1 : 2; continue; }
    const len = buf.readUInt16BE(i + 2);
    if (marker >= 0xc0 && marker <= 0xcf && marker !== 0xc4 && marker !== 0xc8 && marker !== 0xcc) return { width: buf.readUInt16BE(i + 7), height: buf.readUInt16BE(i + 5) };
    i += 2 + len;
  }
  return { width: 0, height: 0 };
}
// Windows: decode, downscale and re-encode with System.Drawing in a child PowerShell.
// The path travels in an environment variable, so quotes or Cyrillic never break it.
function psConvertImage(fullPath, maxSize, fmt) {
  return new Promise((resolve) => {
    const script = [
      "$ErrorActionPreference = 'Stop'",
      "Add-Type -AssemblyName System.Drawing",
      "$src = [System.Drawing.Image]::FromFile($env:ZIDE_IMG)",
      "try {",
      "  $max = [int]$env:ZIDE_MAX",
      "  $scale = [Math]::Min(1.0, $max / [double][Math]::Max($src.Width, $src.Height))",
      "  $w = [Math]::Max(1, [int][Math]::Round($src.Width * $scale)); $h = [Math]::Max(1, [int][Math]::Round($src.Height * $scale))",
      "  $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)",
      "  $g = [System.Drawing.Graphics]::FromImage($bmp)",
      "  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic",
      "  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality",
      "  if ($env:ZIDE_FMT -eq 'jpeg') { $g.Clear([System.Drawing.Color]::White) }",
      "  $g.DrawImage($src, 0, 0, $w, $h); $g.Dispose()",
      "  $ms = New-Object System.IO.MemoryStream",
      "  if ($env:ZIDE_FMT -eq 'jpeg') {",
      "    $enc = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }",
      "    $ep = New-Object System.Drawing.Imaging.EncoderParameters(1)",
      "    $ep.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter([System.Drawing.Imaging.Encoder]::Quality, [long]85)",
      "    $bmp.Save($ms, $enc, $ep)",
      "  } else { $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png) }",
      "  $bmp.Dispose()",
      "  [Console]::Out.Write(('{0}x{1}|{2}x{3}|' -f $src.Width, $src.Height, $w, $h) + [Convert]::ToBase64String($ms.ToArray()))",
      "} finally { $src.Dispose() }",
    ].join("\n");
    let out = "";
    let err = "";
    let done = false;
    const child = spawn("powershell", ["-NoProfile", "-NonInteractive", "-Command", script], {
      windowsHide: true,
      env: { ...process.env, ZIDE_IMG: fullPath, ZIDE_MAX: String(maxSize), ZIDE_FMT: fmt },
    });
    const finish = (val) => { if (done) return; done = true; clearTimeout(timer); resolve(val); };
    const timer = setTimeout(() => { try { child.kill(); } catch {} finish({ ok: false, error: "image_timeout" }); }, 30000);
    child.stdout.on("data", (d) => { out += d.toString("utf8"); });
    child.stderr.on("data", (d) => { err += d.toString("utf8"); });
    child.on("error", (e) => finish({ ok: false, error: String((e && e.message) || e) }));
    child.on("close", (code) => {
      const m = out.trim().match(/^(\d+)x(\d+)\|(\d+)x(\d+)\|([A-Za-z0-9+/=]+)$/);
      if (code !== 0 || !m) { finish({ ok: false, error: (err.trim().split(/\r?\n/)[0] || "image_failed").slice(0, 200) }); return; }
      finish({ ok: true, srcW: +m[1], srcH: +m[2], w: +m[3], h: +m[4], base64: m[5], mime: fmt === "jpeg" ? "image/jpeg" : "image/png" });
    });
  });
}

// ---- System info ------------------------------------------------------------------
let systemInfoCache = null;
function runProbe(file, args, useShell) {
  return new Promise((resolve) => {
    let out = "";
    let child;
    try { child = spawn(file, args, { shell: !!useShell, windowsHide: true }); } catch { resolve(""); return; }
    let done = false;
    const finish = (v) => { if (done) return; done = true; clearTimeout(timer); resolve(v); };
    const timer = setTimeout(() => { try { killTree(child); } catch {} finish(""); }, 6000);
    child.stdout.on("data", (d) => { out += d.toString("utf8"); });
    child.stderr.on("data", (d) => { out += d.toString("utf8"); });
    child.on("error", () => finish(""));
    child.on("close", (code) => finish(code === 0 || /version|\d+\.\d+/i.test(out) && !/not (recognized|found)|не является|не найден/i.test(out) ? out.trim() : ""));
  });
}
async function collectSystemFacts() {
  if (systemInfoCache && Date.now() - systemInfoCache.at < 10 * 60000) return systemInfoCache.facts;
  const probes = IS_WIN
    ? [["node", "node --version"], ["npm", "npm --version"], ["python", "python --version"], ["git", "git --version"], ["dotnet", "dotnet --list-sdks"], ["java", "java -version"], ["go", "go version"], ["rustc", "rustc --version"], ["docker", "docker --version"], ["winget", "winget --version"], ["pwsh", "pwsh -NoLogo -NoProfile -Command $PSVersionTable.PSVersion.ToString()"]]
    : [["node", "node --version"], ["npm", "npm --version"], ["python3", "python3 --version"], ["git", "git --version"], ["dotnet", "dotnet --list-sdks"], ["docker", "docker --version"]];
  const winScript = [
    "$o = Get-CimInstance Win32_OperatingSystem",
    "Add-Type -AssemblyName System.Windows.Forms",
    "$d = [System.Windows.Forms.Screen]::AllScreens | ForEach-Object { '{0}x{1}{2}' -f $_.Bounds.Width, $_.Bounds.Height, $(if ($_.Primary) { ' (primary)' } else { '' }) }",
    "[Console]::Out.Write((@{ os = $o.Caption; build = $o.BuildNumber; ps = $PSVersionTable.PSVersion.ToString(); displays = @($d) } | ConvertTo-Json -Compress))",
  ].join("; ");
  const [toolOut, winOut] = await Promise.all([
    Promise.all(probes.map(async ([name, cmd]) => [name, await runProbe(cmd, [], true)])),
    IS_WIN ? runProbe("powershell", ["-NoProfile", "-NonInteractive", "-Command", winScript], false) : Promise.resolve(""),
  ]);
  let win = {};
  try { win = JSON.parse(winOut || "{}"); } catch {}
  const tools = toolOut
    .filter(([, v]) => v)
    .map(([name, v]) => [name, name === "dotnet" ? v.split(/\r?\n/).map((l) => l.split(" ")[0]).filter(Boolean).join(", ") : v.split(/\r?\n/)[0].replace(/^(git version|go version|docker version|python|java version|openjdk version|rustc)\s*/i, "").replace(/"/g, "").trim()]);
  const disks = [];
  if (IS_WIN && typeof fs.statfsSync === "function") {
    for (const letter of "CDEFGHIJKLMNOPQRSTUVWXYZ") {
      const root = letter + ":\\";
      try {
        if (!fs.existsSync(root)) continue;
        const s = fs.statfsSync(root);
        disks.push({ root, total: s.blocks * s.bsize, free: s.bavail * s.bsize });
      } catch {}
    }
  }
  const facts = {
    os: win.os ? win.os + (win.build ? " (build " + win.build + ")" : "") : os.type() + " " + os.release(),
    arch: os.arch(),
    cpu: (os.cpus()[0]?.model || "?").replace(/\s+/g, " ").trim() + " × " + os.cpus().length,
    memTotal: os.totalmem(),
    memFree: os.freemem(),
    uptimeSec: os.uptime(),
    host: os.hostname(),
    user: (() => { try { return os.userInfo().username; } catch { return ""; } })(),
    timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone || "",
    locale: Intl.DateTimeFormat().resolvedOptions().locale || "",
    powershell: win.ps || "",
    displays: Array.isArray(win.displays) ? win.displays : win.displays ? [win.displays] : [],
    disks,
    tools,
  };
  systemInfoCache = { at: Date.now(), facts };
  return facts;
}
const fmtBytes = (n) => (n >= 1073741824 ? (n / 1073741824).toFixed(1) + " GB" : n >= 1048576 ? (n / 1048576).toFixed(1) + " MB" : n >= 1024 ? (n / 1024).toFixed(1) + " KB" : n + " B");

// ---- user_message open link ------------------------------------------------------
const REVEAL_ONLY_EXT = /\.(exe|com|bat|cmd|ps1|psm1|vbs|vbe|js|jse|wsf|wsh|hta|msi|msp|scr|lnk|reg|cpl|jar|appref-ms|url|pif|application|gadget|msc)$/i;
function resolveOpenTarget(raw, extra) {
  const value = String(raw || "").trim();
  if (!value) return null;
  if (/^https?:\/\//i.test(value)) return value.length <= 2048 ? { kind: "url", target: value, reveal: false } : { error: tr("URL is longer than 2048 characters", "URL длиннее 2048 символов") };
  if (/^[a-z][a-z0-9+.-]*:\/\//i.test(value)) return { error: tr("only http(s) links and local paths can be opened", "открывать можно только ссылки http(s) и локальные пути") };
  const conf = extra?.confinement;
  let full = resolvePath(value);
  if (conf && conf.root) {
    const chk = confineTargetPath(conf.root, value);
    if (!chk.ok) return { error: tr("the path is outside the allowed folder", "путь вне разрешённой папки") };
    full = chk.resolved;
  }
  if (selfProtection(full, "read") === "secret") return { error: tr("protected ZeroIDE file", "защищённый файл ZeroIDE") };
  let st;
  try { st = fs.statSync(full); } catch { return { error: tr("path not found", "путь не найден") }; }
  const isDir = st.isDirectory();
  return { kind: isDir ? "folder" : "file", target: full, reveal: !isDir && REVEAL_ONLY_EXT.test(full) };
}

/* ---------------------- permissions & approvals (v0.2) ------------------- */

const DEFAULT_PERMISSIONS = {
  version: 1,
  defaultMode: "ask",
  approvalTimeoutSec: 60,
  tools: {
    read_file: { mode: "allow" },
    list_directory: { mode: "allow" },
    search_files: { mode: "allow" },
    job_status: { mode: "allow" },
    job_output: { mode: "allow" },
    audit_log: { mode: "allow" },
    journal_log: { mode: "allow" },
    journal_entry: { mode: "allow" },
    agent_message: { mode: "allow" },
    await_owner: { mode: "deny" },
    system_info: { mode: "allow" },
    read_image: { mode: "ask" },
    write_file: { mode: "ask" },
    edit_file: { mode: "ask" },
    file_ops: { mode: "ask" },
    run_command: { mode: "ask" },
  },
  // v0.5: what the risk assessment may change. askAtOrAbove turns an "allow" rule into
  // a prompt for calls at that level; grantsNeverCover keeps tool-wide temporary grants
  // from passing calls at that level; sensitiveReads asks before reading keys/passwords.
  riskGate: {
    enabled: true,
    askAtOrAbove: "high",
    grantsNeverCover: "critical",
    sensitiveReads: true,
  },
  redFlags: {
    enabled: true,
    elevateToAsk: true,
    patterns: [
      "(?i)\\b(Remove-Item|ri|rm|del|erase)\\b.*(-Recurse|-Force|/s|/q)",
      "(?i)\\b(Format-Volume|Clear-Disk|Initialize-Disk|diskpart|cipher\\s+/w)\\b",
      "(?i)\\b(Stop-Computer|Restart-Computer|shutdown|logoff)\\b",
      "(?i)\\b(reg\\s+delete|Remove-ItemProperty)\\b.*HKLM",
      "(?i)\\b(Invoke-Expression|iex)\\b.*(DownloadString|Invoke-WebRequest|curl|iwr)",
      "(?i)\\b(Set-MpPreference|Add-MpPreference).*Disable",
      "(?i)\\b(net\\s+user|New-LocalUser|Add-LocalGroupMember)\\b",
    ],
  },
};

let cachedPermissions = null;
let cachedPermissionsMtimeMs = -1;

function loadPermissions() {
  try {
    if (!fs.existsSync(PERMISSIONS_FILE)) {
      fs.mkdirSync(path.dirname(PERMISSIONS_FILE), { recursive: true });
      const tmp = PERMISSIONS_FILE + ".tmp-" + process.pid;
      fs.writeFileSync(tmp, JSON.stringify(DEFAULT_PERMISSIONS, null, 2), "utf8");
      fs.renameSync(tmp, PERMISSIONS_FILE);
    }
    const st = fs.statSync(PERMISSIONS_FILE);
    if (cachedPermissions && st.mtimeMs === cachedPermissionsMtimeMs) {
      return cachedPermissions;
    }
    const parsed = JSON.parse(fs.readFileSync(PERMISSIONS_FILE, "utf8").replace(/^\uFEFF/, ""));
    cachedPermissions = {
      ...DEFAULT_PERMISSIONS,
      ...parsed,
      tools: { ...DEFAULT_PERMISSIONS.tools, ...(parsed.tools || {}) },
      redFlags: { ...DEFAULT_PERMISSIONS.redFlags, ...(parsed.redFlags || {}) },
      riskGate: { ...DEFAULT_PERMISSIONS.riskGate, ...(parsed.riskGate || {}) },
    };
    cachedPermissionsMtimeMs = st.mtimeMs;
    return cachedPermissions;
  } catch {
    return cachedPermissions || DEFAULT_PERMISSIONS;
  }
}

/* ------------------- token registry & confinement (v0.4) ------------------ */

let cachedTokens = null;
let cachedTokensMtimeMs = -1;

function loadTokens() {
  try {
    if (!fs.existsSync(TOKENS_FILE)) {
      fs.mkdirSync(path.dirname(TOKENS_FILE), { recursive: true });
      const tmp = TOKENS_FILE + ".tmp-" + process.pid;
      fs.writeFileSync(tmp, JSON.stringify({ version: 1, tokens: [] }, null, 2), "utf8");
      fs.renameSync(tmp, TOKENS_FILE);
    }
    const st = fs.statSync(TOKENS_FILE);
    if (cachedTokens && st.mtimeMs === cachedTokensMtimeMs) return cachedTokens;
    const parsed = JSON.parse(fs.readFileSync(TOKENS_FILE, "utf8").replace(/^\uFEFF/, ""));
    const list = Array.isArray(parsed.tokens) ? parsed.tokens : [];
    cachedTokens = { version: 1, tokens: list.filter((t) => t && typeof t.hash === "string") };
    cachedTokensMtimeMs = st.mtimeMs;
    return cachedTokens;
  } catch {
    return cachedTokens || { version: 1, tokens: [] };
  }
}

function saveTokens(reg) {
  try {
    const tmp = TOKENS_FILE + ".tmp-" + process.pid;
    fs.writeFileSync(tmp, JSON.stringify(reg, null, 2), "utf8");
    fs.renameSync(tmp, TOKENS_FILE);
    cachedTokens = reg;
    try { cachedTokensMtimeMs = fs.statSync(TOKENS_FILE).mtimeMs; } catch {}
  } catch {}
}

function findTokenEntry(presented) {
  if (!presented) return null;
  const h = sha256(Buffer.from(String(presented), "utf8"));
  return loadTokens().tokens.find((t) => t.hash === h) || null;
}

// null = usable; otherwise an agent-facing error code.
function tokenEntryState(entry) {
  if (!entry) return "UNKNOWN_TOKEN";
  if (entry.revoked) return "TOKEN_REVOKED";
  if (entry.expiresAt && Date.now() > Date.parse(entry.expiresAt)) return "TOKEN_EXPIRED";
  return null;
}

function bumpTokenUsage(entry) {
  if (!entry) return;
  entry.usedRequests = (Number(entry.usedRequests) || 0) + 1;
  entry.lastUsedAt = new Date().toISOString();
  if (cachedTokens && cachedTokens.tokens.includes(entry)) saveTokens(cachedTokens);
}

const LEGACY_CALLER = { id: "legacy", label: "legacy", scope: "full", root: null, source: "config", legacy: true, entry: null };

function callerFromEntry(entry) {
  return {
    id: entry.id || "token",
    label: entry.label || "token",
    scope: entry.scope || "full",
    root: entry.root || null,
    source: entry.source || "manual",
    legacy: false,
    entry,
  };
}

// Human hints for 401 answers (they never contain secrets).
const AUTH_ERROR_HINTS = {
  NO_TOKEN: "Send Authorization: Bearer <token>, or connect with OAuth (see the WWW-Authenticate header).",
  QUERY_TOKEN_DISABLED: "?token= in the URL is disabled. Send the token in the Authorization: Bearer header.",
  NO_CREDENTIALS_CONFIGURED: "This server has no credentials configured and OAuth is off, so every request is rejected. The owner must add a token or enable OAuth.",
};

// True when at least one way to authenticate exists: the legacy config.env
// token, an active registry token (named or OAuth-issued), or OAuth itself.
// Only picks an honest error code; it never grants access.
function credentialsConfigured() {
  if (TOKEN || OAUTH_ENABLED) return true;
  return loadTokens().tokens.some((t) => !tokenEntryState(t));
}

// Query-string tokens are never logged with their value, and at most one audit
// line per minute is written, so a misconfigured client cannot flood the log.
let lastQueryTokenAuditAt = 0;
function noteQueryToken(req, accepted) {
  const now = Date.now();
  if (now - lastQueryTokenAuditAt < 60000) return;
  lastQueryTokenAuditAt = now;
  try {
    audit({
      event: accepted ? "auth.query_token_used" : "auth.query_token_rejected",
      path: req.path,
      hint: accepted
        ? "deprecated: move the client to the Authorization: Bearer header, then remove MCP_ALLOW_QUERY_TOKEN"
        : "?token= is disabled: send the token in the Authorization: Bearer header or use OAuth",
      ok: accepted,
    });
  } catch {}
}

function verifyPresentedToken(presented) {
  if (!presented) return { error: credentialsConfigured() ? "NO_TOKEN" : "NO_CREDENTIALS_CONFIGURED" };
  if (TOKEN && safeEqual(presented, TOKEN)) return { caller: LEGACY_CALLER };
  const entry = findTokenEntry(presented);
  if (!entry) return { error: "UNKNOWN_TOKEN" };
  const state = tokenEntryState(entry);
  if (state) return { error: state };
  return { caller: callerFromEntry(entry) };
}

// Resolves the caller for an HTTP request from its Bearer token.
// FAIL-CLOSED (audit 2026-11-10): only a credential that verifies grants
// access. "Nothing configured" is a configuration error, never an open mode.
// The legacy config.env token stays valid and maps to a full-access caller;
// registry tokens (named and OAuth-issued) carry their own scope/root/limits.
function authenticateRequest(req) {
  const auth = String((req.headers && req.headers.authorization) || "").trim();
  let presented = "";
  let via = "header";
  if (auth.slice(0, 7).toLowerCase() === "bearer ") {
    presented = auth.slice(7).trim();
  } else if (req.query && req.query.token !== undefined) {
    if (!ALLOW_QUERY_TOKEN || typeof req.query.token !== "string") {
      noteQueryToken(req, false);
      return { error: "QUERY_TOKEN_DISABLED" };
    }
    presented = req.query.token;
    via = "query";
  }
  const result = verifyPresentedToken(presented);
  if (via === "query") noteQueryToken(req, !result.error);
  if (result.caller) result.via = via;
  return result;
}

function foldCase(s) { return IS_WIN ? String(s).toLowerCase() : String(s); }

function canonicalRoot(p) {
  if (!p) return null;
  try { return fs.realpathSync(path.resolve(String(p))); } catch { return null; }
}

function isInsideRoot(rootCanon, targetCanon) {
  const r = foldCase(rootCanon);
  const t = foldCase(targetCanon);
  if (t === r) return true;
  const prefix = r.endsWith(path.sep) ? r : r + path.sep;
  return t.startsWith(prefix);
}

// Combines the global confinement (permissions.json) with the token's own
// root. Both set: the token root must live inside the global one.
function effectiveConfinement(perms, caller) {
  const g = perms && perms.confinement && perms.confinement.enabled ? perms.confinement.root : null;
  const t = caller ? caller.root : null;
  if (!g && !t) return { root: null, misconfigured: false };
  const gCanon = g ? canonicalRoot(g) : null;
  if (g && !gCanon) return { root: null, misconfigured: true };
  const tCanon = t ? canonicalRoot(t) : null;
  if (t && !tCanon) return { root: null, misconfigured: true };
  if (gCanon && tCanon) {
    if (!isInsideRoot(gCanon, tCanon)) return { root: null, misconfigured: true };
    return { root: tCanon, misconfigured: false };
  }
  return { root: gCanon || tCanon, misconfigured: false };
}

// Canonicalizes a requested path against the confinement root and verifies
// containment. Returns { ok: true, resolved } or { ok: false }.
function confineTargetPath(rootCanon, rawPath) {
  let p = rawPath == null || rawPath === "" ? rootCanon : String(rawPath);
  if (p.startsWith("\\\\") || p.startsWith("//")) return { ok: false }; // UNC
  const afterDrive = /^[a-zA-Z]:/.test(p) ? p.slice(2) : p;
  if (afterDrive.includes(":")) return { ok: false }; // ADS / stray colon
  const resolved = path.isAbsolute(p) ? path.resolve(p) : path.resolve(rootCanon, p);
  // Reject trailing dot/space segments (Windows silently strips them, which
  // would make the checked path differ from the opened one).
  for (const seg of resolved.split(/[\\/]+/)) {
    if (seg.length > 1 && (seg.endsWith(" ") || seg.endsWith(".")) && !/^[a-zA-Z]:$/.test(seg)) {
      if (seg !== ".." && seg !== ".") return { ok: false };
    }
  }
  let canon;
  try {
    if (fs.existsSync(resolved)) {
      canon = fs.realpathSync(resolved);
    } else {
      // New file: canonicalize the nearest existing ancestor, append the rest.
      let dir = path.dirname(resolved);
      const rest = [path.basename(resolved)];
      while (!fs.existsSync(dir)) {
        rest.unshift(path.basename(dir));
        const parent = path.dirname(dir);
        if (parent === dir) return { ok: false };
        dir = parent;
      }
      canon = path.join(fs.realpathSync(dir), ...rest);
    }
  } catch { return { ok: false }; }
  if (!isInsideRoot(rootCanon, canon)) return { ok: false };
  return { ok: true, resolved: canon };
}

/* ------------------------------ OAuth 2.1 (v0.5) ------------------------------ */
// The MCP server doubles as its own authorization server. OAuth is an
// issuance method on top of the token registry: access tokens are ordinary
// registry entries (source: "oauth", 1 h), refresh tokens live in oauth.json
// as rotating families with reuse detection. Consent is NOT a web form: the
// authorize page waits while the owner decides on a tray card
// (kind: "oauth_consent") — the system has no password anywhere.

let cachedOAuth = null;
let cachedOAuthMtimeMs = -1;

function loadOAuth() {
  try {
    if (!fs.existsSync(OAUTH_FILE)) {
      fs.mkdirSync(path.dirname(OAUTH_FILE), { recursive: true });
      const tmp = OAUTH_FILE + ".tmp-" + process.pid;
      fs.writeFileSync(tmp, JSON.stringify({ version: 1, clients: [], families: [] }, null, 2), "utf8");
      fs.renameSync(tmp, OAUTH_FILE);
    }
    const st = fs.statSync(OAUTH_FILE);
    if (cachedOAuth && st.mtimeMs === cachedOAuthMtimeMs) return cachedOAuth;
    const parsed = JSON.parse(fs.readFileSync(OAUTH_FILE, "utf8").replace(/^\uFEFF/, ""));
    cachedOAuth = {
      version: 1,
      clients: Array.isArray(parsed.clients) ? parsed.clients : [],
      families: Array.isArray(parsed.families) ? parsed.families : [],
    };
    cachedOAuthMtimeMs = st.mtimeMs;
    return cachedOAuth;
  } catch {
    return cachedOAuth || { version: 1, clients: [], families: [] };
  }
}

function saveOAuth(store) {
  try {
    const tmp = OAUTH_FILE + ".tmp-" + process.pid;
    fs.writeFileSync(tmp, JSON.stringify(store, null, 2), "utf8");
    fs.renameSync(tmp, OAUTH_FILE);
    cachedOAuth = store;
    try { cachedOAuthMtimeMs = fs.statSync(OAUTH_FILE).mtimeMs; } catch {}
  } catch {}
}

const OAUTH_SCOPES = { "mcp.read": "read", "mcp.readwrite": "readwrite", "mcp.full": "full" };
const SCOPE_HUMAN_RU = { read: tr("read-only", "только чтение"), readwrite: tr("read and write, no commands", "чтение и запись, без команд"), full: tr("full access", "полный доступ") };

function issuerOf(req) {
  if (process.env.MCP_OAUTH_ISSUER) return process.env.MCP_OAUTH_ISSUER.replace(/\/+$/, "");
  return req.protocol + "://" + req.get("host");
}

function genSecret(prefix, bytes) {
  return prefix + crypto.randomBytes(bytes).toString("base64url");
}

function hashSecret(s) {
  return sha256(Buffer.from(String(s), "utf8"));
}

function escapeHtml(s) {
  return String(s == null ? "" : s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

// Pending authorization codes: in-memory, one-time, 60 s TTL (per spec).
const pendingCodes = new Map(); // codeHash -> { clientId, redirectUri, challenge, scope, familyExpiresAt, createdAt }
// Pending consents the browser page polls: id -> { status, redirect?, error? }
const pendingConsents = new Map();
// Dedupe for concurrent identical authorizations: key -> { primaryId, attached[] }.
// When a client sends authorize twice (same client/scope/redirect) while the first
// consent card is still pending, the second request attaches to the first decision
// instead of showing a second card. Concurrent-only: decided primaries are not reused.
const pendingConsentByKey = new Map();

function pruneOAuthState() {
  const now = Date.now();
  for (const [k, v] of pendingCodes) if (now - v.createdAt > 90000) pendingCodes.delete(k);
  for (const [k, v] of pendingConsents) if (now - v.createdAt > 300000) pendingConsents.delete(k);
  // A dedupe slot whose primary consent is gone or decided no longer holds a card.
  for (const [k, v] of pendingConsentByKey) {
    const primary = pendingConsents.get(v.primaryId);
    if (!primary || primary.status !== "pending") pendingConsentByKey.delete(k);
  }
}

function issueAccessEntry({ label, scope, clientId, familyId, ttlSec }) {
  const plain = genSecret("apw_", 24);
  const entry = {
    id: "tok_" + crypto.randomBytes(4).toString("hex"),
    label,
    hash: hashSecret(plain),
    scope,
    root: null,
    source: "oauth",
    oauthClientId: clientId || null,
    familyId: familyId || null,
    createdAt: new Date().toISOString(),
    expiresAt: new Date(Date.now() + ttlSec * 1000).toISOString(),
    ttlDays: null,
    maxRequests: null,
    usedRequests: 0,
    lastUsedAt: null,
    revoked: false,
    rotatedFrom: null,
  };
  const reg = loadTokens();
  reg.tokens.push(entry);
  saveTokens(reg);
  return { plain, entry };
}

function revokeFamily(family, why) {
  family.revoked = true;
  family.revokedAt = new Date().toISOString();
  const reg = loadTokens();
  let killed = 0;
  for (const t of reg.tokens) {
    if (t.familyId === family.id && !t.revoked) { t.revoked = true; killed++; }
  }
  saveTokens(reg);
  audit({ event: "oauth.family_revoked", familyId: family.id, clientId: family.clientId, why: why || "", killedAccess: killed, ok: true });
  return killed;
}

function canonicalize(val) {
  if (Array.isArray(val)) return val.map(canonicalize);
  if (val && typeof val === "object") {
    const out = {};
    for (const k of Object.keys(val).sort()) {
      if (val[k] !== undefined) out[k] = canonicalize(val[k]);
    }
    return out;
  }
  return val;
}

function computeArgsHash(cleanArgs) {
  return sha256(Buffer.from(JSON.stringify(canonicalize(cleanArgs || {})), "utf8"));
}

function matchesRedFlags(command, redFlagsCfg) {
  if (!redFlagsCfg || !redFlagsCfg.enabled || !command) return false;
  const patterns = Array.isArray(redFlagsCfg.patterns) ? redFlagsCfg.patterns : [];
  for (const rawPat of patterns) {
    try {
      let pat = String(rawPat);
      let flags = "i";
      if (pat.startsWith("(?i)")) pat = pat.slice(4);
      const re = new RegExp(pat, flags);
      if (re.test(command)) return true;
    } catch {}
  }
  return false;
}

// In-memory active grants: key -> { key, tool, scope, argsHash, target, createdAt, expiresAt }
const activeGrants = new Map();

function pruneGrants() {
  const now = Date.now();
  for (const [k, g] of activeGrants) {
    if (g.expiresAt <= now) activeGrants.delete(k);
  }
}

function findActiveGrant(tool, argsHash) {
  pruneGrants();
  const exactKey = `${tool}:${argsHash}`;
  if (activeGrants.has(exactKey)) return activeGrants.get(exactKey);
  const toolKey = `${tool}:*`;
  if (activeGrants.has(toolKey)) return activeGrants.get(toolKey);
  return null;
}

function storeGrant(tool, scope, argsHash, minutes, target) {
  pruneGrants();
  const now = Date.now();
  const expiresAt = now + Math.max(1, Number(minutes) || 10) * 60000;
  const key = scope === "exact" ? `${tool}:${argsHash}` : `${tool}:*`;
  const entry = {
    key,
    tool,
    scope: scope === "exact" ? "exact" : "tool",
    argsHash: scope === "exact" ? argsHash : null,
    target: target || tool,
    createdAt: new Date(now).toISOString(),
    expiresAt,
    expiresAtIso: new Date(expiresAt).toISOString(),
  };
  activeGrants.set(key, entry);
  return entry;
}

// Missed approval requests (timeout / tray unavailable): the owner was away
// and never saw the card. Kept in memory (last 50) and mirrored into audit;
// the tray polls /local/status (missedCount) and reads /local/missed.
const missedApprovals = [];
function recordMissedApproval(kind, x) {
  missedApprovals.push({ ts: new Date().toISOString(), kind, ...x });
  if (missedApprovals.length > 50) missedApprovals.shift();
}

const TOOL_TITLES_RU = {
  run_command: tr("PowerShell command", "Команда PowerShell"),
  write_file: tr("Write file", "Запись файла"),
  edit_file: tr("Edit file", "Правка файла"),
  read_file: tr("Read file", "Чтение файла"),
  list_directory: tr("List directory", "Список папки"),
  search_files: tr("Search files", "Поиск по файлам"),
  job_status: tr("Background job status", "Статус фоновой задачи"),
  job_output: tr("Background job output", "Вывод фоновой задачи"),
  audit_log: tr("Audit log", "Журнал аудита"),
  journal_entry: tr("Journal entry", "Запись в журнал"),
  journal_log: tr("Read journal", "Чтение журнала"),
  take_screenshot: tr("Screenshot", "Скриншот"),
  user_message: tr("Agent message", "Сообщение агента"),
  task_status: tr("Task status", "Статус задачи"),
  agent_message: tr("Agent messenger", "Мессенджер агентов"),
  await_owner: tr("Standby for owner (5 min)", "Ожидание владельца (5 мин)"),
  read_image: tr("View image", "Просмотр изображения"),
  file_ops: tr("File operation", "Операция с файлами"),
  system_info: tr("System info", "Сведения о системе"),
};

function buildApprovalSummary(tool, cleanArgs) {
  const title = TOOL_TITLES_RU[tool] || tool;
  let target = "";
  let detail = "";
  if (tool === "run_command") {
    target = redactSecrets(String(cleanArgs.command || "")).replace(/\s+/g, " ").trim();
    const cwd = cleanArgs.cwd ? resolvePath(cleanArgs.cwd) : PROJECT_DIR;
    detail = `cwd: ${cwd}`;
    if (/CopyFromScreen|BitBlt/i.test(String(cleanArgs.command || ""))) {
      detail += tr(" · looks like a screen capture", " · похоже на захват экрана");
    }
  } else if (tool === "take_screenshot") {
    target = tr("Screenshot of the primary screen", "Скриншот основного экрана");
    detail = tr("JPEG preview is attached to the approval card", "JPEG-превью приложено к карточке подтверждения");
  } else if (tool === "write_file") {
    target = resolvePath(cleanArgs.path || "");
    const bytes = Buffer.byteLength(String(cleanArgs.content || ""), "utf8");
    detail = `${bytes} bytes${cleanArgs.dryRun ? " (dryRun)" : ""}`;
  } else if (tool === "edit_file") {
    target = resolvePath(cleanArgs.path || "");
    if (Array.isArray(cleanArgs.edits) && cleanArgs.edits.length) {
      detail = tr(cleanArgs.edits.length + " edits in one atomic change", cleanArgs.edits.length + " правок одним атомарным изменением");
    } else {
      const oldLen = String(cleanArgs.oldString || "").split("\n").length;
      const newLen = String(cleanArgs.newString || "").split("\n").length;
      detail = tr("replace " + oldLen + " lines → " + newLen + " lines", "замена " + oldLen + " стр. → " + newLen + " стр.");
    }
  } else if (tool === "file_ops") {
    const op = String(cleanArgs.op || "");
    const opText = { copy: tr("Copy", "Копировать"), move: tr("Move", "Переместить"), delete: tr("Delete (to history)", "Удалить (в историю)"), mkdir: tr("Create folder", "Создать папку") }[op] || op;
    target = resolvePath(cleanArgs.path || "") + (cleanArgs.to ? "  →  " + resolvePath(cleanArgs.to) : "");
    detail = opText + (cleanArgs.overwrite ? tr(" · overwrite", " · с заменой") : "") + (cleanArgs.recursive ? tr(" · with contents", " · с содержимым") : "");
  } else if (tool === "read_image") {
    target = resolvePath(cleanArgs.path || "");
    detail = tr("The picture will be shared with the model", "Изображение будет передано модели");
  } else if (cleanArgs && typeof cleanArgs.path === "string") {
    target = resolvePath(cleanArgs.path);
    detail = cleanArgs.query ? tr("query: " + cleanArgs.query, "запрос: " + cleanArgs.query) : "";
  } else if (cleanArgs && typeof cleanArgs.query === "string") {
    target = cleanArgs.query;
    detail = PROJECT_DIR;
  } else if (cleanArgs && typeof cleanArgs.jobId === "string") {
    target = `job_id: ${cleanArgs.jobId}`;
  } else if (cleanArgs && typeof cleanArgs.summary === "string") {
    target = cleanArgs.summary.slice(0, 120);
  } else {
    target = tool;
  }
  return { title, target: target.slice(0, 300), detail: detail.slice(0, 200) };
}

function requestTrayApproval(payload, timeoutSec) {
  return new Promise((resolve) => {
    const bodyBuf = Buffer.from(JSON.stringify(payload), "utf8");
    const headers = {
      "Content-Type": "application/json; charset=utf-8",
      "Content-Length": String(bodyBuf.length),
      Connection: "close",
    };
    if (TRAY_CONTROL_SECRET) headers["X-ZeroIDE-Control"] = TRAY_CONTROL_SECRET;

    let settled = false;
    const done = (val) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      resolve(val);
    };

    const req = http.request(
      {
        hostname: "127.0.0.1",
        port: TRAY_CONTROL_PORT,
        path: "/approval/request",
        method: "POST",
        headers,
        agent: false,
      },
      (res) => {
        const chunks = [];
        res.on("data", (d) => chunks.push(d));
        res.on("end", () => {
          const raw = Buffer.concat(chunks).toString("utf8");
          if (res.statusCode !== 200) {
            done({ error: `HTTP ${res.statusCode}: ${raw.slice(0, 120)}` });
            return;
          }
          try {
            done(JSON.parse(raw));
          } catch (e) {
            done({ error: "invalid JSON from tray" });
          }
        });
      }
    );

    const timer = setTimeout(() => {
      try {
        req.destroy(new Error("approval_http_timeout"));
      } catch {}
      done({ error: "approval_http_timeout" });
    }, (timeoutSec + 5) * 1000);

    req.on("error", (err) => {
      done({ error: String((err && err.message) || err) });
    });

    req.write(bodyBuf);
    req.end();
  });
}

/* ---------------------- tray message + screenshot helpers ------------------- */
function postTrayJson(path, payload, timeoutMs = 3000) {
  return new Promise((resolve) => {
    const bodyBuf = Buffer.from(JSON.stringify(payload), "utf8");
    const headers = {
      "Content-Type": "application/json; charset=utf-8",
      "Content-Length": String(bodyBuf.length),
      Connection: "close",
    };
    if (TRAY_CONTROL_SECRET) headers["X-ZeroIDE-Control"] = TRAY_CONTROL_SECRET;
    const req = http.request(
      { hostname: "127.0.0.1", port: TRAY_CONTROL_PORT, path, method: "POST", headers, agent: false },
      (res) => {
        const chunks = [];
        res.on("data", (d) => chunks.push(d));
        res.on("end", () =>
          resolve({ ok: res.statusCode >= 200 && res.statusCode < 300, status: res.statusCode })
        );
      }
    );
    req.setTimeout(timeoutMs, () => {
      try { req.destroy(new Error("tray_post_timeout")); } catch {}
      resolve({ ok: false, error: "tray_post_timeout" });
    });
    req.on("error", (e) => resolve({ ok: false, error: String((e && e.message) || e) }));
    req.write(bodyBuf);
    req.end();
  });
}

/* -------- multi-agent registry + instant owner-to-agent messaging -------- */
// Per-boot in-memory stamp passed in SERVER_INSTRUCTIONS so the model knows
//PAUSED_BY_OWNER / INTERRUPTED_BY_OWNER messages come from the real tray UI,
// never from an external file on disk.
const OWNER_AUTH_TAG = "ZIDE-" + crypto.randomBytes(3).toString("hex").toUpperCase();

// Keyed by normalized agentId (e.g. "claude-landing", "backend-fixer", "legacy").
const agentRegistry = new Map();
// Maps callerId ("legacy", "tok_xxx") -> most recent agentId used by that token,
// so calls that omit `agent` still attach to the right agent slot.
const lastAgentByCaller = new Map();

function normalizeAgentId(raw) {
  const s = String(raw || "").trim().slice(0, 64);
  if (!s) return "";
  return s.toLowerCase().replace(/\s+/g, "-");
}

function resolveAgentSlot(sessionRef, explicitAgent) {
  const caller = sessionRef?.caller || LEGACY_CALLER;
  const callerKey = caller.id || "legacy";
  const tokenLabel = caller.label || "legacy";
  const explicitClean = String(explicitAgent || "").trim().slice(0, 64);
  if (explicitClean) {
    sessionRef.agentName = explicitClean;
  }
  let agentName =
    explicitClean ||
    sessionRef?.agentName ||
    lastAgentByCaller.get(callerKey) ||
    (tokenLabel !== "legacy" ? tokenLabel : sessionRef?.clientName || "AI-агент");
  const agentId = normalizeAgentId(agentName) || "default";
  lastAgentByCaller.set(callerKey, agentName);

  let slot = agentRegistry.get(agentId);
  if (!slot) {
    slot = {
      agentId,
      agentName,
      tokenLabel,
      callerKey,
      title: "",
      current: "",
      done: [],
      next: [],
      taskState: "running", // "running" | "done" | "waiting"
      startedAt: "",
      completedAt: "",
      updatedAt: "",
      lastTool: "",
      lastSeenAt: new Date().toISOString(),
      callsSinceTaskStatus: 0,
      totalCalls: 0,
      hasEverSetTask: false,
      hasEverSentUserMessage: false,
      lastTaskStatusAtMs: 0,
      lastUserMessageAtMs: 0,
      userMessageText: "",
      userMessageKind: "status",
      userMessageId: "",
      standbyUntilMs: 0,
      standbyActiveCalls: 0,
      standbyPulseGraceUntilMs: 0,
      standbyReleased: false,
    };
    agentRegistry.set(agentId, slot);
  } else {
    if (explicitClean) slot.agentName = explicitClean;
    slot.tokenLabel = tokenLabel;
    slot.lastSeenAt = new Date().toISOString();
  }
  return slot;
}

// Queue of owner / peer messages waiting to intercept an agent.
const ownerMessageQueue = [];
const ownerMessageListeners = new Set();
// Unified dialogue log between agents and the PC owner shown in the tray chat card.
const chatHistory = [];
// When true (toggled manually by the PC owner in the tray UI), the server instructs
// finishing agents to call await_owner and wait 5 minutes in the tray before closing.
let holdAfterTask = false;

function pushChatHistory(entry) {
  chatHistory.push({
    id: entry.id || "chat_" + crypto.randomUUID().slice(0, 8),
    role: entry.role || "agent", // "agent" | "owner"
    agent: entry.agent || "AI-агент",
    text: String(entry.text || "").trim().slice(0, 500),
    kind: entry.kind || "status",
    status: entry.status || "done",
    jobId: entry.jobId || null,
    ts: entry.ts || new Date().toISOString(),
  });
  while (chatHistory.length > 30) chatHistory.shift();
}

function notifyOwnerListeners() {
  for (const listener of Array.from(ownerMessageListeners)) {
    try { listener(); } catch {}
  }
}

function messageMatchesSlot(msg, slot) {
  if (!msg || msg.status !== "queued") return false;
  const target = foldCase(String(msg.to || "*").trim());
  if (!target || target === "*" || target === "all") return true;
  if (!slot) return false;
  if (
    target === foldCase(slot.agentId) ||
    target === foldCase(slot.agentName) ||
    target === foldCase(slot.tokenLabel)
  ) {
    return true;
  }
  // If the owner sent a message from the tray while another tab under the same
  // token/caller is idle, deliver it to the actively running agent on this PC
  // instead of stranding the message on an inactive tab.
  if (msg.from === "owner") {
    const targetSlot =
      agentRegistry.get(normalizeAgentId(msg.to)) ||
      Array.from(agentRegistry.values()).find(
        (a) => foldCase(a.agentName) === target || foldCase(a.agentId) === target
      );
    if (!targetSlot || targetSlot.callerKey === slot.callerKey) {
      return true;
    }
  }
  return false;
}

function enqueueOwnerMessage({ to = "*", text, stopCommand = false, from = "owner" }) {
  const cleanText = String(text || "").trim().slice(0, 2000);
  if (!cleanText) return null;
  const msg = {
    id: "omsg_" + crypto.randomUUID().slice(0, 8),
    to: String(to || "*").trim() || "*",
    text: cleanText,
    stopCommand: Boolean(stopCommand),
    from: from === "owner" ? "owner" : String(from).trim().slice(0, 64),
    authTag: from === "owner" ? OWNER_AUTH_TAG : null,
    status: "queued",
    createdAt: new Date().toISOString(),
    deliveredAt: null,
    deliveredTo: null,
    deliveryMode: null,
    jobId: null,
  };
  ownerMessageQueue.push(msg);
  while (ownerMessageQueue.length > 50) ownerMessageQueue.shift();
  pushChatHistory({
    id: msg.id,
    role: "owner",
    agent: msg.to === "*" ? "Всем" : msg.to,
    text: msg.text,
    status: "queued",
    ts: msg.createdAt,
  });
  audit({
    event: "owner_message.queued",
    id: msg.id,
    to: msg.to,
    from: msg.from,
    stopCommand: msg.stopCommand,
    ok: true,
  });
  notifyOwnerListeners();
  return msg;
}

function matchAndClaimMessage(slot, deliveryMode) {
  for (const msg of ownerMessageQueue) {
    if (messageMatchesSlot(msg, slot)) {
      msg.status = "delivered";
      msg.deliveredAt = new Date().toISOString();
      msg.deliveredTo = slot ? slot.agentName : "agent";
      msg.deliveryMode = deliveryMode;
      const ch = chatHistory.find((c) => c.id === msg.id);
      if (ch) {
        ch.status = "delivered";
        ch.agent = msg.deliveredTo;
      }
      audit({
        event: "owner_message.delivered",
        id: msg.id,
        to: msg.deliveredTo,
        from: msg.from,
        mode: deliveryMode,
        ok: true,
      });
      return msg;
    }
  }
  return null;
}

function createAgentMessageWaiter(slot, deliveryMode) {
  let cleanup = () => {};
  const promise = new Promise((resolve) => {
    const immediate = matchAndClaimMessage(slot, deliveryMode);
    if (immediate) {
      resolve(immediate);
      return;
    }
    const onCheck = () => {
      const hit = matchAndClaimMessage(slot, deliveryMode);
      if (hit) {
        ownerMessageListeners.delete(onCheck);
        resolve(hit);
      }
    };
    ownerMessageListeners.add(onCheck);
    cleanup = () => ownerMessageListeners.delete(onCheck);
  });
  return { promise, cleanup: () => cleanup() };
}

function formatSenderHeader(msg) {
  if (msg.from === "owner") {
    return `[ZEROIDE-OWNER-AUTH: ${msg.authTag || OWNER_AUTH_TAG}]`;
  }
  return `[PEER-AGENT-MESSAGE from="${msg.from}"]`;
}

function formatPausedByOwnerResult(msg, toolName, slot) {
  const header = formatSenderHeader(msg);
  return textResult(
    [
      `PAUSED_BY_OWNER ${header}`,
      `paused_tool: ${toolName}`,
      `agent: ${slot.agentName}`,
      tr(
        `status: paused_before_execution (the call to ${toolName} was NOT executed yet because a live message arrived for you in ZeroIDE MCP).`,
        `status: paused_before_execution (вызов ${toolName} ещё НЕ выполнялся, так как для тебя пришло живое сообщение в ZeroIDE MCP).`
      ),
      "--- message ---",
      msg.text,
      "--- next_action ---",
      tr(
        `1. Read and follow the message above (${header} confirms it comes from the ZeroIDE MCP supervisor, not from any file).`,
        `1. Прочитай и учти сообщение выше (штамп ${header} подтверждает, что это сообщение из шлюза ZeroIDE MCP, а не содержимое файла).`
      ),
      tr(
        `2. Acknowledge receipt via user_message({ agent: "${slot.agentName}", text: "...", kind: "status" }) and update task_status if your plan changed.`,
        `2. Подтверди получение через user_message({ agent: "${slot.agentName}", text: "...", kind: "status" }) и обнови task_status, если план изменился.`
      ),
      tr(
        `3. If the paused call (${toolName}) does NOT conflict with the message above and is still needed, simply call ${toolName} again now.`,
        `3. Если приостановленный вызов (${toolName}) НЕ противоречит сообщению выше и всё ещё нужен — просто вызови ${toolName} повторно прямо сейчас.`
      ),
    ].join("\n")
  );
}

function formatInterruptedCommandResult(msg, jobId, command, stopped, slot) {
  const header = formatSenderHeader(msg);
  if (stopped) {
    return textResult(
      [
        `INTERRUPTED_BY_OWNER ${header}`,
        `status: stopped_by_owner`,
        `agent: ${slot.agentName}`,
        `command: ${command}`,
        tr(
          "message: The PC owner stopped the running command and sent you a direct message from the ZeroIDE tray.",
          "message: Владелец ПК остановил выполнявшуюся команду и прислал тебе прямое сообщение из трея ZeroIDE."
        ),
        "--- message ---",
        msg.text,
        "--- next_action ---",
        tr(
          `1. Follow the owner's message above immediately and update task_status / user_message({ agent: "${slot.agentName}", ... }).`,
          `1. Немедленно выполни указание владельца выше и обнови task_status / user_message({ agent: "${slot.agentName}", ... }).`
        ),
      ].join("\n")
    );
  }
  return textResult(
    [
      `INTERRUPTED_BY_OWNER ${header}`,
      `status: running`,
      `job_id: ${jobId}`,
      `agent: ${slot.agentName}`,
      `command: ${command}`,
      tr(
        `message: The PC owner sent you a live message while this command was running. The command was NOT killed — it continues running in the background under job_id: ${jobId}.`,
        `message: Владелец ПК прислал тебе сообщение во время работы команды. Команда НЕ прервана — она переведена в фон и продолжает выполняться под job_id: ${jobId}.`
      ),
      "--- message ---",
      msg.text,
      "--- next_action ---",
      tr(
        `1. Read and follow the owner's message above (${header}).`,
        `1. Прочитай и учти сообщение владельца выше (${header}).`
      ),
      tr(
        `2. Acknowledge via user_message({ agent: "${slot.agentName}", text: "...", kind: "status" }) and update task_status if needed.`,
        `2. Подтверди получение через user_message({ agent: "${slot.agentName}", text: "...", kind: "status" }) и обнови task_status при необходимости.`
      ),
      tr(
        `3. Your command is still running under job_id="${jobId}": check it with job_status({ jobId: "${jobId}", waitSec: 30 }) or fetch output with job_output({ jobId: "${jobId}" }).`,
        `3. Твоя команда продолжает работу в фоне под job_id="${jobId}": проверь её через job_status({ jobId: "${jobId}", waitSec: 30 }) или забери вывод через job_output({ jobId: "${jobId}" }).`
      ),
    ].join("\n")
  );
}

async function handleUserMessage(args, extra) {
  const text = String(args.text || "").trim().slice(0, 500);
  const kind = ["status", "request", "warning"].includes(args.kind) ? args.kind : "status";
  const persistent = args.persistent !== false;
  const id = String(args.id || "").slice(0, 64);
  const caller = extra?.caller || LEGACY_CALLER;
  const slot = extra?.agentSlot || resolveAgentSlot({ caller }, args.agent);
  if ((extra?.mode || "ask") === "deny") {
    audit({ tool: "user_message", agent: slot.agentName, kind, persistent, id, ok: false, reason: "TOOL_DENIED_BY_MODE" });
    return textResult(
      [
        tr("DENIED [TOOL_DENIED]: the PC owner disabled agent messages (Settings → Tools).", "ОТКАЗ [TOOL_DENIED]: владелец ПК отключил сообщения агента (Настройки → Инструменты)."),
        "tool: user_message",
      ].join("\n"),
      true
    );
  }
  if (caller.entry) bumpTokenUsage(caller.entry);
  const now = Date.now();
  // Per-agent rate limit (was global, which caused parallel agents to drop each other's messages).
  if (now - slot.lastUserMessageAtMs < 600) {
    return textResult(
      tr("User message skipped (rate limit: one per 0.6s per agent). The previous message is still shown.", "Сообщение пользователю пропущено (лимит: одно в 0.6 с на агента). Предыдущее сообщение ещё показано.")
    );
  }
  slot.lastUserMessageAtMs = now;
  slot.hasEverSentUserMessage = true;
  slot.userMessageText = text;
  slot.userMessageKind = kind;
  slot.userMessageId = id;
  if (text) {
    pushChatHistory({
      role: "agent",
      agent: slot.agentName,
      text,
      kind,
      status: "done",
    });
  }
  // v0.5: an optional one-click link (http(s) URL, file or folder). Nothing opens by itself;
  // programs and scripts are only revealed in Explorer, never launched from the button.
  const openInfo = text ? resolveOpenTarget(args.open, extra) : null;
  const open = openInfo && !openInfo.error ? openInfo : undefined;
  const res = await postTrayJson("/message/show", {
    text,
    kind,
    persistent,
    id,
    agent: slot.agentName,
    agentId: slot.agentId,
    open,
  });
  audit({ tool: "user_message", agent: slot.agentName, kind, persistent, id, ok: res.ok, open: open ? open.kind : undefined });
  let replyText = !res.ok
    ? tr("User message NOT shown: the tray app is unreachable (" + (res.error || "HTTP " + res.status) + ").", "Сообщение пользователю НЕ показано: приложение в трее недоступно (" + (res.error || "HTTP " + res.status) + ").")
    : text === ""
      ? tr("User message cleared.", "Сообщение пользователю очищено.")
      : tr("User message shown in the tray.", "Сообщение пользователю показано в трее.");
  if (res.ok && open) replyText += "\n" + (open.reveal ? tr("The message has a \"Show in folder\" button for ", "В сообщении есть кнопка «Показать в папке» для ") : tr("The message has an \"Open\" button for ", "В сообщении есть кнопка «Открыть» для ")) + open.target;
  else if (res.ok && openInfo && openInfo.error) replyText += "\n" + tr("open was ignored: ", "open не прикреплён: ") + openInfo.error;
  return textResult(replyText);
}

function isAwaitOwnerEnabled() {
  try {
    const perms = loadPermissions();
    const mode = (perms.tools && perms.tools.await_owner && perms.tools.await_owner.mode) || "deny";
    return mode !== "deny";
  } catch {
    return false;
  }
}

async function handleTaskStatus(args, extra) {
  const title = String(args.title || "").trim().slice(0, 120);
  const current = String(args.current || "").trim().slice(0, 200);
  const done = Array.isArray(args.done) ? args.done.map((s) => String(s).trim()).filter(Boolean).slice(0, 10).map((s) => s.slice(0, 120)) : [];
  const next = Array.isArray(args.next) ? args.next.map((s) => String(s).trim()).filter(Boolean).slice(0, 10).map((s) => s.slice(0, 120)) : [];
  const startedAtRaw = String(args.startedAt || "");
  const startedAt = startedAtRaw && !Number.isNaN(Date.parse(startedAtRaw)) ? startedAtRaw : "";
  const caller = extra?.caller || LEGACY_CALLER;
  const slot = extra?.agentSlot || resolveAgentSlot({ caller }, args.agent);
  if ((extra?.mode || "ask") === "deny") {
    audit({ tool: "task_status", agent: slot.agentName, title, ok: false, reason: "TOOL_DENIED_BY_MODE" });
    return textResult(
      [
        tr("DENIED [TOOL_DENIED]: the PC owner disabled task status (Settings → Tools).", "ОТКАЗ [TOOL_DENIED]: владелец ПК отключил статус задачи (Настройки → Инструменты)."),
        "tool: task_status",
      ].join("\n"),
      true
    );
  }
  if (caller.entry) bumpTokenUsage(caller.entry);
  const now = Date.now();
  let resolvedState = "running";
  const rawStatus = String(args.status || "").trim().toLowerCase();
  if (rawStatus === "done" || rawStatus === "completed") {
    resolvedState = "done";
  } else if (rawStatus === "waiting") {
    resolvedState = "waiting";
  } else if (
    title &&
    next.length === 0 &&
    (/^(готово|выполнено|завершено|done|completed|finished)\b/i.test(current) ||
      /^(готово|выполнено|завершено|done|completed|finished)\b/i.test(title))
  ) {
    resolvedState = "done";
  }
  // Never drop a final 'done' transition or explicit clear due to the 0.6s rate limit.
  const isStateTransition = (resolvedState === "done" && slot.taskState !== "done") || title === "";
  if (!isStateTransition && now - slot.lastTaskStatusAtMs < 600) {
    return textResult(
      tr("Task status skipped (rate limit: one per 0.6s per agent). The previous status is still shown.", "Статус задачи пропущен (лимит: один в 0.6 с на агента). Предыдущий статус ещё показан.")
    );
  }

  slot.lastTaskStatusAtMs = now;
  slot.title = title;
  slot.current = current;
  slot.done = done;
  slot.next = next;
  slot.taskState = resolvedState;
  if (startedAt) slot.startedAt = startedAt;
  else if (title && !slot.startedAt) slot.startedAt = new Date().toISOString();
  if (!title) {
    slot.startedAt = "";
    slot.completedAt = "";
    slot.taskState = "running";
    slot.standbyUntilMs = 0;
    slot.standbyPulseGraceUntilMs = 0;
  } else if (resolvedState === "done") {
    if (!slot.completedAt) slot.completedAt = new Date().toISOString();
    // Do NOT fake a standby countdown here — countdown is active ONLY while await_owner is genuinely open.
  } else {
    slot.completedAt = "";
    slot.standbyUntilMs = 0;
    slot.standbyPulseGraceUntilMs = 0;
  }
  slot.updatedAt = new Date().toISOString();
  slot.hasEverSetTask = Boolean(title);
  slot.callsSinceTaskStatus = 0;

  const res = await postTrayJson("/task/show", {
    title,
    current,
    done,
    next,
    status: slot.taskState,
    startedAt: slot.startedAt,
    completedAt: slot.completedAt,
    agent: slot.agentName,
    agentId: slot.agentId,
  });
  audit({ tool: "task_status", agent: slot.agentName, agentId: slot.agentId, title, status: slot.taskState, ok: res.ok });
  let replyText = !res.ok
    ? tr("Task status NOT shown: the tray app is unreachable (" + (res.error || "HTTP " + res.status) + ").", "Статус задачи НЕ показан: приложение в трее недоступно (" + (res.error || "HTTP " + res.status) + ").")
    : title === ""
      ? tr("Task status cleared for agent " + slot.agentName + ".", "Статус задачи очищен для агента " + slot.agentName + ".")
      : resolvedState === "done"
        ? (() => {
            const standbyAllowed = isAwaitOwnerEnabled();
            const holdActive = standbyAllowed && holdAfterTask;
            return tr(
              `Task marked as DONE in the tray (agent: ${slot.agentName}).` +
              (!slot.hasEverSentUserMessage ? `\n- Also call user_message({ agent: "${slot.agentName}", text: "...", kind: "status" }) so the owner sees your final summary in the tray.` : "") +
              (holdActive
                ? `\n- [HOLD_AFTER_TASK_ENABLED_BY_OWNER]: The PC owner manually turned ON the 'Hold agent 5 min' switch in the ZeroIDE tray right now (they may not have access to the chat window). Do NOT end your turn in chat — call await_owner({ agent: "${slot.agentName}", summary: "..." }) immediately and wait in the tray!`
                : standbyAllowed
                  ? `\n- If the owner specifically asked you to stay connected via tray (or chat UI is unavailable), call await_owner({ agent: "${slot.agentName}", summary: "..." }); otherwise reply normally in chat.`
                  : ""),
              `Задача отмечена как ВЫПОЛНЕННАЯ в трее (агент: ${slot.agentName}).` +
              (!slot.hasEverSentUserMessage ? `\n- Также отправь краткий итог через user_message({ agent: "${slot.agentName}", text: "...", kind: "status" }), чтобы владелец увидел результат в окне трея.` : "") +
              (holdActive
                ? `\n- [HOLD_AFTER_TASK_ENABLED_BY_OWNER]: Владелец ПК прямо сейчас вручную включил тумблер «Удерживать 5 мин» в трее ZeroIDE (возможно, у него нет доступа к окну чата). НЕ завершай ход в чате — обязательно вызови user_message с итогом и сразу вызови await_owner({ agent: "${slot.agentName}", summary: "..." }) для ожидания в трее!`
                : standbyAllowed
                  ? `\n- Если владелец просил ждать в трее (или веб-чат недоступен), вызови await_owner({ agent: "${slot.agentName}", summary: "..." }); иначе отвечай в чат как обычно.`
                  : "")
            );
          })()
        : tr("Task status shown in the tray (agent: " + slot.agentName + ").", "Статус задачи показан в трее (агент: " + slot.agentName + ").");
  return textResult(replyText);
}

const STANDBY_PULSE_MS = 45000; // 45s per pulse (< 60s cloud proxy timeout)

async function handleAwaitOwner(args, extra) {
  const caller = extra?.caller || LEGACY_CALLER;
  const slot = extra?.agentSlot || resolveAgentSlot({ caller }, args.agent);
  const summary = String(args.summary || "").trim().slice(0, 500);
  const continueStandby = Boolean(args.continueStandby);
  const waitMinutes = Math.min(Math.max(Number(args.waitMinutes) || 5, 0.1), 30);

  if (!continueStandby || !slot.standbyUntilMs || slot.standbyUntilMs <= Date.now()) {
    slot.standbyUntilMs = Date.now() + Math.round(waitMinutes * 60 * 1000);
    slot.standbyReleased = false;
    if (slot.title) {
      slot.taskState = "done";
      if (!slot.completedAt) slot.completedAt = new Date().toISOString();
    }
    if (summary) {
      slot.userMessageText = summary;
      slot.userMessageKind = "request";
      slot.hasEverSentUserMessage = true;
      pushChatHistory({
        role: "agent",
        agent: slot.agentName,
        text: summary,
        kind: "request",
        status: "done",
      });
      await postTrayJson("/message/show", {
        text: summary,
        kind: "request",
        persistent: true,
        id: "standby",
        agent: slot.agentName,
        agentId: slot.agentId,
      });
    }
  }

  const pulseEndMs = Math.min(slot.standbyUntilMs, Date.now() + STANDBY_PULSE_MS);
  const waiter = createAgentMessageWaiter(slot, "standby_reply");
  let claimedMsg = null;
  let pollTimer = null;
  slot.standbyActiveCalls += 1;
  slot.standbyPulseGraceUntilMs = 0;
  try {
    const raced = await Promise.race([
      waiter.promise.then((m) => ({ type: "msg", msg: m })),
      new Promise((resolve) => {
        pollTimer = setInterval(() => {
          if (slot.standbyReleased || Date.now() >= pulseEndMs) {
            clearInterval(pollTimer);
            resolve({ type: "tick" });
          }
        }, 150);
      }),
    ]);
    if (raced && raced.type === "msg" && raced.msg) {
      claimedMsg = raced.msg;
    }
  } finally {
    slot.standbyActiveCalls = Math.max(0, slot.standbyActiveCalls - 1);
    if (pollTimer) clearInterval(pollTimer);
    waiter.cleanup();
  }

  if (claimedMsg) {
    slot.standbyUntilMs = 0;
    slot.standbyPulseGraceUntilMs = 0;
    slot.taskState = "running";
    slot.completedAt = "";
    const hdr = formatSenderHeader(claimedMsg);
    return textResult(
      [
        `NEW_INSTRUCTION_FROM_OWNER ${hdr}`,
        `status: new_instruction_received`,
        `agent: ${slot.agentName}`,
        "--- message ---",
        claimedMsg.text,
        "--- next_action ---",
        tr(
          `1. The PC owner sent you a new task/message from the ZeroIDE tray while you were on standby (${hdr}).`,
          `1. Владелец ПК отправил тебе новое поручение/сообщение из трея ZeroIDE во время режима ожидания (${hdr}).`
        ),
        tr(
          `2. Update task_status({ agent: "${slot.agentName}", status: "running", title: "...", current: "..." }) and execute the instruction immediately.`,
          `2. Обнови task_status({ agent: "${slot.agentName}", status: "running", title: "...", current: "..." }) и сразу выполни поручение.`
        ),
        tr(
          `3. When finished, mark task_status({ status: "done", ... }) and call await_owner again for another 5-minute standby window.`,
          `3. Когда закончишь, отметь task_status({ status: "done", ... }) и снова вызови await_owner для 5-минутного ожидания.`
        ),
      ].join("\n")
    );
  }

  if (slot.standbyReleased) {
    slot.standbyUntilMs = 0;
    slot.standbyPulseGraceUntilMs = 0;
    slot.standbyReleased = false;
    return textResult(
      [
        "STANDBY_RELEASED_BY_OWNER",
        "status: released",
        tr(
          "message: The PC owner clicked Release in the tray. Standby is over — write your final response in chat and finish your turn now.",
          "message: Владелец ПК нажал «Отпустить» в трее. Ожидание завершено — напиши финальный ответ в чате и завершай ход."
        ),
      ].join("\n")
    );
  }

  const remainingSec = Math.max(0, Math.ceil((slot.standbyUntilMs - Date.now()) / 1000));
  if (remainingSec <= 0) {
    slot.standbyUntilMs = 0;
    slot.standbyPulseGraceUntilMs = 0;
    return textResult(
      [
        "STANDBY_TIMEOUT_COMPLETED",
        "status: timeout_completed",
        tr(
          "message: The 5-minute standby window has elapsed with no new messages from the PC owner. You may now write your final response in chat and disconnect.",
          "message: 5-минутное окно ожидания истекло, новых сообщений от владельца ПК не поступило. Теперь можешь написать финальный ответ в чате и завершить работу."
        ),
      ].join("\n")
    );
  }

  // Allow up to 4 seconds for the client to send the next continueStandby pulse
  // without flickering the tray indicator to offline.
  slot.standbyPulseGraceUntilMs = Date.now() + 4000;
  return textResult(
    [
      "STANDBY_KEEPALIVE",
      "status: waiting_for_owner",
      "remaining_seconds: " + remainingSec,
      tr(
        `next_action: Still holding the 5-minute standby connection (${remainingSec}s remaining). Immediately call await_owner({ agent: "${slot.agentName}", continueStandby: true }) with NO chat output in between to keep listening for the owner.`,
        `next_action: Продолжаем удерживать 5-минутную связь (${remainingSec} с осталось). Немедленно вызови await_owner({ agent: "${slot.agentName}", continueStandby: true }) без лишнего текста, чтобы продолжить слушать трей.`
      ),
    ].join("\n")
  );
}

async function handleAgentMessage(args, extra) {
  const action = args.action || (args.text ? "send" : "list_agents");
  const caller = extra?.caller || LEGACY_CALLER;
  const slot = extra?.agentSlot || resolveAgentSlot({ caller }, args.from || args.agent);
  if (action === "list_agents") {
    const list = Array.from(agentRegistry.values()).map((a) => ({
      agentId: a.agentId,
      agentName: a.agentName,
      token: a.tokenLabel,
      taskTitle: a.title || "(no active task)",
      currentStep: a.current || "-",
      lastTool: a.lastTool || "-",
      lastSeenAt: a.lastSeenAt,
    }));
    if (!list.length) return textResult(tr("No active agents registered yet.", "Активных агентов пока нет."));
    return textResult(
      [
        "active_agents: " + list.length,
        ...list.map(
          (a) =>
            `- agentId="${a.agentId}" name="${a.agentName}" token="${a.token}" task="${a.taskTitle}" step="${a.currentStep}" lastTool=${a.lastTool} lastSeen=${a.lastSeenAt}`
        ),
      ].join("\n")
    );
  }
  const text = String(args.text || "").trim().slice(0, 2000);
  if (!text) return textResult("ERROR: text is required when sending an agent_message.", true);
  const to = String(args.to || "*").trim() || "*";
  const stopCommand = Boolean(args.stopCommand);
  const msg = enqueueOwnerMessage({
    to,
    text,
    stopCommand,
    from: args.from || slot.agentName || caller.label || "peer-agent",
  });
  return textResult(
    [
      tr("OK: message queued for instant delivery to agent.", "OK: сообщение поставлено в очередь мгновенной доставки агенту."),
      "message_id: " + msg.id,
      "to: " + msg.to,
      "stop_running_command: " + msg.stopCommand,
      tr(
        "delivery: If the target agent is currently running a command, it will be interrupted immediately with a job_id; otherwise its next tool call will pause at the gate with your message.",
        "delivery: Если целевой агент сейчас выполняет команду, она мгновенно переведётся в job_id с доставкой сообщения; иначе его следующий вызов получит сообщение на входном шлюзе."
      ),
    ].join("\n")
  );
}

function captureScreenshot() {
  return new Promise((resolve) => {
    const script = [
      "Add-Type -AssemblyName System.Windows.Forms",
      "Add-Type -AssemblyName System.Drawing",
      "$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds",
      "$bmp = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)",
      "$g = [System.Drawing.Graphics]::FromImage($bmp)",
      "$g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)",
      "$w = 1440; $h = [int](1440 * $bounds.Height / $bounds.Width)",
      "$small = New-Object System.Drawing.Bitmap($w, $h)",
      "$sg = [System.Drawing.Graphics]::FromImage($small)",
      "$sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic",
      "$sg.DrawImage($bmp, 0, 0, $w, $h)",
      "$ms = New-Object System.IO.MemoryStream",
      "$enc = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }",
      "$ep = New-Object System.Drawing.Imaging.EncoderParameters(1)",
      "$ep.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter([System.Drawing.Imaging.Encoder]::Quality, 70)",
      "$small.Save($ms, $enc, $ep)",
      "[Convert]::ToBase64String($ms.ToArray())",
    ].join("\n");
    const child = spawn("powershell", ["-NoProfile", "-Command", script], { windowsHide: true });
    let out = "";
    let done = false;
    const timer = setTimeout(() => {
      if (done) return;
      done = true;
      try { child.kill(); } catch {}
      resolve({ ok: false, error: "screenshot_timeout" });
    }, 15000);
    child.stdout.on("data", (d) => { out += d.toString("utf8"); });
    child.on("error", (e) => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      resolve({ ok: false, error: String((e && e.message) || e) });
    });
    child.on("close", (code) => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      const b64 = out.replace(/\s+/g, "");
      if (code !== 0 || !b64) {
        resolve({ ok: false, error: "screenshot_failed" });
        return;
      }
      resolve({ ok: true, base64: b64 });
    });
  });
}

function imageResult(base64) {
  return { content: [{ type: "image", data: base64, mimeType: "image/jpeg" }] };
}

async function notifyScreenshotSaved(base64, description, note, tokenLabel) {
  try {
    await postTrayJson("/screenshot/save", { imageBase64: base64, description, note, tokenLabel }, 15000);
  } catch {}
}

async function handleTakeScreenshot(args, extra) {
  const note = extra?.note || "";
  const description = String(args.description || "").trim().slice(0, 500);
  const caller = extra?.caller || LEGACY_CALLER;
  const callerLabel = caller.label || "legacy";
  const argsHash = computeArgsHash({});
  if (caller.scope === "read") {
    audit({ event: "token.scope_deny", tool: "take_screenshot", session: extra?.sessionId, token: callerLabel, argsHash, ok: false, reason: "TOKEN_SCOPE_DENIED" });
    return textResult(
      [
        tr("DENIED [TOKEN_SCOPE_DENIED]: token «", "ОТКАЗ [TOKEN_SCOPE_DENIED]: токен «") + callerLabel + tr("» has scope read and cannot take screenshots.", "» имеет область доступа read и не может делать скриншоты."),
        "tool: take_screenshot",
        tr("action: Ask the PC owner to issue a token with scope readwrite or full (Settings → Access).", "action: Попроси владельца ПК выпустить токен с областью readwrite или full (Настройки → Доступ)."),
      ].join("\n"),
      true
    );
  }
  const shotMode = extra?.mode || "ask";
  if (shotMode === "deny") {
    audit({ event: "tool.mode_deny", tool: "take_screenshot", session: extra?.sessionId, token: callerLabel, argsHash, ok: false, reason: "TOOL_DENIED_BY_MODE" });
    return textResult(
      [
        tr("DENIED [TOOL_DENIED]: the PC owner disabled screenshots (Settings → Tools). No image was captured.", "ОТКАЗ [TOOL_DENIED]: владелец ПК отключил скриншоты (Настройки → Инструменты). Изображение не снималось."),
        "tool: take_screenshot",
      ].join("\n"),
      true
    );
  }
  if (shotMode === "allow") {
    const img0 = await captureScreenshot();
    if (!img0.ok) return textResult(tr("Screenshot failed: ", "Скриншот не получился: ") + img0.error, true);
    if (caller.entry) bumpTokenUsage(caller.entry);
    audit({ event: "tool.mode_allow", tool: "take_screenshot", session: extra?.sessionId, token: callerLabel, argsHash, ok: true });
    await notifyScreenshotSaved(img0.base64, description, note, callerLabel);
    return imageResult(img0.base64);
  }
  const grantHit = findActiveGrant("take_screenshot", argsHash);
  if (grantHit) {
    const img = await captureScreenshot();
    if (!img.ok) return textResult(tr("Screenshot failed: ", "Скриншот не получился: ") + img.error, true);
    if (caller.entry) bumpTokenUsage(caller.entry);
    audit({ event: "grant.hit", tool: "take_screenshot", session: extra?.sessionId, scope: grantHit.scope, argsHash, expiresAt: grantHit.expiresAtIso, ok: true });
    await notifyScreenshotSaved(img.base64, description, note, callerLabel);
    return imageResult(img.base64);
  }
  const img = await captureScreenshot();
  if (!img.ok) return textResult(tr("Screenshot failed: ", "Скриншот не получился: ") + img.error, true);
  const perms = loadPermissions();
  const timeoutSec = Math.max(5, Number(perms.approvalTimeoutSec) || 60);
  const reqId = "scr_" + Date.now().toString(36) + Math.random().toString(36).slice(2, 8);
  const summary = buildApprovalSummary("take_screenshot", {});
  const decisionResp = await requestTrayApproval(
    { id: reqId, tool: "take_screenshot", note: note || "", description: description || "", summary, argsHash, timeoutSec, previewImage: img.base64 },
    timeoutSec
  );
  if (decisionResp.error) {
    audit({ event: "approval.unavailable", id: reqId, tool: "take_screenshot", session: extra?.sessionId, argsHash, ok: false, reason: "TRAY_UNAVAILABLE" });
    return textResult(
      [
        tr("DENIED [TRAY_UNAVAILABLE]: the screenshot requires approval, but the tray app is unreachable. The screenshot was NOT sent.", "ОТКАЗ [TRAY_UNAVAILABLE]: скриншот требует подтверждения, но приложение в трее недоступно. Скриншот НЕ отправлен."),
        "tool: take_screenshot",
        tr("action: Tell the PC owner the tray app is not responding.", "action: Сообщи владельцу ПК, что приложение в трее не отвечает."),
      ].join("\n"),
      true
    );
  }
  if (decisionResp.decision === "allow_once") {
    if (caller.entry) bumpTokenUsage(caller.entry);
    audit({ event: "approval.allow_once", id: reqId, tool: "take_screenshot", session: extra?.sessionId, by: "user", argsHash, ok: true });
    await notifyScreenshotSaved(img.base64, description, note, callerLabel);
    return imageResult(img.base64);
  }
  if (decisionResp.decision === "allow_grant") {
    const grantMinutes = Math.max(1, Number(decisionResp.grantMinutes) || 10);
    const grantScope = decisionResp.grantScope === "exact" ? "exact" : "tool";
    const g = storeGrant("take_screenshot", grantScope, argsHash, grantMinutes, summary.target);
    if (caller.entry) bumpTokenUsage(caller.entry);
    audit({ event: "approval.grant", id: reqId, tool: "take_screenshot", session: extra?.sessionId, by: "user", grantMinutes, grantScope, argsHash, expiresAt: g.expiresAtIso, ok: true });
    await notifyScreenshotSaved(img.base64, description, note, callerLabel);
    return imageResult(img.base64);
  }
  if (decisionResp.decision === "deny" && decisionResp.by === "timeout") {
    audit({ event: "approval.timeout", id: reqId, tool: "take_screenshot", session: extra?.sessionId, by: "timeout", timeoutSec, argsHash, ok: false, reason: "APPROVAL_TIMEOUT" });
    recordMissedApproval("timeout", { id: reqId, tool: "take_screenshot", note: note || "", target: summary.target, detail: summary.detail, token: callerLabel });
    return textResult(
      [
        tr("DENIED [APPROVAL_TIMEOUT]: the PC owner did not answer the screenshot request within ", "ОТКАЗ [APPROVAL_TIMEOUT]: владелец ПК не ответил на запрос скриншота за ") + timeoutSec + tr(" s. The screenshot was NOT sent.", " с. Скриншот НЕ отправлен."),
        "tool: take_screenshot",
        tr("action: Do NOT repeat the call in a loop. Ask the user in chat how they want to proceed.", "action: НЕ повторяй вызов в цикле. Спроси у пользователя в чате, как он хочет поступить."),
      ].join("\n"),
      true
    );
  }
  audit({ event: "approval.deny", id: reqId, tool: "take_screenshot", session: extra?.sessionId, by: decisionResp.by || "user", argsHash, ok: false, reason: "APPROVAL_DENIED_BY_USER" });
  return textResult(
    [
      tr("DENIED [APPROVAL_DENIED_BY_USER]: the PC owner rejected the screenshot (pressed Dismiss). The screenshot was NOT sent.", "ОТКАЗ [APPROVAL_DENIED_BY_USER]: владелец ПК отклонил скриншот (нажал Отклонить). Скриншот НЕ отправлен."),
      "target: " + summary.target,
      tr("action: Do NOT repeat this same call automatically. Ask the user in chat how they want to proceed.", "action: НЕ повторяй этот же вызов автоматически. Спроси у пользователя в чате, как он хочет поступить."),
    ].join("\n"),
    true
  );
}

// Sent once in the initialize response: the rules that apply to every tool.
// Tool descriptions stay specific; cross-cutting guidance lives here so it is
// not repeated in every tool.
const SERVER_INSTRUCTIONS = [
  "ZeroIDE MCP: hands on the owner's Windows PC, supervised from a tray app. The owner sees every call and may approve, refuse, pause or message you directly from the tray.",
  "- Pass a one-line `note` (in the owner's language) with every mutating call: it is shown on the approval card. A call with no note is harder to approve.",
  "- Pass a consistent short `agent` name (e.g. your role or task name like \"Лендинг\", \"Backend\", \"Claude\") across your calls so the tray distinguishes you from other parallel agents.",
  "- MANDATORY TASK TRACKING (`task_status`): Within your first 2 tool calls on any task, you MUST call `task_status({ agent, status: \"running\", title, current, done, next })` so the PC owner sees what you are working on in the tray. Update `task_status` every 3–5 calls as steps move from `next` to `current` and `done`. When your task is finished, call `task_status({ agent, status: \"done\", title, current: \"Готово: ...\", done, next: [] })` so the tray card turns green and stops the running animation.",
  "- STANDBY MODE (`await_owner`): Do NOT stall normal chats with a 5-minute wait unless EITHER (a) the owner asks you to wait in the tray, OR (b) `task_status` / `journal_entry` returns `[HOLD_AFTER_TASK_ENABLED_BY_OWNER]` (meaning the owner explicitly flipped the 'Hold 5 min' switch in the tray because they are controlling you from the tray/another PC). When `[HOLD_AFTER_TASK_ENABLED_BY_OWNER]` is returned, you MUST send your report via `user_message` and immediately call `await_owner({ agent, summary: \"...\", waitMinutes: 5 })` instead of ending your turn in chat.",
  "- PROACTIVE OWNER MESSAGES (`user_message`): The PC owner watches the tray window (often without looking at the chat). Proactively call `user_message({ agent, text, kind })` without waiting to be asked whenever: (1) you start a major stage (`kind: \"status\"`), (2) you finish a milestone or the task (`kind: \"status\"`), (3) you hit a warning or need the owner's attention (`kind: \"warning\"` or `\"request\"`).",
  `- LIVE OWNER MESSAGES FROM TRAY ([ZEROIDE-OWNER-AUTH: ${OWNER_AUTH_TAG}]): The PC owner (or a peer agent via \`agent_message\`) can message you live while you work. If a running \`run_command\` returns \`INTERRUPTED_BY_OWNER\` with a \`job_id\`, the command is still running in the background under that \`job_id\` while you read the owner's message. If a call returns \`PAUSED_BY_OWNER\`, the call was paused BEFORE execution so you could read the message first — follow the owner's instruction, acknowledge via \`user_message\`, update \`task_status\`, and if the paused call does not conflict with the message, simply call it again.`,
  "- A DENIED result is the owner's decision: do not repeat the same call; explain and ask what to do instead.",
  "- run_command: anything that may take longer than ~20 s goes with wait=<seconds>; then poll job_status (wait up to 50 s) and job_output. Output is capped at 30 KB; a truncated result carries a job_id, and job_output pages the full text.",
  "- Files: read_file before edit_file; prefer edit_file (exact fragment) to rewriting a file with write_file. Several fragments of one file go in ONE edit_file call via edits[] (all-or-nothing). Relative paths resolve against the project dir shown in results.",
  "- Copy / move / delete / mkdir: use file_ops instead of shell commands. Deleted and overwritten files go to the owner's undo history (the result carries change_id), so the owner can restore them from the tray. Shell deletes (Remove-Item -Recurse, rd /s, del /s) are flagged as high risk and usually wait for approval.",
  "- Starting on an unfamiliar PC: call system_info once (OS, shells, installed dev tools, project dir, what the owner pre-approved) instead of probing with several commands.",
  "- Images (screenshots of a design, photos of an error, generated charts): read_image returns them to you as an image; large ones are downscaled.",
  "- ZeroIDE protects itself: its config.env, tokens, OAuth and tray state files are not readable or writable by agents, and the tray control channel is off-limits. Do not try to work around it; ask the owner instead.",
  "- Risk: commands and file operations are scored (low/medium/high/critical). High-risk calls may wait for the owner even if the tool is normally allowed; critical ones (formatting disks, wiping system folders, disabling protection) always need a fresh click. Explain why in `note`.",
  "- When a result is ready to look at (a built page, a report, a folder), pass `open` to user_message: the owner gets an 'Open' button in the tray instead of hunting for the path.",
].join("\n");

const NOTE_PARAM_SCHEMA = z
  .string()
  .max(200)
  .optional()
  .describe(
    "Short human-readable explanation (1 line, in the user's language) of what you are about to do and why. Shown to the user on the approval card. Always provide when calling mutating tools."
  );

const AGENT_PARAM_SCHEMA = z
  .string()
  .max(64)
  .optional()
  .describe(
    "Short consistent identifier or display name of this agent (e.g. 'Claude · Лендинг', 'Тесты', 'Мой агент'). Used to separate tasks and route tray messages when multiple agents work in parallel."
  );

/* --------------------------------- server -------------------------------- */

let lastMcpActivityAt = null;
let activeToolCalls = 0;
let activitySeq = 0;
let eventSeq = 0;
const recentEvents = [];

function recordToolEvent(tool, args, note, initialStatus = "running") {
  const now = new Date().toISOString();
  lastMcpActivityAt = now;
  activitySeq += 1;
  eventSeq += 1;
  let detail = "";
  let cmd = "";
  let filePath = "";
  let query = "";
  if (args && typeof args === "object") {
    if (typeof args.command === "string") {
      cmd = redactSecrets(args.command).replace(/\s+/g, " ").trim();
      detail = cmd;
    } else if (typeof args.path === "string") {
      filePath = args.path;
      detail = filePath;
    } else if (typeof args.query === "string") {
      query = args.query;
      filePath = typeof args.path === "string" ? args.path : "";
      detail = query + (filePath ? " (" + filePath + ")" : "");
    } else if (typeof args.jobId === "string") {
      detail = "job_id=" + args.jobId;
    } else if (typeof args.summary === "string") {
      detail = args.summary;
    }
  }
  detail = detail.replace(/\s+/g, " ").trim();
  if (detail.length > 200) detail = detail.slice(0, 197) + "...";
  const ev = {
    id: eventSeq,
    ts: now,
    tool,
    note: note || "",
    detail,
    cmd: cmd.slice(0, 240),
    path: filePath.slice(0, 240),
    query: query.slice(0, 120),
    status: initialStatus,
    startedAtMs: Date.now(),
    durationMs: null,
  };
  recentEvents.push(ev);
  if (recentEvents.length > 30) recentEvents.shift();
  return ev;
}

function makeServer(ctx) {
  const server = new McpServer({ name: "zeroide-mcp", version: SERVER_VERSION }, { instructions: SERVER_INSTRUCTIONS });
  const sessionRef = ctx || { sessionId: "unknown" };
  const reg = (name, config, handler) => {
    const augmentedConfig = {
      ...config,
      inputSchema: {
        ...(config.inputSchema || {}),
        note: NOTE_PARAM_SCHEMA,
        agent: (config.inputSchema && config.inputSchema.agent) || AGENT_PARAM_SCHEMA,
      },
    };
    return server.registerTool(
      name,
      augmentedConfig,
      wrapWithNudge(async (rawArgs, extra) => {
        const note =
          typeof rawArgs?.note === "string"
            ? redactSecrets(rawArgs.note).replace(/\s+/g, " ").trim().slice(0, 200)
            : "";
        const explicitAgent =
          typeof rawArgs?.agent === "string"
            ? rawArgs.agent.replace(/\s+/g, " ").trim().slice(0, 64)
            : "";
        const cleanArgs = { ...(rawArgs || {}) };
        delete cleanArgs.note;
        if (name !== "task_status" && name !== "user_message" && name !== "agent_message" && name !== "await_owner") {
          delete cleanArgs.agent;
        }
        const agentSlot = resolveAgentSlot(sessionRef, explicitAgent);
        agentSlot.lastTool = name;

        const appendTaskProtocolNudge = (resObj) => {
          if (!resObj || resObj.isError || !Array.isArray(resObj.content)) return resObj;
          if (name === "task_status" || name === "user_message" || name === "agent_message" || name === "await_owner" || name === "journal_entry") {
            return resObj;
          }
          agentSlot.totalCalls += 1;
          agentSlot.callsSinceTaskStatus += 1;
          let nudgeText = "";
          if (!agentSlot.hasEverSetTask && agentSlot.totalCalls >= 1) {
            nudgeText = tr(
              `\n\n⚠️ [TRAY PROTOCOL]: The PC owner tracks your work in the ZeroIDE tray (and Telegram alerts if enabled), which currently shows no task. On your next step call task_status({ agent: "${agentSlot.agentName}", status: "running", title: "...", current: "...", done: [...], next: [...] }) and report milestones/results via user_message({ agent: "${agentSlot.agentName}", text: "...", kind: "status" }).`,
              `\n\n⚠️ [ПРОТОКОЛ ТРЕЯ]: Владелец следит за работой в трее ZeroIDE (и в Telegram-алертах, если включены), где сейчас не отображается твоя задача. На следующем шаге вызови task_status({ agent: "${agentSlot.agentName}", status: "running", title: "...", current: "...", done: [...], next: [...] }) и сообщай об этапах и итоге через user_message({ agent: "${agentSlot.agentName}", text: "...", kind: "status" }).`
            );
          } else if (agentSlot.hasEverSetTask && agentSlot.callsSinceTaskStatus >= 4) {
            nudgeText = tr(
              `\n\n⚠️ [TRAY PROTOCOL]: ${agentSlot.callsSinceTaskStatus} calls since your last task_status update. Update task_status({ agent: "${agentSlot.agentName}", title: "${agentSlot.title}", current: "...", done: [...], next: [...] }) now so the tray reflects your actual current step` + (!agentSlot.hasEverSentUserMessage ? ` and send a brief update via user_message.` : `.`),
              `\n\n⚠️ [ПРОТОКОЛ ТРЕЯ]: Прошло ${agentSlot.callsSinceTaskStatus} вызовов без обновления статуса задачи. Обнови task_status({ agent: "${agentSlot.agentName}", title: "${agentSlot.title}", current: "...", done: [...], next: [...] }), чтобы карточка «Текущая задача» соответствовала реальности` + (!agentSlot.hasEverSentUserMessage ? ` и отправь короткий статус через user_message.` : `.`)
            );
          } else if (agentSlot.hasEverSetTask && !agentSlot.hasEverSentUserMessage && agentSlot.totalCalls >= 3 && agentSlot.totalCalls % 3 === 0) {
            nudgeText = tr(
              `\n\nℹ️ [TRAY PROTOCOL]: You have not sent a user_message yet. Send brief progress or your final summary via user_message({ agent: "${agentSlot.agentName}", text: "...", kind: "status" }) so the owner sees it in the tray / Telegram.`,
              `\n\nℹ️ [ПРОТОКОЛ ТРЕЯ]: Ты ещё не отправлял сообщение владельцу через user_message. Сообщи короткий прогресс или итог через user_message({ agent: "${agentSlot.agentName}", text: "...", kind: "status" }), чтобы владелец увидел его в трее / Telegram.`
            );
          }
          if (nudgeText) {
            for (const item of resObj.content) {
              if (item.type === "text") {
                item.text += nudgeText;
                break;
              }
            }
          }
          return resObj;
        };

        const argsHash = computeArgsHash(cleanArgs);
        const perms = loadPermissions();
        let mode =
          (perms.tools && perms.tools[name] && perms.tools[name].mode) ||
          perms.defaultMode ||
          "ask";

        // ---- v0.4 caller policy: token scope, budget, confinement ----
        // Runs BEFORE grants and permissions: neither a grant nor an "allow"
        // may widen what the caller's token permits.
        const caller = sessionRef.caller || LEGACY_CALLER;
        const callerLabel = caller.label || "legacy";
        if (caller.scope === "read" && !READ_TOOLS.has(name)) {
          audit({ event: "token.scope_deny", tool: name, session: sessionRef.sessionId, token: callerLabel, argsHash, ok: false, reason: "TOKEN_SCOPE_DENIED" });
          return textResult(
            [
              tr("DENIED [TOKEN_SCOPE_DENIED]: token «", "ОТКАЗ [TOKEN_SCOPE_DENIED]: токен «") + callerLabel + tr("» has scope read and cannot call ", "» имеет область доступа read и не может вызывать ") + name + ".",
              "tool: " + name,
              tr("action: Do NOT repeat this call. This action requires a token with scope readwrite or full — ask the PC owner to issue a different token (Settings → Access).", "action: НЕ повторяй вызов. Для этого действия нужен токен с областью readwrite или full — попроси владельца ПК выпустить другой токен (Настройки → Доступ)."),
            ].join("\n"),
            true
          );
        }
        if (caller.scope === "readwrite" && EXEC_TOOLS.has(name)) {
          audit({ event: "token.scope_deny", tool: name, session: sessionRef.sessionId, token: callerLabel, argsHash, ok: false, reason: "TOKEN_SCOPE_DENIED" });
          return textResult(
            [
              tr("DENIED [TOKEN_SCOPE_DENIED]: token «", "ОТКАЗ [TOKEN_SCOPE_DENIED]: токен «") + callerLabel + tr("» has scope readwrite; running commands is available only to full tokens.", "» имеет область доступа readwrite; выполнение команд доступно только токенам full."),
              "tool: " + name,
              tr("action: Do NOT repeat this call and do NOT try to replace the command with file writes. Ask the PC owner to issue a full token if the task really requires a terminal.", "action: НЕ повторяй вызов и НЕ пытайся заменить команду записью файлов. Попроси владельца ПК выпустить токен full, если задача действительно требует терминала."),
            ].join("\n"),
            true
          );
        }
        if (caller.entry) {
          const entryState = tokenEntryState(caller.entry);
          if (entryState === "TOKEN_EXPIRED") {
            audit({ event: "token.expired", tool: name, session: sessionRef.sessionId, token: callerLabel, argsHash, ok: false, reason: "TOKEN_EXPIRED" });
            return textResult(
              [
                tr("DENIED [TOKEN_EXPIRED]: token «", "ОТКАЗ [TOKEN_EXPIRED]: срок действия токена «") + callerLabel + tr("» has expired.", "» истёк."),
                "tool: " + name,
                tr("action: stop and ask the PC owner to issue a new token (Settings → Access).", "action: остановись и попроси владельца ПК выпустить новый токен (Настройки → Доступ)."),
              ].join("\n"),
              true
            );
          }
          if (caller.entry.maxRequests && (Number(caller.entry.usedRequests) || 0) >= Number(caller.entry.maxRequests)) {
            audit({ event: "token.exhausted", tool: name, session: sessionRef.sessionId, token: callerLabel, argsHash, ok: false, reason: "TOKEN_EXHAUSTED" });
            return textResult(
              [
                tr("DENIED [TOKEN_EXHAUSTED]: token «", "ОТКАЗ [TOKEN_EXHAUSTED]: токен «") + callerLabel + tr("» has exhausted its request limit (", "» исчерпал лимит запросов (") + caller.entry.maxRequests + ").",
                "tool: " + name,
                tr("action: stop and ask the PC owner to raise the limit or issue a new token (Settings → Access).", "action: остановись и попроси владельца ПК повысить лимит или выпустить новый токен (Настройки → Доступ)."),
              ].join("\n"),
              true
            );
          }
        }
        const conf = effectiveConfinement(perms, caller);
        if (conf.misconfigured && (FILE_TOOLS.has(name) || EXEC_TOOLS.has(name))) {
          audit({ event: "confinement.misconfigured", tool: name, session: sessionRef.sessionId, token: callerLabel, argsHash, ok: false, reason: "CONFINEMENT_MISCONFIGURED" });
          return textResult(
            [
              tr("DENIED [CONFINEMENT_MISCONFIGURED]: single-folder mode is misconfigured (the folder does not exist or the token folder lies outside the global one). Access is closed until the owner fixes the setting.", "ОТКАЗ [CONFINEMENT_MISCONFIGURED]: режим одной папки настроен неверно (папка не существует или папка токена выходит за глобальную). Доступ закрыт до исправления настройки владельцем."),
              "tool: " + name,
              tr("action: Do NOT repeat this call. Tell the PC owner to check the folder in ZeroIDE MCP settings.", "action: НЕ повторяй вызов. Сообщи владельцу ПК, что нужно проверить папку в настройках ZeroIDE MCP."),
            ].join("\n"),
            true
          );
        }
        if (conf.root) {
          if (EXEC_TOOLS.has(name) || name === "job_status" || name === "job_output") {
            audit({ event: "confinement.exec_deny", tool: name, session: sessionRef.sessionId, token: callerLabel, argsHash, ok: false, reason: "TOOL_DISABLED_BY_CONFINEMENT" });
            return textResult(
              [
                tr("DENIED [TOOL_DISABLED_BY_CONFINEMENT]: single-folder mode is enabled for this connection (", "ОТКАЗ [TOOL_DISABLED_BY_CONFINEMENT]: для этого подключения включён режим одной папки (") + conf.root + tr("), the terminal is fully disabled in it.", "), терминал в нём отключён полностью."),
                "tool: " + name,
                tr("action: Do NOT try to bypass the restriction through other tools. Work with files inside the allowed folder or ask the owner to issue a token without a folder.", "action: НЕ пытайся обойти ограничение через другие инструменты. Работай файлами внутри разрешённой папки или попроси владельца выдать токен без папки."),
              ].join("\n"),
              true
            );
          }
          if (FILE_TOOLS.has(name)) {
            const chk = confineTargetPath(conf.root, typeof cleanArgs.path === "string" ? cleanArgs.path : null);
            if (!chk.ok) {
              audit({ event: "confinement.path_deny", tool: name, session: sessionRef.sessionId, token: callerLabel, argsHash, ok: false, reason: "PATH_OUTSIDE_ROOT", root: conf.root });
              return textResult(
                [
                  tr("DENIED [PATH_OUTSIDE_ROOT]: the path is outside the allowed folder ", "ОТКАЗ [PATH_OUTSIDE_ROOT]: путь выходит за пределы разрешённой папки ") + conf.root + ".",
                  "tool: " + name,
                  tr("action: Do NOT repeat requests to paths outside this folder and do NOT look for workarounds. If the data you need lies outside, ask the owner to widen access.", "action: НЕ повторяй запрос к путям вне этой папки и НЕ ищи обходных путей. Если нужные данные лежат снаружи, попроси владельца расширить доступ."),
                ].join("\n"),
                true
              );
            }
            cleanArgs.path = chk.resolved;
            if (name === "file_ops" && typeof cleanArgs.to === "string" && cleanArgs.to) {
              const chkTo = confineTargetPath(conf.root, cleanArgs.to);
              if (!chkTo.ok) {
                audit({ event: "confinement.path_deny", tool: name, session: sessionRef.sessionId, token: callerLabel, argsHash, ok: false, reason: "PATH_OUTSIDE_ROOT", root: conf.root });
                return textResult(
                  [
                    tr("DENIED [PATH_OUTSIDE_ROOT]: the destination is outside the allowed folder ", "ОТКАЗ [PATH_OUTSIDE_ROOT]: путь назначения выходит за пределы разрешённой папки ") + conf.root + ".",
                    "tool: " + name,
                    tr("action: Do NOT look for workarounds; keep both paths inside the allowed folder.", "action: НЕ ищи обходных путей; оба пути должны быть внутри разрешённой папки."),
                  ].join("\n"),
                  true
                );
              }
              cleanArgs.to = chkTo.resolved;
            }
          }
        }

        // ---- v0.5 self-protection: ZeroIDE's own credentials and state ----
        if (FILE_TOOLS.has(name) && name !== "list_directory") {
          const checks = [];
          if (typeof cleanArgs.path === "string" && cleanArgs.path) {
            const intent = name === "file_ops" ? (cleanArgs.op === "copy" ? "read" : "write") : WRITE_PATH_TOOLS.has(name) ? "write" : "read";
            checks.push([resolvePath(cleanArgs.path), intent]);
          }
          if (name === "file_ops" && typeof cleanArgs.to === "string" && cleanArgs.to) checks.push([resolvePath(cleanArgs.to), "write"]);
          for (const [target, intent] of checks) {
            const kind = selfProtection(target, intent);
            if (kind) {
              audit({ event: "self.protect", tool: name, session: sessionRef.sessionId, token: callerLabel, argsHash, ok: false, reason: "PROTECTED_PATH", kind });
              return protectedPathResult(name, target, kind);
            }
          }
        }

        // ---- user_message / take_screenshot / task_status / agent_message: custom flows, bypass the generic gate ----
        // user_message and task_status are display-only (no approval); take_screenshot captures first
        // so the approval card can preview exactly the image that would be sent.
        // The registered handlers below receive the agent note via extra.note, plus the resolved mode.
        if (name === "user_message" || name === "take_screenshot" || name === "task_status" || name === "agent_message" || name === "await_owner") {
          const feedStart = Date.now();
          const bypassRes = await handler(cleanArgs, { ...extra, note, caller, sessionId: sessionRef.sessionId, argsHash, mode, agentSlot, confinement: conf });
          const feedArgs = name === "task_status" ? { summary: String(cleanArgs.title || "") } : name === "user_message" ? { summary: String(cleanArgs.text || "") } : name === "await_owner" ? { summary: String(cleanArgs.summary || "standby 5m") } : name === "agent_message" ? { summary: String(cleanArgs.text || cleanArgs.action || "") } : { summary: "screenshot" };
          const feedEv = recordToolEvent(name, feedArgs, note, bypassRes && bypassRes.isError ? "failed" : "done");
          feedEv.durationMs = Date.now() - feedStart;
          return bypassRes;
        }

        // ---- Pre-Execution Owner Message Gate (0% Prompt Injection Risk, Instant Delivery) ----
        // If the PC owner (or a peer agent) queued a message for this agent while it was thinking,
        // pause the incoming tool call BEFORE executing it or reading any external file.
        if (name !== "job_status") {
          const preMsg = matchAndClaimMessage(agentSlot, "paused_before_call");
          if (preMsg) {
            const pauseEv = recordToolEvent(name, cleanArgs, note || "paused by owner message", "done");
            pauseEv.durationMs = 1;
            return formatPausedByOwnerResult(preMsg, name, agentSlot);
          }
        }

        // ---- v0.5 risk assessment: explains the card and drives riskGate ----
        const assessed = MUTATING_TOOLS.has(name) || name === "read_file" || name === "read_image" || name === "search_files";
        const risk = assessed ? assessRisk(name, cleanArgs) : { level: "low", reasons: [] };
        const gate = { ...DEFAULT_PERMISSIONS.riskGate, ...(perms.riskGate || {}) };
        if (riskAtLeast(risk.level, "high")) {
          audit({ event: "risk.flagged", tool: name, session: sessionRef.sessionId, token: callerLabel, argsHash, level: risk.level, codes: risk.reasons.map((r) => r.code) });
        }
        // 1. Check active grants first
        let grantHit = findActiveGrant(name, argsHash);
        let riskEscalated = "";
        const grantable = !(gate.enabled !== false && riskAtLeast(risk.level, gate.grantsNeverCover || "critical"));
        if (grantHit && !grantable) {
          // No grant (tool-wide or exact) covers a critical call: the owner sees this call first.
          grantHit = null;
          riskEscalated = "grant";
          if (mode === "allow") mode = "ask";
        }
        let elevatedByRedFlag = false;
        if (!grantHit && name === "run_command" && mode === "allow") {
          if (
            perms.redFlags?.enabled !== false &&
            perms.redFlags?.elevateToAsk !== false &&
            matchesRedFlags(cleanArgs.command, perms.redFlags)
          ) {
            mode = "ask";
            elevatedByRedFlag = true;
          }
        }
        if (!grantHit && mode === "allow" && gate.enabled !== false) {
          const askAt = READ_TOOLS.has(name) ? (gate.sensitiveReads === false ? "" : "high") : gate.askAtOrAbove || "high";
          if (askAt && riskAtLeast(risk.level, askAt)) {
            mode = "ask";
            riskEscalated = riskEscalated || "rule";
          }
        }

        if (grantHit) {
          audit({
            event: "grant.hit",
            tool: name,
            session: sessionRef.sessionId,
            scope: grantHit.scope,
            argsHash,
            expiresAt: grantHit.expiresAtIso,
            ok: true,
          });
        } else if (mode === "deny") {
          audit({
            event: "approval.deny",
            tool: name,
            session: sessionRef.sessionId,
            by: "policy",
            note,
            argsHash,
            ok: false,
            reason: "TOOL_DISABLED_BY_USER",
          });
          return textResult(
            [
              tr("DENIED [TOOL_DISABLED_BY_USER]: tool ", "ОТКАЗ [TOOL_DISABLED_BY_USER]: инструмент ") +
                name +
                tr(" disabled by the PC owner in ZeroIDE MCP settings (mode “Denied”).", " отключён владельцем ПК в настройках ZeroIDE MCP (режим «Запрещено»)."),
              tr("action: Do NOT repeat this call and do NOT try to bypass the ban through another tool. Continue the task without it or tell the user in chat that this step requires enabling ", "action: НЕ повторяй этот вызов и НЕ пытайся обойти запрет через другой инструмент. Продолжи задачу без него или сообщи пользователю в чате, что для этого шага нужно включить ") +
                name +
                tr(" in the tray (Settings → Tools and permissions).", " в трее (Настройки → Инструменты и права)."),
            ].join("\n"),
            true
          );
        } else if (mode === "ask") {
          // An edit that cannot apply is answered right away instead of asking the owner first.
          if (name === "edit_file") {
            try {
              const pre = prepareEdit(resolvePath(cleanArgs.path), cleanArgs);
              if (!pre.ok) return editFailureResult(resolvePath(cleanArgs.path), pre, Array.isArray(cleanArgs.edits) ? cleanArgs.edits.length : 1);
            } catch {}
          }
          if (name === "file_ops") {
            const why = fileOpsPrecheck(cleanArgs);
            if (why) return textResult(why, true);
          }
          const timeoutSec = Math.max(5, Number(perms.approvalTimeoutSec) || 60);
          const reqId = "apr_" + crypto.randomUUID().slice(0, 8);
          const summary = buildApprovalSummary(name, cleanArgs);
          const preview = name === "write_file" || name === "edit_file" ? buildChangePreview(name, cleanArgs) : null;
          audit({
            event: "approval.request",
            id: reqId,
            tool: name,
            session: sessionRef.sessionId,
            note,
            argsHash,
            elevatedByRedFlag: elevatedByRedFlag || undefined,
            risk: risk.level !== "low" ? risk.level : undefined,
            riskEscalated: riskEscalated || undefined,
            timeoutSec,
            target: summary.target,
            detail: summary.detail,
          });

          const decisionResp = await requestTrayApproval(
            {
              id: reqId,
              tool: name,
              note,
              summary,
              argsHash,
              timeoutSec,
              createdAt: new Date().toISOString(),
              agent: agentSlot.agentName || undefined,
              assessment: {
                level: risk.level,
                headline: riskHeadline(risk.level),
                reasons: risk.reasons.map((r) => r.text),
                escalated: riskEscalated || (elevatedByRedFlag ? "redflag" : undefined),
                grantable,
              },
              preview: preview || undefined,
            },
            timeoutSec
          );

          if (!decisionResp || decisionResp.error) {
            audit({
              event: "approval.unavailable",
              id: reqId,
              tool: name,
              session: sessionRef.sessionId,
              argsHash,
              error: decisionResp?.error || "unreachable",
              ok: false,
              reason: "APPROVAL_UNAVAILABLE",
            });
            recordMissedApproval("unavailable", { id: reqId, tool: name, note, target: summary.target, detail: summary.detail, token: callerLabel });
            return textResult(
              [
                tr("DENIED [APPROVAL_UNAVAILABLE]: tool ", "ОТКАЗ [APPROVAL_UNAVAILABLE]: для инструмента ") +
                  name +
                  tr(" requires approval, but the tray app is unavailable.", " требуется подтверждение, но приложение в трее недоступно."),
                tr("action: tell the user to start ZeroIDE MCP on the PC.", "action: сообщи пользователю, что нужно запустить ZeroIDE MCP на ПК."),
              ].join("\n"),
              true
            );
          }

          if (decisionResp.decision === "allow_grant" && !grantable) {
            // Critical calls are approved one at a time, even if an older tray offers a grant.
            decisionResp.decision = "allow_once";
            audit({ event: "risk.grant_refused", id: reqId, tool: name, session: sessionRef.sessionId, argsHash, level: risk.level, ok: true });
          }
          if (decisionResp.decision === "allow_once") {
            audit({
              event: "approval.allow_once",
              id: reqId,
              tool: name,
              session: sessionRef.sessionId,
              by: "user",
              argsHash,
              ok: true,
            });
          } else if (decisionResp.decision === "allow_grant") {
            const grantMinutes = Math.max(1, Number(decisionResp.grantMinutes) || 10);
            const grantScope = decisionResp.grantScope === "exact" ? "exact" : "tool";
            const g = storeGrant(name, grantScope, argsHash, grantMinutes, summary.target);
            audit({
              event: "approval.grant",
              id: reqId,
              tool: name,
              session: sessionRef.sessionId,
              by: "user",
              grantMinutes,
              grantScope,
              argsHash,
              expiresAt: g.expiresAtIso,
              ok: true,
            });
          } else if (decisionResp.decision === "deny" && decisionResp.by === "timeout") {
            audit({
              event: "approval.timeout",
              id: reqId,
              tool: name,
              session: sessionRef.sessionId,
              by: "timeout",
              timeoutSec,
              argsHash,
              ok: false,
              reason: "APPROVAL_TIMEOUT",
            });
            recordMissedApproval("timeout", { id: reqId, tool: name, note, target: summary.target, detail: summary.detail, token: callerLabel });
            return textResult(
              [
                tr("DENIED [APPROVAL_TIMEOUT]: the PC owner did not answer the approval request within ", "ОТКАЗ [APPROVAL_TIMEOUT]: владелец ПК не ответил на запрос подтверждения за ") +
                  timeoutSec +
                  tr(" s (possibly away from the computer). The action was not performed.", " с (возможно, отошёл от компьютера). Действие не выполнялось."),
                "tool: " + name,
                tr("action: Do NOT repeat the call in a loop. Stop and write to the user in chat which action is awaiting their approval.", "action: НЕ повторяй вызов в цикле. Остановись и напиши пользователю в чате, какое действие ждёт его подтверждения."),
              ].join("\n"),
              true
            );
          } else {
            audit({
              event: "approval.deny",
              id: reqId,
              tool: name,
              session: sessionRef.sessionId,
              by: decisionResp.by || "user",
              argsHash,
              ok: false,
              reason: "APPROVAL_DENIED_BY_USER",
            });
            return textResult(
              [
                tr("DENIED [APPROVAL_DENIED_BY_USER]: the PC owner rejected this action in the approval card (pressed Dismiss).", "ОТКАЗ [APPROVAL_DENIED_BY_USER]: владелец ПК отклонил это действие в карточке подтверждения (нажал Dismiss)."),
                "tool: " + name,
                "target: " + summary.target,
                tr("action: Do NOT repeat this same call automatically. Ask the user in chat how they want to proceed, or suggest a safe alternative.", "action: НЕ повторяй этот же вызов автоматически. Спроси у пользователя в чате, как он хочет поступить, или предложи безопасную альтернативу."),
              ].join("\n"),
              true
            );
          }
        }

        if (caller.entry) bumpTokenUsage(caller.entry);
        const ev = recordToolEvent(name, cleanArgs, note, "running");
        if (risk.level !== "low") ev.risk = risk.level;
        const t0 = Date.now();
        activeToolCalls += 1;
        try {
          const res = await handler(cleanArgs, { ...extra, agentSlot, note, caller, confinement: conf, risk, sessionId: sessionRef.sessionId });
          ev.durationMs = Date.now() - t0;
          ev.status = res && res.isError ? "error" : "done";
          return appendTaskProtocolNudge(res);
        } catch (err) {
          ev.durationMs = Date.now() - t0;
          ev.status = "error";
          throw err;
        } finally {
          activeToolCalls = Math.max(0, activeToolCalls - 1);
          lastMcpActivityAt = new Date().toISOString();
          activitySeq += 1;
        }
      })
    );
  };

  /* ------------------------------ user_message ---------------------------- */
  reg(
    "user_message",
    {
      title: "Agent message",
      description:
        "Show a short message to the PC owner in the tray (banner in the panel + icon tooltip; kind=request also pops a toast). PROACTIVELY call this to report key milestones, completion summaries, or questions/blockers — the PC owner watches the tray window directly. Display-only: nothing leaves the PC and nothing is executed. Pass `open` to hand over a result: the banner gets an Open button for that page, file or folder. Pass `agent` to identify yourself when multiple agents work in parallel. An empty text clears it.",
      inputSchema: {
        text: z.string(),
        kind: z.enum(["status", "request", "warning"]).optional(),
        persistent: z.boolean().optional(),
        id: z.string().optional(),
        open: z.string().max(2048).optional().describe("Optional http(s) URL or local path (file or folder) the owner can open with one click from the message, e.g. a built page, a report or an output folder. Nothing opens automatically; programs and scripts are only shown in Explorer."),
        agent: AGENT_PARAM_SCHEMA,
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (args, extra) => handleUserMessage(args, extra)
  );

  /* ------------------------------ task_status ---------------------------- */
  reg(
    "task_status",
    {
      title: "Task status",
      description:
        "MANDATORY on all multi-step tasks: register and update your current task in the tray (panel block + multi-agent tab + icon tooltip). Call within your first 2 tool calls with `status: 'running'`, update every 3–5 calls so `current`, `done` and `next` always match reality, and when the task is complete MUST call with `status: 'done'` so the tray marks the task as completed (green check, stops progress animation) instead of leaving it looking like it is still running.",
      inputSchema: {
        title: z.string(),
        status: z.enum(["running", "done", "waiting"]).optional().describe("Task state: 'running' while in progress, 'done' when completed (turns card green & stops spinner), 'waiting' when waiting on owner."),
        current: z.string().optional(),
        done: z.array(z.string()).optional(),
        next: z.array(z.string()).optional(),
        startedAt: z.string().optional(),
        agent: z.string().optional(),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (args, extra) => handleTaskStatus(args, extra)
  );

  /* ------------------------------ await_owner ---------------------------- */
  reg(
    "await_owner",
    {
      title: "Standby for owner (5 min)",
      description:
        "Call at the end of a completed task (right after `task_status({ status: 'done' })`) to hold the MCP connection open for 5 minutes (default `waitMinutes: 5`) so the PC owner can send you follow-up instructions from the ZeroIDE tray even if they do not have access to the chat window. Uses 45-second keepalive pulses under the hood so cloud proxies never time out. If it returns `STANDBY_KEEPALIVE`, immediately call `await_owner({ agent, continueStandby: true })` until either the owner sends a message (`NEW_INSTRUCTION_FROM_OWNER`) or the 5 minutes expire (`STANDBY_TIMEOUT_COMPLETED` / `STANDBY_RELEASED_BY_OWNER`).",
      inputSchema: {
        summary: z.string().optional().describe("Short completion message shown to the owner in the tray chat card while waiting."),
        waitMinutes: z.number().optional().describe("Total standby duration in minutes (default 5)."),
        continueStandby: z.boolean().optional().describe("Set true on subsequent keepalive pulses within the same 5-minute standby window."),
        agent: AGENT_PARAM_SCHEMA,
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (args, extra) => handleAwaitOwner(args, extra)
  );

  /* ------------------------------ agent_message ---------------------------- */
  reg(
    "agent_message",
    {
      title: "Agent messenger",
      description:
        "Send a live message/instruction to another working agent connected to this ZeroIDE MCP server (for example, when coordinating from another PC or another session), or list currently active agents and their tasks (`action: 'list_agents'`). When `action: 'send'` is used, if the target agent is currently executing `run_command`, its command is immediately backgrounded into a `job_id` (or stopped if `stopCommand: true`) and your message is delivered in <50ms; otherwise the target agent's very next tool call pauses at the gate with your message.",
      inputSchema: {
        action: z.enum(["send", "list_agents"]).optional(),
        to: z.string().optional().describe("Target agent name/id (from list_agents) or '*' for all active agents."),
        text: z.string().optional().describe("Message or instruction to deliver to the target agent."),
        stopCommand: z.boolean().optional().describe("If true and the target agent is currently running a command, terminate that command immediately instead of moving it to a background job_id."),
        from: z.string().optional().describe("Your agent name/identifier."),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: false,
        openWorldHint: false,
      },
    },
    async (args, extra) => handleAgentMessage(args, extra)
  );

  /* ------------------------------ take_screenshot ---------------------------- */
  reg(
    "take_screenshot",
    {
      title: "Screenshot",
      description:
        "Capture the primary screen (1440px JPEG) and show it to the PC owner on a dedicated approval card with the exact preview image. The image leaves the PC ONLY if the owner approves (once or via a grant). Always asks unless an active grant exists. Pass a short \"description\" of why you need this screenshot: it is burned as a caption into the archived copy when the owner enabled screenshot archiving.",
      inputSchema: {
        description: z.string().max(500).optional().describe("Why you need this screenshot, in the user's language (1-2 sentences). Saved as a caption on the archived copy when the owner enabled screenshot archiving; falls back to the card note when empty."),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async (args, extra) => handleTakeScreenshot(args, extra)
  );

  /* ------------------------------ run_command ---------------------------- */
  reg(
    "run_command",
    {
      title: "Run command",
      description:
        (IS_WIN
          ? "Run a PowerShell command on this Windows PC as the Windows user who runs the tray (that user's rights; administrator only if the tray itself runs elevated). "
          : "Run a bash command on this machine as the user who runs the server (that user's rights). ") +
        "Depending on the owner's permission settings a command may wait for approval in the tray or be refused. Default cwd is the project dir. " +
        "Returns exit_code, status, duration_ms and separate stdout/stderr sections. " +
        "Output is capped at maxOutputBytes (default 30 KB, head+tail kept, truncation is always reported; a truncated result includes a job_id, and job_output pages the full text). " +
        "Set wait=<seconds> to run long commands in background: you get a job_id immediately and poll it with job_status/job_output. " +
        "Pass a unique requestId to make retries safe (a repeated requestId returns the cached result instead of running again).",
      inputSchema: {
        command: z.string(),
        cwd: z.string().optional(),
        timeoutMs: z.number().optional(),
        maxOutputBytes: z.number().optional(),
        wait: z.number().optional(),
        requestId: z.string().optional(),
      },
      // Arbitrary commands can delete data, are not idempotent (requestId only
      // de-duplicates retries) and can reach the network.
      annotations: {
        readOnlyHint: false,
        destructiveHint: true,
        idempotentHint: false,
        openWorldHint: true,
      },
    },
    async ({ command, cwd, timeoutMs, maxOutputBytes, wait, requestId }, extra) => {
      const maxOut = maxOutputBytes || DEFAULT_MAX_OUTPUT;
      const slot = extra?.agentSlot || resolveAgentSlot(sessionRef);
      try {
        const hit = dedupeGet(requestId);
        if (hit) {
          const cached = await hit.promise;
          audit({
            tool: "run_command",
            session: sessionRef.sessionId,
            requestId,
            dedupe: true,
            command,
          });
          if (cached && cached.__job)
            return textResult(
              tr("DEDUPE: requestId was already used, the command was NOT run again.\n", "DEDUPE: requestId уже использовался, команда НЕ выполнялась заново.\n") +
                cached.text
            );
          return textResult(
            formatRun(
              cached,
              maxOut,
              tr("DEDUPE: requestId was already used, the command was NOT run again. The saved result follows.", "DEDUPE: requestId уже использовался, команда НЕ выполнялась заново. Ниже сохранённый результат.")
            )
          );
        }

        const started = startShell({ command, cwd, timeoutMs });

        started.promise.then((st) => {
          audit({
            tool: "run_command",
            session: sessionRef.sessionId,
            requestId: requestId || null,
            command,
            cwd: st.cwd,
            exitCode: st.exitCode,
            status: st.status,
            durationMs: (st.finishedAt || Date.now()) - st.startedAt,
            stdoutBytes: st.stdoutBytes,
            stderrBytes: st.stderrBytes,
            ok: st.exitCode === 0,
          });
        });

        const msgWaiter = createAgentMessageWaiter(slot, "interrupted_command");
        const msgRacePromise = msgWaiter.promise.then((m) => ({ __ownerMsg: m }));
        try {
          if (typeof wait === "number" && wait >= 0) {
            const marker = Symbol("timeout");
            const raced = await Promise.race([
              started.promise,
              msgRacePromise,
              new Promise((r) => setTimeout(() => r(marker), wait * 1000)),
            ]);
            if (raced && raced.__ownerMsg) {
              const msg = raced.__ownerMsg;
              if (msg.stopCommand) {
                killTree(started.child);
                return formatInterruptedCommandResult(msg, null, command, true, slot);
              }
              const jobId = keepAsJob(started.state);
              jobs.set(jobId, {
                id: jobId,
                state: started.state,
                promise: started.promise,
                createdAt: Date.now(),
              });
              msg.jobId = jobId;
              return formatInterruptedCommandResult(msg, jobId, command, false, slot);
            }
            if (raced === marker) {
              const jobId = crypto.randomUUID().slice(0, 8);
              jobs.set(jobId, {
                id: jobId,
                state: started.state,
                promise: started.promise,
                createdAt: Date.now(),
              });
              const text =
                "status: running\njob_id: " +
                jobId +
                tr("\nmessage: the command is running in the background, the connection has been released.\n", "\nmessage: команда выполняется в фоне, соединение освобождено.\n") +
                "next: job_status({ jobId: \"" +
                jobId +
                tr("\" }) to check the status, job_output({ jobId }) to fetch the output.\n", "\" }) чтобы проверить статус, job_output({ jobId }) чтобы забрать вывод.\n") +
                "command: " +
                command;
              dedupeSet(requestId, Promise.resolve({ __job: true, text }));
              return textResult(text);
            }
            dedupeSet(requestId, Promise.resolve(raced));
            return textResult(formatRun(raced, maxOut));
          }

          dedupeSet(requestId, started.promise);
          const raced = await Promise.race([started.promise, msgRacePromise]);
          if (raced && raced.__ownerMsg) {
            const msg = raced.__ownerMsg;
            if (msg.stopCommand) {
              killTree(started.child);
              return formatInterruptedCommandResult(msg, null, command, true, slot);
            }
            const jobId = keepAsJob(started.state);
            jobs.set(jobId, {
              id: jobId,
              state: started.state,
              promise: started.promise,
              createdAt: Date.now(),
            });
            msg.jobId = jobId;
            return formatInterruptedCommandResult(msg, jobId, command, false, slot);
          }
          return textResult(formatRun(raced, maxOut));
        } finally {
          msgWaiter.cleanup();
        }
      } catch (e) {
        audit({
          tool: "run_command",
          session: sessionRef.sessionId,
          command,
          error: String((e && e.message) || e),
          ok: false,
        });
        return errResult(e);
      }
    }
  );

  /* ------------------------------ job_status ----------------------------- */
  reg(
    "job_status",
    {
      title: "Job status",
      description:
        "Check the status of a background command started by run_command with wait=<seconds>. " +
        "Returns status (running/completed/timeout/failed), exit_code and duration. " +
        "DO NOT poll in a tight loop. Pass waitSec (1-50): the server holds the answer until the job " +
        "finishes or the time runs out, so one call replaces dozens. " +
        "Pass tailLines to see the last lines of output and confirm real progress. " +
        "Use job_output for the full log.",
      inputSchema: {
        jobId: z.string(),
        waitSec: z.number().optional(),
        tailLines: z.number().optional(),
      },
      // Only reads job state (it may wait up to waitSec, but changes nothing).
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ jobId, waitSec, tailLines }, extra) => {
      const slot = extra?.agentSlot || resolveAgentSlot(sessionRef);
      const job = jobs.get(jobId);
      if (!job)
        return textResult(
          tr("ERROR: job not found (possibly expired after the 6-hour TTL or the service restarted): ", "ERROR: job не найден (возможно, истёк TTL 6 часов или сервис перезапускался): ") +
            jobId,
          true
        );
      const waitedFrom = Date.now();
      const waitMs =
        Math.min(Math.max(Number(waitSec) || 0, 0), MAX_JOB_WAIT_SEC) * 1000;
      const deadline = waitedFrom + waitMs;
      let interruptedMsg = matchAndClaimMessage(slot, "interrupted_job_wait");
      while (!interruptedMsg && job.state.status === "running" && Date.now() < deadline) {
        await new Promise((r) => setTimeout(r, 100));
        interruptedMsg = matchAndClaimMessage(slot, "interrupted_job_wait");
      }
      const s = job.state;
      const nTail = Math.min(Math.max(Number(tailLines) || 0, 0), 200);
      const tailOf = (text) => {
        const arr = String(text || "").split("\n");
        while (arr.length && arr[arr.length - 1] === "") arr.pop();
        if (!arr.length) return tr("(empty)", "(пусто)");
        let out = arr.slice(-nTail).join("\n");
        if (out.length > 8192) out = "..." + out.slice(-8192);
        return out;
      };
      const lines = [];
      if (interruptedMsg) {
        const hdr = formatSenderHeader(interruptedMsg);
        lines.push(`INTERRUPTED_BY_OWNER ${hdr}`);
        lines.push("--- message ---");
        lines.push(interruptedMsg.text);
        lines.push("--- job_status_snapshot ---");
      }
      lines.push(
        "job_id: " + jobId,
        "status: " + s.status,
        "exit_code: " + (s.exitCode === null ? tr("(still running)", "(ещё выполняется)") : s.exitCode),
        "timed_out: " + s.timedOut,
        "duration_ms: " + ((s.finishedAt || Date.now()) - s.startedAt),
        "waited_ms: " + (Date.now() - waitedFrom),
        "stdout_bytes: " + s.stdoutBytes,
        "stderr_bytes: " + s.stderrBytes,
        "cwd: " + s.cwd,
        "command: " + s.command
      );
      if (nTail > 0) {
        lines.push("--- stdout tail (" + nTail + tr(" lines) ---", " строк) ---"));
        lines.push(tailOf(s.stdout));
        lines.push("--- stderr tail (" + nTail + tr(" lines) ---", " строк) ---"));
        lines.push(tailOf(s.stderr));
      }
      if (s.status === "running") {
        lines.push(
          tr("hint: the task is still running. Call job_status again with waitSec: ", "подсказка: задача ещё идёт. Вызови job_status снова с waitSec: ") +
            MAX_JOB_WAIT_SEC +
            tr(" (and tailLines: 20, to see progress). Do not poll more often — it only burns context.", " (и tailLines: 20, чтобы видеть прогресс). Не опрашивай чаще — это только жжёт контекст.")
        );
      }
      return textResult(lines.join("\n"));
    }
  );

  /* ------------------------------ job_output ----------------------------- */
  reg(
    "job_output",
    {
      title: "Job output",
      description:
        "Fetch output of a background job, or of a finished run_command whose result was truncated (it reports a job_id). Supports paging through large output with offsetBytes/maxBytes, " +
        "so nothing is lost even for very long build logs.",
      inputSchema: {
        jobId: z.string(),
        stream: z.enum(["stdout", "stderr", "both"]).optional(),
        offsetBytes: z.number().optional(),
        maxBytes: z.number().optional(),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ jobId, stream, offsetBytes, maxBytes }) => {
      const job = jobs.get(jobId);
      if (!job) return textResult(tr("ERROR: job not found: ", "ERROR: job не найден: ") + jobId, true);
      const s = job.state;
      const which = stream || "both";
      const off = offsetBytes || 0;
      const cap = maxBytes || DEFAULT_MAX_OUTPUT;
      const pick = (text) => {
        const buf = Buffer.from(text || "", "utf8");
        const slice = buf.subarray(off, off + cap);
        return {
          text: slice.toString("utf8"),
          total: buf.length,
          nextOffset: off + slice.length,
          hasMore: off + slice.length < buf.length,
        };
      };
      const lines = [
        "job_id: " + jobId,
        "status: " + s.status,
        "exit_code: " + (s.exitCode === null ? tr("(still running)", "(ещё выполняется)") : s.exitCode),
      ];
      if (which === "stdout" || which === "both") {
        const p = pick(s.stdout);
        lines.push(
          "stdout_total_bytes: " +
            p.total +
            " | next_offset: " +
            p.nextOffset +
            " | has_more: " +
            p.hasMore
        );
        lines.push("--- stdout ---");
        lines.push(p.text || tr("(empty)", "(пусто)"));
      }
      if (which === "stderr" || which === "both") {
        const p = pick(s.stderr);
        lines.push(
          "stderr_total_bytes: " +
            p.total +
            " | next_offset: " +
            p.nextOffset +
            " | has_more: " +
            p.hasMore
        );
        lines.push("--- stderr ---");
        lines.push(p.text || tr("(empty)", "(пусто)"));
      }
      return textResult(lines.join("\n"));
    }
  );

  /* ------------------------------- read_file ----------------------------- */
  reg(
    "read_file",
    {
      title: "Read file",
      description:
        "Read a UTF-8 text file on this computer. Supports partial reads via startLine/endLine and a maxBytes cap " +
        "(default 200 KB) so large files never flood the context. Returns size, total line count and sha256 " +
        "(pass that sha256 back to write_file/edit_file as expectedSha256 for safe concurrent edits). Binary files are detected and refused.",
      inputSchema: {
        path: z.string(),
        startLine: z.number().optional(),
        endLine: z.number().optional(),
        maxBytes: z.number().optional(),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ path: filePath, startLine, endLine, maxBytes }) => {
      try {
        const fullPath = resolvePath(filePath);
        const st = fs.statSync(fullPath);
        if (st.isDirectory())
          return textResult(
            tr("ERROR: this is a directory, use list_directory: ", "ERROR: это директория, используй list_directory: ") + fullPath,
            true
          );
        const buf = fs.readFileSync(fullPath);
        const probe = buf.subarray(0, 8000);
        if (probe.includes(0))
          return textResult(
            tr("ERROR: the file looks binary (null byte found), reading as text cancelled.\npath: ", "ERROR: файл выглядит бинарным (найден нулевой байт), чтение как текста отменено.\npath: ") +
              fullPath +
              "\nsize_bytes: " +
              st.size,
            true
          );

        const full = buf.toString("utf8");
        const allLines = full.split("\n");
        const totalLines = allLines.length;
        const sha = sha256(buf);
        const cap = maxBytes || DEFAULT_READ_MAX_BYTES;

        let body = full;
        let rangeNote = "lines: 1-" + totalLines + tr(" (whole file)", " (весь файл)");
        if (startLine || endLine) {
          const s = Math.max(1, startLine || 1);
          const e = Math.min(totalLines, endLine || totalLines);
          body = allLines.slice(s - 1, e).join("\n");
          rangeNote = "lines: " + s + "-" + e + tr(" of ", " из ") + totalLines;
        }
        const clamped = clampText(body, cap);

        return textResult(
          [
            "path: " + fullPath,
            "size_bytes: " + st.size,
            "total_lines: " + totalLines,
            rangeNote,
            "sha256: " + sha,
            "mtime: " + st.mtime.toISOString(),
            "truncated: " + clamped.truncated,
            "--- content ---",
            clamped.text,
          ].join("\n")
        );
      } catch (e) {
        return errResult(e);
      }
    }
  );

  /* ---------------------------- list_directory --------------------------- */
  reg(
    "list_directory",
    {
      title: "List directory",
      description:
        "List files and folders on this computer with size and mtime. Supports depth (recursive listing) and a result limit. " +
        "Heavy folders (node_modules, .git, dist, build, ...) are skipped when recursing unless includeIgnored is true.",
      inputSchema: {
        path: z.string().optional(),
        depth: z.number().optional(),
        limit: z.number().optional(),
        showHidden: z.boolean().optional(),
        includeIgnored: z.boolean().optional(),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ path: dirPath, depth, limit, showHidden, includeIgnored }) => {
      try {
        const root = resolvePath(dirPath || PROJECT_DIR);
        const maxDepth = Math.max(1, depth || 1);
        const cap = limit || 300;
        const rows = [];
        let truncated = false;
        let skipped = 0;

        const walk = (dir, level, prefix) => {
          if (truncated) return;
          let items;
          try {
            items = fs.readdirSync(dir, { withFileTypes: true });
          } catch (e) {
            rows.push("error\t" + prefix + "  (" + e.message + ")");
            return;
          }
          items.sort((a, b) => {
            const ad = a.isDirectory() ? 0 : 1;
            const bd = b.isDirectory() ? 0 : 1;
            return ad - bd || a.name.localeCompare(b.name);
          });
          for (const item of items) {
            if (!showHidden && item.name.startsWith(".")) continue;
            if (rows.length >= cap) {
              truncated = true;
              return;
            }
            const abs = path.join(dir, item.name);
            const rel = prefix + item.name;
            const kind = item.isDirectory()
              ? "dir"
              : item.isFile()
                ? "file"
                : item.isSymbolicLink()
                  ? "symlink"
                  : "other";
            let size = "-";
            let mtime = "-";
            try {
              const s = fs.lstatSync(abs);
              size = item.isDirectory() ? "-" : String(s.size);
              mtime = s.mtime.toISOString().slice(0, 16).replace("T", " ");
            } catch {}
            rows.push(kind + "\t" + size + "\t" + mtime + "\t" + rel);
            if (item.isDirectory() && level < maxDepth) {
              if (!includeIgnored && EXCLUDE_DIRS.includes(item.name)) {
                skipped++;
                continue;
              }
              walk(abs, level + 1, rel + "/");
            }
          }
        };

        walk(root, 1, "");

        return textResult(
          [
            "path: " + root,
            "depth: " + maxDepth,
            "entries: " +
              rows.length +
              (truncated ? tr(" (reached limit ", " (достигнут limit ") + cap + tr(", list truncated)", ", список обрезан)") : ""),
            skipped ? "skipped_heavy_dirs: " + skipped : null,
            "columns: type\tsize\tmtime\tname",
            "--- entries ---",
            rows.join("\n") || tr("(empty)", "(пусто)"),
          ]
            .filter(Boolean)
            .join("\n")
        );
      } catch (e) {
        return errResult(e);
      }
    }
  );

  /* ------------------------------ search_files --------------------------- */
  reg(
    "search_files",
    {
      title: "Search files",
      description:
        "Search text in one file or across the files of a folder on this computer (path may point to either). Returns file:line matches with optional surrounding context. " +
        "Heavy folders (node_modules, .git, dist, build, ...) are excluded by default, binary files are skipped. " +
        "Use include ('*.ts') to narrow by file mask and regex=true for regular expressions. Always reports the true match count.",
      inputSchema: {
        query: z.string(),
        path: z.string().optional(),
        include: z.string().optional(),
        regex: z.boolean().optional(),
        ignoreCase: z.boolean().optional(),
        contextLines: z.number().optional(),
        maxResults: z.number().optional(),
        includeIgnored: z.boolean().optional(),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({
      query,
      path: searchPath,
      include,
      regex,
      ignoreCase,
      contextLines,
      maxResults,
      includeIgnored,
    }) => {
      try {
        const root = resolvePath(searchPath || PROJECT_DIR);
        if (!fs.existsSync(root)) return errResult(new Error(tr("path not found: ", "путь не найден: ") + root));
        const cap = maxResults || 60;
        if (IS_WIN || process.env.MCP_FORCE_NODE_SEARCH === "1") {
          return textResult(nodeSearchFiles({ query, root, include, regex, ignoreCase, contextLines, cap, includeIgnored }));
        }
        const args = ["-rn", "--binary-files=without-match"];
        if (ignoreCase) args.push("-i");
        args.push(regex ? "-E" : "-F");
        if (contextLines) args.push("-C", String(contextLines));
        if (!includeIgnored)
          for (const d of EXCLUDE_DIRS) args.push("--exclude-dir=" + d);
        if (include) args.push("--include=" + include);
        args.push("-e", query, root);

        const res = spawnSync("grep", args, {
          maxBuffer: 32 * 1024 * 1024,
          encoding: "utf8",
          timeout: 120000,
        });

        if (res.error) return errResult(res.error);

        const out = res.stdout || "";
        const allLines = out.split("\n").filter(Boolean);
        const shown = allLines.slice(0, cap);
        const files = new Set(
          allLines.map((l) => l.split(":")[0]).filter(Boolean)
        );

        return textResult(
          [
            "query: " + query + (regex ? "  (regex)" : "  (literal)"),
            "root: " + root,
            include ? "include: " + include : null,
            "matches_total: " + allLines.length,
            "files_with_matches: " + files.size,
            "showing: " +
              shown.length +
              (allLines.length > shown.length
                ? tr(" (truncated at maxResults=", " (обрезано по maxResults=") +
                  cap +
                  tr(", increase maxResults or narrow the query)", ", увеличь maxResults или сузь запрос)")
                : ""),
            allLines.length === 0 ? tr("note: no matches (this is not an error)", "note: совпадений нет (это не ошибка)") : null,
            "--- matches (file:line:text) ---",
            shown.join("\n") || tr("(no matches)", "(нет совпадений)"),
          ]
            .filter(Boolean)
            .join("\n")
        );
      } catch (e) {
        return errResult(e);
      }
    }
  );

  /* ------------------------------- write_file ---------------------------- */
  reg(
    "write_file",
    {
      title: "Write file",
      description:
        "Write a UTF-8 text file on this computer ATOMICALLY (temp file + rename, so a dropped connection never leaves a half-written file). Creates or overwrites the file. " +
        "Pass expectedSha256 (from read_file) to refuse the write if the file changed meanwhile — this prevents silently overwriting " +
        "work done by another agent in parallel. The previous version is kept in ZeroIDE history (not next to the file), so the owner can undo the change in Settings → Changes; backup=false skips that. Use dryRun=true to preview a diff without writing.",
      inputSchema: {
        path: z.string(),
        content: z.string(),
        expectedSha256: z.string().optional(),
        dryRun: z.boolean().optional(),
        backup: z.boolean().optional(),
        requestId: z.string().optional(),
      },
      // Creates or overwrites files: not read-only, and destructive because an
      // overwrite replaces content. The same path+content again = the same file.
      annotations: {
        readOnlyHint: false,
        destructiveHint: true,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({
      path: filePath,
      content,
      expectedSha256,
      dryRun,
      backup,
      requestId,
    }, extra) => {
      try {
        const fullPath = resolvePath(filePath);
        const exists = fs.existsSync(fullPath);
        const currentSha = exists ? fileSha(fullPath) : null;

        const hit = dedupeGet(requestId);
        if (hit) {
          const cached = await hit.promise;
          return textResult(
            tr("DEDUPE: requestId was already used, the write was NOT repeated.\n", "DEDUPE: requestId уже использовался, запись НЕ повторялась.\n") +
              cached
          );
        }

        if (expectedSha256 && currentSha !== expectedSha256) {
          audit({
            tool: "write_file",
            session: sessionRef.sessionId,
            path: fullPath,
            ok: false,
            reason: "sha_mismatch",
            expected: expectedSha256,
            actual: currentSha,
          });
          return textResult(
            [
              tr("DENIED: the file changed since reading — write cancelled to avoid overwriting someone else's edit.", "ОТКАЗ: файл изменился с момента чтения — запись отменена, чтобы не затереть чужую правку."),
              "path: " + fullPath,
              "expected_sha256: " + expectedSha256,
              "actual_sha256: " + (currentSha || tr("(no file)", "(файла нет)")),
              tr("next: re-read the file via read_file, rebase your edit onto the current version and retry.", "next: перечитай файл через read_file, перенеси свою правку на актуальную версию и повтори."),
            ].join("\n"),
            true
          );
        }

        if (dryRun) {
          const oldText = exists ? fs.readFileSync(fullPath, "utf8") : "";
          const diffOut = clampText(makeUnifiedDiff(oldText, content, fullPath), 60 * 1024);
          return textResult(
            [
              tr("DRY RUN — nothing written.", "DRY RUN — ничего не записано."),
              "path: " + fullPath,
              "exists: " + exists,
              "current_sha256: " + (currentSha || "-"),
              "new_size_bytes: " + Buffer.byteLength(content, "utf8"),
              "--- diff ---",
              diffOut.text,
            ].join("\n")
          );
        }

        const work = (async () => {
          const dir = path.dirname(fullPath);
          fs.mkdirSync(dir, { recursive: true });
          const bytesBefore = exists ? fs.statSync(fullPath).size : 0;
          const kept = keepBeforeChange(fullPath, backup);
          const tmp = path.join(
            dir,
            "." + path.basename(fullPath) + ".tmp-" + crypto.randomUUID().slice(0, 8)
          );
          fs.writeFileSync(tmp, content, "utf8");
          fs.renameSync(tmp, fullPath);
          const newSha = fileSha(fullPath);
          const bytes = Buffer.byteLength(content, "utf8");
          historyAppend({
            id: kept.id,
            op: exists ? "modify" : "create",
            tool: "write_file",
            path: fullPath,
            kind: "file",
            backup: kept.backup,
            beforeSha: currentSha,
            afterSha: newSha,
            bytesBefore,
            bytesAfter: bytes,
            session: sessionRef.sessionId,
            agent: extra?.agentSlot?.agentName || "",
            note: extra?.note || "",
            risk: extra?.risk && extra.risk.level !== "low" ? extra.risk.level : undefined,
          });
          audit({
            tool: "write_file",
            session: sessionRef.sessionId,
            requestId: requestId || null,
            path: fullPath,
            bytes,
            backup: kept.backup || kept.sidecar,
            change: kept.id,
            newSha,
            ok: true,
          });
          return [
            tr("OK: file written atomically (tmp + rename).", "OK: файл записан атомарно (tmp + rename)."),
            "path: " + fullPath,
            "bytes: " + bytes,
            "new_sha256: " + newSha,
            kept.backup || kept.sidecar
              ? "backup: " + (kept.backup || kept.sidecar)
              : exists ? tr("backup: (not created)", "backup: (не создавался)") : tr("backup: (new file)", "backup: (новый файл)"),
            "change_id: " + kept.id + tr("  (the owner can undo it in Settings → Changes)", "  (владелец может откатить в Настройки → «Изменения»)"),
          ].join("\n");
        })();

        dedupeSet(requestId, work);
        return textResult(await work);
      } catch (e) {
        audit({
          tool: "write_file",
          session: sessionRef.sessionId,
          path: filePath,
          ok: false,
          error: String((e && e.message) || e),
        });
        return errResult(e);
      }
    }
  );

  /* -------------------------------- edit_file ---------------------------- */
  reg(
    "edit_file",
    {
      title: "Edit file",
      description:
        "Replace exact text fragments in a file instead of rewriting the whole file. " +
        "One edit: oldString/newString. Several edits in one atomic change: edits=[{oldString,newString,replaceAll?}] — applied in order, all or nothing. " +
        "Refuses an edit whose oldString is not found or found more than once (unless replaceAll), so it can never patch the wrong place. " +
        "Supports expectedSha256 (safe concurrent editing) and dryRun preview; the previous version is kept in ZeroIDE history for undo. Write is atomic.",
      inputSchema: {
        path: z.string(),
        oldString: z.string().optional(),
        newString: z.string().optional(),
        replaceAll: z.boolean().optional().describe("Single-edit mode: replace every occurrence of oldString instead of requiring exactly one."),
        edits: z
          .array(z.object({ oldString: z.string(), newString: z.string(), replaceAll: z.boolean().optional() }))
          .max(100)
          .optional()
          .describe("Several replacements applied in order as one atomic change. Use instead of oldString/newString."),
        expectedSha256: z.string().optional(),
        dryRun: z.boolean().optional(),
        backup: z.boolean().optional(),
        requestId: z.string().optional(),
      },
      // Replaces text in place: destructive, and repeating the same call is not
      // guaranteed to be a no-op (newString may contain oldString).
      annotations: {
        readOnlyHint: false,
        destructiveHint: true,
        idempotentHint: false,
        openWorldHint: false,
      },
    },
    async ({
      path: filePath,
      oldString,
      newString,
      replaceAll,
      edits,
      expectedSha256,
      dryRun,
      backup,
      requestId,
    }, extra) => {
      try {
        const fullPath = resolvePath(filePath);
        if (!fs.existsSync(fullPath))
          return textResult(tr("ERROR: file not found: ", "ERROR: файл не найден: ") + fullPath, true);

        const hit = dedupeGet(requestId);
        if (hit) {
          const cached = await hit.promise;
          return textResult(
            tr("DEDUPE: requestId was already used, the edit was NOT applied again.\n", "DEDUPE: requestId уже использовался, правка НЕ применялась повторно.\n") +
              cached
          );
        }

        const buf = fs.readFileSync(fullPath);
        const currentSha = sha256(buf);
        const text = buf.toString("utf8");

        if (expectedSha256 && currentSha !== expectedSha256) {
          audit({
            tool: "edit_file",
            session: sessionRef.sessionId,
            path: fullPath,
            ok: false,
            reason: "sha_mismatch",
          });
          return textResult(
            [
              tr("DENIED: the file changed since reading — edit cancelled to avoid overwriting someone else's work.", "ОТКАЗ: файл изменился с момента чтения — правка отменена, чтобы не затереть чужую работу."),
              "path: " + fullPath,
              "expected_sha256: " + expectedSha256,
              "actual_sha256: " + currentSha,
              tr("next: re-read the file via read_file and repeat the edit on the current version.", "next: перечитай файл через read_file и повтори правку на актуальной версии."),
            ].join("\n"),
            true
          );
        }

        const editArgs = { oldString, newString, replaceAll, edits };
        const editCount = Array.isArray(edits) && edits.length ? edits.length : 1;
        const prep = prepareEdit(fullPath, editArgs);
        if (!prep.ok) return editFailureResult(fullPath, prep, editCount);
        const updated = prep.updated;

        if (dryRun) {
          return textResult(
            [
              tr("DRY RUN — nothing written.", "DRY RUN — ничего не записано."),
              "path: " + fullPath,
              tr("edits: ", "правок: ") + prep.edits + tr(", replacements: ", ", замен: ") + prep.replaced + tr(" (ok)", " (ок)"),
              "current_sha256: " + currentSha,
              "--- diff ---",
              clampText(makeUnifiedDiff(text, updated, fullPath), 60 * 1024).text,
            ].join("\n")
          );
        }

        const work = (async () => {
          const dir = path.dirname(fullPath);
          const kept = keepBeforeChange(fullPath, backup);
          const tmp = path.join(
            dir,
            "." + path.basename(fullPath) + ".tmp-" + crypto.randomUUID().slice(0, 8)
          );
          fs.writeFileSync(tmp, updated, "utf8");
          fs.renameSync(tmp, fullPath);
          const newSha = fileSha(fullPath);
          historyAppend({
            id: kept.id,
            op: "modify",
            tool: "edit_file",
            path: fullPath,
            kind: "file",
            backup: kept.backup,
            beforeSha: currentSha,
            afterSha: newSha,
            bytesBefore: buf.length,
            bytesAfter: Buffer.byteLength(updated, "utf8"),
            edits: prep.edits,
            session: sessionRef.sessionId,
            agent: extra?.agentSlot?.agentName || "",
            note: extra?.note || "",
            risk: extra?.risk && extra.risk.level !== "low" ? extra.risk.level : undefined,
          });
          audit({
            tool: "edit_file",
            session: sessionRef.sessionId,
            requestId: requestId || null,
            path: fullPath,
            edits: prep.edits,
            replacements: prep.replaced,
            change: kept.id,
            newSha,
            ok: true,
          });
          return [
            prep.edits > 1 || prep.replaced > 1
              ? tr("OK: applied " + prep.edits + " edit(s), " + prep.replaced + " replacement(s), file written atomically.", "OK: применено правок: " + prep.edits + ", замен: " + prep.replaced + ", файл записан атомарно.")
              : tr("OK: replaced 1 occurrence, file written atomically.", "OK: заменено 1 вхождение, файл записан атомарно."),
            "path: " + fullPath,
            "old_sha256: " + currentSha,
            "new_sha256: " + newSha,
            kept.backup || kept.sidecar ? "backup: " + (kept.backup || kept.sidecar) : tr("backup: (not created)", "backup: (не создавался)"),
            "change_id: " + kept.id + tr("  (the owner can undo it in Settings → Changes)", "  (владелец может откатить в Настройки → «Изменения»)"),
          ].join("\n");
        })();

        dedupeSet(requestId, work);
        return textResult(await work);
      } catch (e) {
        audit({
          tool: "edit_file",
          session: sessionRef.sessionId,
          path: filePath,
          ok: false,
          error: String((e && e.message) || e),
        });
        return errResult(e);
      }
    }
  );

  /* ------------------------------- read_image ---------------------------- */
  reg(
    "read_image",
    {
      title: "View image",
      description:
        "Look at an image file on this PC (PNG, JPEG, GIF, BMP, ICO, WebP): returns the picture itself, so you can check screenshots, UI renders, charts or photos the owner points you to. " +
        "Large images are downscaled on the PC (maxSize, default 1600 px on the longer side; the file itself is not changed). Same folder rules as read_file; the owner may require approval because the picture leaves the PC.",
      inputSchema: {
        path: z.string(),
        maxSize: z.number().int().min(64).max(4096).optional().describe("Longest side in pixels after downscaling (default 1600)."),
        format: z.enum(["auto", "png", "jpeg"]).optional().describe("auto keeps JPEG photos as JPEG and everything else as PNG."),
      },
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ path: filePath, maxSize, format }) => {
      try {
        const fullPath = resolvePath(filePath);
        let st;
        try { st = fs.statSync(fullPath); } catch { return textResult(tr("ERROR: file not found: ", "ERROR: файл не найден: ") + fullPath, true); }
        if (!st.isFile()) return textResult(tr("ERROR: not a file: ", "ERROR: это не файл: ") + fullPath, true);
        if (st.size > IMAGE_MAX_SOURCE_BYTES) return textResult(tr("ERROR: the image is larger than 64 MB.", "ERROR: изображение больше 64 МБ."), true);
        const buf = fs.readFileSync(fullPath);
        const info = sniffImage(buf);
        if (!info) return textResult(tr("ERROR: not a supported image (PNG, JPEG, GIF, BMP, ICO, WebP): ", "ERROR: неподдерживаемый формат (нужен PNG, JPEG, GIF, BMP, ICO, WebP): ") + fullPath, true);
        const max = Math.max(64, Math.min(4096, Number(maxSize) || 1600));
        const fmt = format === "png" || format === "jpeg" ? format : "auto";
        const fits = info.width > 0 && info.height > 0 && Math.max(info.width, info.height) <= max;
        const sameFormat = fmt === "auto" || fmt === info.type;
        let data, mime, outW = info.width, outH = info.height, converted = false;
        if (["png", "jpeg", "gif", "webp"].includes(info.type) && buf.length <= IMAGE_PASSTHROUGH_BYTES && sameFormat && (fits || info.type === "webp")) {
          data = buf.toString("base64");
          mime = info.mime;
        } else if (!IS_WIN) {
          return textResult(tr("ERROR: downscaling needs Windows; ask for a smaller image.", "ERROR: уменьшение доступно только в Windows; попроси изображение поменьше."), true);
        } else if (info.type === "webp") {
          return textResult(tr("ERROR: this WebP image is too large to send (", "ERROR: это WebP-изображение слишком большое (") + fmtBytes(buf.length) + tr(") and WebP cannot be downscaled here. Convert it to PNG or JPEG first.", "), а WebP здесь не уменьшить. Сначала преобразуй его в PNG или JPEG."), true);
        } else {
          const target = fmt === "auto" ? (info.type === "jpeg" ? "jpeg" : "png") : fmt;
          let r = await psConvertImage(fullPath, max, target);
          if (r.ok && target === "png" && fmt === "auto" && r.base64.length > 4 * 1024 * 1024) r = await psConvertImage(fullPath, max, "jpeg");
          if (!r.ok) return textResult(tr("ERROR: could not read the image: ", "ERROR: не удалось прочитать изображение: ") + r.error, true);
          data = r.base64;
          mime = r.mime;
          outW = r.w;
          outH = r.h;
          converted = true;
          if (!info.width) { info.width = r.srcW; info.height = r.srcH; }
        }
        audit({ tool: "read_image", session: sessionRef.sessionId, path: fullPath, bytes: buf.length, converted, ok: true });
        const lines = [
          "path: " + fullPath,
          tr("format: ", "формат: ") + info.type + (converted ? " → " + (mime === "image/jpeg" ? "jpeg" : "png") : ""),
          tr("size: ", "размер: ") + (info.width && info.height ? info.width + "×" + info.height : "?") + (converted && (outW !== info.width || outH !== info.height) ? " → " + outW + "×" + outH : "") + " px",
          tr("file: ", "файл: ") + fmtBytes(buf.length) + ", sha256: " + sha256(buf).slice(0, 16) + "…",
        ];
        return { content: [{ type: "text", text: lines.join("\n") }, { type: "image", data, mimeType: mime }] };
      } catch (e) {
        audit({ tool: "read_image", session: sessionRef.sessionId, path: filePath, ok: false, error: String((e && e.message) || e) });
        return errResult(e);
      }
    }
  );

  /* -------------------------------- file_ops ----------------------------- */
  reg(
    "file_ops",
    {
      title: "File operations",
      description:
        "Copy, move/rename or delete files and folders, or create a folder, without a shell. " +
        "delete never destroys data: the files move to ZeroIDE history and the owner can restore them in Settings → Changes. " +
        "Anything replaced with overwrite=true keeps its previous version too. Prefer this to Remove-Item/Move-Item in run_command: it is reversible and the owner sees exactly what changes.",
      inputSchema: {
        op: z.enum(["copy", "move", "delete", "mkdir"]),
        path: z.string().describe("Source (copy/move/delete) or the folder to create (mkdir). Absolute or relative to the project dir."),
        to: z.string().optional().describe("Destination for copy and move."),
        overwrite: z.boolean().optional().describe("copy/move: replace an existing destination file; its previous version is kept in history. Default false."),
        recursive: z.boolean().optional().describe("Required to copy or delete a folder with its contents."),
      },
      annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false },
    },
    async ({ op, path: srcRaw, to, overwrite, recursive }, extra) => {
      const src = resolvePath(srcRaw);
      const dst = to ? resolvePath(to) : "";
      const meta = { session: sessionRef.sessionId, agent: extra?.agentSlot?.agentName || "", note: extra?.note || "" };
      const fail = (code, message, hint) => {
        audit({ tool: "file_ops", session: sessionRef.sessionId, op, path: src, to: dst || undefined, ok: false, reason: code });
        return textResult([message, "op: " + op, "path: " + src, dst ? "to: " + dst : null, hint ? "hint: " + hint : null].filter(Boolean).join("\n"), true);
      };
      try {
        if (op === "mkdir") {
          const existed = fs.existsSync(src);
          if (existed && !fs.statSync(src).isDirectory()) return fail("NOT_A_FOLDER", tr("ERROR: a file with this name already exists.", "ОШИБКА: файл с таким именем уже существует."));
          fs.mkdirSync(src, { recursive: true });
          if (!existed) historyAppend({ id: historyNewId(), op: "mkdir", tool: "file_ops", path: src, kind: "dir", ...meta });
          audit({ tool: "file_ops", session: sessionRef.sessionId, op, path: src, existed, ok: true });
          return textResult([existed ? tr("OK: the folder already exists.", "OK: папка уже существует.") : tr("OK: folder created.", "OK: папка создана."), "path: " + src].join("\n"));
        }
        if (!fs.existsSync(src)) return fail("NOT_FOUND", tr("ERROR: path not found.", "ОШИБКА: путь не найден."));
        const st = fs.statSync(src);
        const isDir = st.isDirectory();
        if (op === "delete") {
          const guard = deleteGuard(src);
          if (guard) return fail("REFUSED", guard);
          if (isDir && !recursive) return fail("FOLDER_NEEDS_RECURSIVE", tr("ERROR: this is a folder; pass recursive: true to delete it with its contents.", "ОШИБКА: это папка; передай recursive: true, чтобы удалить её вместе с содержимым."));
          const stats = isDir ? measureTree(src, TRASH_MAX_FILES + 1) : { files: 1, bytes: st.size };
          if (stats.files > TRASH_MAX_FILES || stats.bytes > TRASH_MAX_BYTES)
            return fail("TOO_LARGE_FOR_HISTORY", tr("DENIED: too large to keep a recoverable copy (more than 20,000 files or 2 GB).", "ОТКАЗ: слишком большой объём для восстанавливаемой копии (больше 20 000 файлов или 2 ГБ)."), tr("delete smaller parts, or ask the owner to remove it by hand.", "удаляй частями или попроси владельца удалить это вручную."));
          const id = historyNewId();
          const keepDir = path.join(HISTORY_DIR, "trash", historyDay(), id);
          fs.mkdirSync(keepDir, { recursive: true });
          const keptAt = path.join(keepDir, path.basename(src));
          const beforeSha = isDir ? null : fileSha(src);
          movePath(src, keptAt);
          historyAppend({ id, op: "delete", tool: "file_ops", path: src, kind: isDir ? "dir" : "file", backup: keptAt, beforeSha, files: stats.files, bytes: stats.bytes, risk: extra?.risk && extra.risk.level !== "low" ? extra.risk.level : undefined, ...meta });
          audit({ tool: "file_ops", session: sessionRef.sessionId, op, path: src, files: stats.files, bytes: stats.bytes, change: id, ok: true });
          return textResult(
            [
              tr("OK: deleted — moved to ZeroIDE history, recoverable.", "OK: удалено — перемещено в историю ZeroIDE, можно восстановить."),
              "path: " + src,
              tr("kind: ", "тип: ") + (isDir ? tr("folder", "папка") + ", " + stats.files + tr(" files", " файлов") : tr("file", "файл")) + ", " + fmtBytes(stats.bytes),
              "change_id: " + id + tr("  (the owner restores it in Settings → Changes)", "  (владелец восстанавливает это в Настройки → «Изменения»)"),
            ].join("\n")
          );
        }
        if (op === "copy" || op === "move") {
          if (!dst) return fail("NO_DESTINATION", tr("ERROR: pass `to` with the destination path.", "ОШИБКА: передай `to` — путь назначения."));
          const cs = canonicalLoose(src), cd = canonicalLoose(dst);
          if (cs === cd) return fail("SAME_PATH", tr("ERROR: source and destination are the same.", "ОШИБКА: источник и назначение совпадают."));
          if (isDir && insideDir(cs, cd)) return fail("INTO_ITSELF", tr("ERROR: a folder cannot be copied or moved into itself.", "ОШИБКА: папку нельзя копировать или переносить внутрь самой себя."));
          if (op === "copy" && isDir && !recursive) return fail("FOLDER_NEEDS_RECURSIVE", tr("ERROR: this is a folder; pass recursive: true to copy it with its contents.", "ОШИБКА: это папка; передай recursive: true, чтобы скопировать её вместе с содержимым."));
          if (op === "move") { const guard = deleteGuard(src); if (guard) return fail("REFUSED", guard); }
          const dstExists = fs.existsSync(dst);
          let replacedChange = null;
          if (dstExists) {
            if (!overwrite) return fail("DESTINATION_EXISTS", tr("DENIED: the destination already exists — nothing changed.", "ОТКАЗ: назначение уже существует — ничего не изменено."), tr("pass overwrite: true to replace it (its previous version is kept in history).", "передай overwrite: true, чтобы заменить (прежняя версия сохранится в истории)."));
            if (fs.statSync(dst).isDirectory() || isDir) return fail("FOLDER_OVERWRITE", tr("DENIED: replacing an existing folder is not supported. Delete it first (it goes to history) or choose another destination.", "ОТКАЗ: замена существующей папки не поддерживается. Сначала удали её (она попадёт в историю) или выбери другое место."));
            const kept = keepBeforeChange(dst, true);
            replacedChange = kept.id;
            historyAppend({ id: kept.id, op: "overwrite", tool: "file_ops", path: dst, kind: "file", backup: kept.backup, beforeSha: fileSha(dst), reason: op, ...meta });
          }
          const stats = isDir ? measureTree(src, 200001) : { files: 1, bytes: st.size };
          fs.mkdirSync(path.dirname(dst), { recursive: true });
          if (op === "copy") fs.cpSync(src, dst, { recursive: isDir, force: true, errorOnExist: false, preserveTimestamps: true });
          else {
            if (dstExists) fs.rmSync(dst, { force: true });
            movePath(src, dst);
          }
          const id = historyNewId();
          historyAppend({ id, op, tool: "file_ops", path: src, to: dst, kind: isDir ? "dir" : "file", files: stats.files, bytes: stats.bytes, replaced: replacedChange, afterSha: isDir ? null : fileSha(dst), ...meta });
          audit({ tool: "file_ops", session: sessionRef.sessionId, op, path: src, to: dst, files: stats.files, bytes: stats.bytes, overwrote: dstExists, change: id, ok: true });
          return textResult(
            [
              op === "copy" ? tr("OK: copied.", "OK: скопировано.") : tr("OK: moved.", "OK: перемещено."),
              "from: " + src,
              "to: " + dst,
              tr("kind: ", "тип: ") + (isDir ? tr("folder", "папка") + ", " + stats.files + tr(" files", " файлов") : tr("file", "файл")) + ", " + fmtBytes(stats.bytes),
              replacedChange ? tr("replaced file kept as change ", "заменённый файл сохранён как изменение ") + replacedChange : null,
              "change_id: " + id,
            ].filter(Boolean).join("\n")
          );
        }
        return fail("BAD_OP", tr("ERROR: unknown op.", "ОШИБКА: неизвестная операция."));
      } catch (e) {
        audit({ tool: "file_ops", session: sessionRef.sessionId, op, path: src, to: dst || undefined, ok: false, error: String((e && e.message) || e) });
        return errResult(e);
      }
    }
  );

  /* ------------------------------ system_info ---------------------------- */
  reg(
    "system_info",
    {
      title: "System info",
      description:
        "One read-only call for the context you would otherwise gather with several commands: Windows version, CPU, memory, disks, displays, user, time zone, PowerShell and installed developer tools (node, npm, python, git, dotnet, java, go, rust, docker, winget). " +
        "It also says how ZeroIDE is set up for you right now: project folder, folder confinement, your access scope, which tools run freely, ask the owner first or are disabled, active temporary grants and where undo history is kept. Call it once at the start of a task.",
      inputSchema: {
        refresh: z.boolean().optional().describe("Probe installed tools again instead of using the 10-minute cache."),
      },
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ refresh }, extra) => {
      try {
        if (refresh) systemInfoCache = null;
        const f = await collectSystemFacts();
        const perms = loadPermissions();
        const caller = extra?.caller || LEGACY_CALLER;
        const conf = extra?.confinement || {};
        const confined = !!conf.root;
        const modeOf = (t) => (perms.tools && perms.tools[t] && perms.tools[t].mode) || perms.defaultMode || "ask";
        const groups = { allow: [], ask: [], deny: [] };
        for (const t of ["read_file", "read_image", "list_directory", "search_files", "write_file", "edit_file", "file_ops", "run_command", "take_screenshot", "journal_entry"]) (groups[modeOf(t)] || groups.ask).push(t);
        pruneGrants();
        const grants = [...activeGrants.values()].map((g) => g.tool + (g.scope === "exact" ? tr(" (one exact call)", " (один конкретный вызов)") : "") + ", " + Math.max(1, Math.round((g.expiresAt - Date.now()) / 60000)) + tr(" min left", " мин осталось"));
        const gate = { ...DEFAULT_PERMISSIONS.riskGate, ...(perms.riskGate || {}) };
        const found = new Set(f.tools.map(([n]) => n));
        const missing = (IS_WIN ? ["node", "npm", "python", "git", "dotnet", "java", "go", "rustc", "docker", "winget", "pwsh"] : ["node", "npm", "python3", "git", "dotnet", "docker"]).filter((n) => !found.has(n));
        const out = ["== " + tr("Machine", "Компьютер") + " ==", "os: " + f.os + " · " + f.arch, "cpu: " + f.cpu, tr("memory: ", "память: ") + fmtBytes(f.memTotal) + tr(" total, ", " всего, ") + fmtBytes(f.memFree) + tr(" free", " свободно")];
        if (!confined) {
          out.push(tr("user: ", "пользователь: ") + f.user + " @ " + f.host);
          for (const d of f.disks) out.push(tr("disk ", "диск ") + d.root + " " + fmtBytes(d.free) + tr(" free of ", " свободно из ") + fmtBytes(d.total));
          if (f.displays.length) out.push(tr("displays: ", "мониторы: ") + f.displays.join(", "));
        }
        out.push(tr("time: ", "время: ") + new Date().toLocaleString("sv-SE") + " (" + f.timeZone + "), locale " + f.locale + tr(", uptime ", ", работает ") + Math.floor(f.uptimeSec / 3600) + tr(" h", " ч"));
        out.push("", "== " + tr("Shell and tools", "Оболочка и инструменты") + " ==");
        if (f.powershell) out.push("powershell: " + f.powershell + (IS_WIN ? tr(" (run_command runs Windows PowerShell)", " (run_command запускает Windows PowerShell)") : ""));
        for (const [name, v] of f.tools) out.push(name + ": " + v);
        if (missing.length) out.push(tr("not found: ", "не найдены: ") + missing.join(", "));
        out.push("", "== ZeroIDE MCP ==");
        out.push(tr("server: ", "сервер: ") + SERVER_VERSION + tr(", messages in ", ", язык сообщений: ") + LANG);
        out.push(tr("project dir (base for relative paths): ", "папка проекта (база для относительных путей): ") + PROJECT_DIR);
        out.push(tr("your access: ", "твой доступ: ") + (caller.scope || "full") + (caller.label && caller.label !== "legacy" ? " (" + caller.label + ")" : ""));
        out.push(tr("folder confinement: ", "ограничение папкой: ") + (confined ? conf.root + tr(" (terminal disabled)", " (терминал отключён)") : tr("off", "выкл.")));
        out.push(tr("run without asking: ", "без подтверждения: ") + (groups.allow.join(", ") || "—"));
        out.push(tr("ask the owner first: ", "с подтверждением владельца: ") + (groups.ask.join(", ") || "—"));
        out.push(tr("disabled by the owner: ", "отключены владельцем: ") + (groups.deny.join(", ") || "—"));
        out.push(tr("approval timeout: ", "время на подтверждение: ") + (Number(perms.approvalTimeoutSec) || 60) + tr(" s", " с"));
        out.push(tr("active temporary grants: ", "временные разрешения: ") + (grants.join("; ") || "—"));
        const gateOff = gate.enabled === false || (gate.askAtOrAbove === "none" && gate.grantsNeverCover === "none");
        let gateDesc;
        if (gateOff) {
          gateDesc = tr("off (full autonomy for allowed tools)", "выкл. (полная автономия для разрешённых инструментов)");
        } else if (gate.askAtOrAbove === "critical") {
          gateDesc = gate.grantsNeverCover === "none"
            ? tr("critical calls still ask; temporary grants may cover them", "критичные вызовы спрашивают; временные разрешения могут их покрывать")
            : tr("high-risk runs silently when allowed; critical calls always ask (no temporary grant)", "высокий риск не спрашивает при разрешении; критичные вызовы всегда спрашивают (без временных разрешений)");
        } else {
          gateDesc = gate.grantsNeverCover === "none"
            ? tr("high-risk and critical calls ask on first use; temporary grants may cover them", "высокий и критичный риск спрашивают; временные разрешения могут их покрывать")
            : tr("high-risk calls always ask; no temporary grant covers a critical call", "вызовы с высоким риском всегда спрашивают; временные разрешения не покрывают критичные");
        }
        out.push(tr("risk check: ", "проверка риска: ") + gateDesc);
        out.push(tr("undo history: ", "история для отката: ") + HISTORY_DIR + " (" + HISTORY_RETENTION_DAYS + tr(" days; write_file, edit_file and file_ops are recorded)", " дн.; записываются write_file, edit_file и file_ops)"));
        out.push(tr("tip: group related replacements into one edit_file call (edits[]) so the owner approves once.", "совет: объединяй связанные замены в один вызов edit_file (edits[]), чтобы владелец подтверждал один раз."));
        return textResult(out.join("\n"));
      } catch (e) {
        return errResult(e);
      }
    }
  );

  /* ------------------------------- audit_log ----------------------------- */
  reg(
    "audit_log",
    {
      title: "Audit log",
      description:
        "Read the MCP audit trail: who (which session) ran which command or edited which file, when, and with what exit code. " +
        "USE THIS FIRST when something broke, when a file looks unexpectedly changed, or when you suspect another agent " +
        "worked in parallel. Filters: onlyErrors, tool, contains, sinceMinutes.",
      inputSchema: {
        limit: z.number().optional(),
        sinceMinutes: z.number().optional(),
        onlyErrors: z.boolean().optional(),
        tool: z.string().optional(),
        contains: z.string().optional(),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ limit, sinceMinutes, onlyErrors, tool, contains }) => {
      try {
        if (!fs.existsSync(AUDIT_FILE))
          return textResult(tr("Audit log is still empty: ", "Аудит-лог пока пуст: ") + AUDIT_FILE);
        const cap = limit || 50;
        const raw = fs.readFileSync(AUDIT_FILE, "utf8").split("\n").filter(Boolean);
        const cutoff = sinceMinutes ? Date.now() - sinceMinutes * 60000 : null;
        const out = [];
        for (let i = raw.length - 1; i >= 0 && out.length < cap; i--) {
          let e;
          try {
            e = JSON.parse(raw[i]);
          } catch {
            continue;
          }
          if (cutoff && new Date(e.ts).getTime() < cutoff) break;
          if (onlyErrors && e.ok !== false) continue;
          if (tool && e.tool !== tool) continue;
          if (contains && !raw[i].includes(contains)) continue;
          out.push(e);
        }
        const fmt = out
          .map((e) => {
            const what =
              e.command !== undefined
                ? "$ " + e.command
                : e.path || e.note || "";
            return [
              e.ts,
              e.tool || e.event || "-",
              e.event ? "event=" + e.event : null,
              "session=" + (e.session || "-"),
              e.by ? "by=" + e.by : null,
              e.grantScope ? "scope=" + e.grantScope : null,
              e.exitCode !== undefined ? "exit=" + e.exitCode : null,
              e.durationMs !== undefined ? e.durationMs + "ms" : null,
              e.ok === false ? "FAILED" : null,
              e.reason ? "reason=" + e.reason : null,
              e.dedupe ? "DEDUPE" : null,
              e.error ? "error=" + e.error : null,
              "| " + what,
            ]
              .filter(Boolean)
              .join("  ");
          })
          .join("\n");
        return textResult(
          [
            "file: " + AUDIT_FILE,
            "total_entries: " + raw.length,
            "returned: " + out.length + tr(" (newest first)", " (новые сверху)"),
            "--- entries ---",
            fmt || tr("(no entries match the filter)", "(нет записей по фильтру)"),
          ].join("\n")
        );
      } catch (e) {
        return errResult(e);
      }
    }
  );

  /* ------------------------------ journal_entry --------------------------- */
  reg(
    "journal_entry",
    {
      title: "Journal entry",
      description:
        tr("Write to the agent journal the outcome of a finished chunk of work. summary — 2-4 sentences: what and why. ", "Записать в журнал агента итог законченного куска работы. summary — 2-4 фразы: что и зачем. ") +
        tr("outcome — done/failed/partial/reverted. next — what is left (optional). commit — git hash, if the work produced a commit (optional). refs — arbitrary references (optional). ", "outcome — done/failed/partial/reverted. next — что осталось (опционально). commit — git-хеш, если работа родила коммит (опционально). refs — произвольные ссылки (опционально). ") +
        tr("The server itself attaches all audit entries of the SAME session since the previous journal entry of that same session ", "Сервер сам подшивает все audit-записи ТОЙ же session с момента предыдущей journal-записи этой же session ") +
        tr("(or since the start of the connection, if there was no entry yet) — there is no need to find and specify the range manually. ", "(или с начала соединения, если записи ещё не было) — диапазон искать и указывать вручную не нужно. ") +
        tr("If you are explicitly continuing an interrupted task from another (older) session, pass continuesFromSeq — ", "Если явно продолжаешь прерванную задачу из другой (более старой) session, передай continuesFromSeq — ") +
        tr("seq of the last accounted audit entry, so its tail is not lost.", "seq последней учтённой audit-записи, чтобы не терять её хвост."),
      inputSchema: {
        summary: z.string(),
        outcome: z.enum(["done", "failed", "partial", "reverted"]),
        next: z.string().optional(),
        commit: z.string().optional(),
        refs: z.array(z.string()).optional(),
        continuesFromSeq: z.number().optional(),
      },
      annotations: {
        readOnlyHint: false,
        destructiveHint: false,
        idempotentHint: false,
        openWorldHint: false,
      },
    },
    async ({ summary, outcome, next, commit, refs, continuesFromSeq }, extra) => {
      try {
        const slot = extra?.agentSlot;
        const session = sessionRef.sessionId;
        const effectiveSince =
          continuesFromSeq != null ? continuesFromSeq : lastJournalToSeq;
        const auditRange = readAuditSince(effectiveSince);
        const seq = nextJournalSeq();
        const entry = {
          seq,
          ts: ts(),
          session,
          summary: redactSecrets(summary),
          outcome,
          next: next ? redactSecrets(next) : null,
          commit: commit || null,
          refs: refs || [],
          auditRange,
        };
        fs.mkdirSync(LOG_DIR, { recursive: true });
        fs.appendFileSync(JOURNAL_FILE, JSON.stringify(entry) + "\n");
        audit({ tool: "journal_entry", session, ok: true, journalSeq: seq, outcome });
        lastJournalToSeq =
          auditRange.toSeq != null ? auditRange.toSeq : effectiveSince;
        mutatingSinceJournal = 0;
        const lines = [
          tr("OK: journal entry added.", "OK: запись в журнал добавлена."),
          "seq: " + seq,
          "session: " + session,
          "outcome: " + outcome,
          "audit_range: " +
            (auditRange.count
              ? "seq " + auditRange.fromSeq + "-" + auditRange.toSeq + " (" + auditRange.count + tr(" entries)", " записей)")
              : tr("(no new audit entries in this session)", "(нет новых audit-записей в этой session)")),
        ];
        if (slot && outcome === "done" && !slot.hasEverSentUserMessage) {
          lines.push(
            tr(
              `reminder: Send a short summary to the tray via user_message({ agent: "${slot.agentName}", text: "...", kind: "status" }) before finishing.`,
              `напоминание: Отправь краткий итог в трей через user_message({ agent: "${slot.agentName}", text: "...", kind: "status" }) перед завершением.`
            )
          );
        }
        if (slot && outcome === "done" && holdAfterTask && isAwaitOwnerEnabled()) {
          lines.push(
            tr(
              `[HOLD_AFTER_TASK_ENABLED_BY_OWNER]: The owner explicitly turned ON tray standby right now. Do NOT end your turn in chat — call await_owner({ agent: "${slot.agentName}", summary: "..." }) immediately!`,
              `[HOLD_AFTER_TASK_ENABLED_BY_OWNER]: Владелец вручную включил удержание в трее на 5 минут. НЕ закрывай ход в чате — сразу вызови await_owner({ agent: "${slot.agentName}", summary: "..." })!`
            )
          );
        }
        return textResult(lines.join("\n"));
      } catch (e) {
        return errResult(e);
      }
    }
  );

  /* ------------------------------- journal_log ---------------------------- */
  reg(
    "journal_log",
    {
      title: "Journal log",
      description:
        tr("Read the agent journal: what was done (summary), with what outcome (outcome), what is left (next), ", "Прочитать журнал агента: что было сделано (summary), с каким итогом (outcome), что осталось (next), ") +
        tr("and which range of audit entries was attached automatically to each entry. ", "и какой диапазон audit-записей подшит автоматически к каждой записи. ") +
        tr("Use at the start of work to quickly grasp recent context without reading the full audit_log.", "Используй в начале работы, чтобы быстро понять недавний контекст без чтения полного audit_log."),
      inputSchema: {
        limit: z.number().optional(),
        sinceMinutes: z.number().optional(),
        outcome: z.enum(["done", "failed", "partial", "reverted"]).optional(),
        session: z.string().optional(),
      },
      annotations: {
        readOnlyHint: true,
        destructiveHint: false,
        idempotentHint: true,
        openWorldHint: false,
      },
    },
    async ({ limit, sinceMinutes, outcome, session }) => {
      try {
        if (!fs.existsSync(JOURNAL_FILE))
          return textResult(tr("Journal is still empty: ", "Журнал пока пуст: ") + JOURNAL_FILE);
        const cap = limit || 20;
        const raw = fs
          .readFileSync(JOURNAL_FILE, "utf8")
          .split("\n")
          .filter(Boolean);
        const cutoff = sinceMinutes ? Date.now() - sinceMinutes * 60000 : null;
        const out = [];
        for (let i = raw.length - 1; i >= 0 && out.length < cap; i--) {
          let e;
          try {
            e = JSON.parse(raw[i]);
          } catch {
            continue;
          }
          if (cutoff && new Date(e.ts).getTime() < cutoff) break;
          if (outcome && e.outcome !== outcome) continue;
          if (session && e.session !== session) continue;
          out.push(e);
        }
        const fmt = out
          .map((e) =>
            [
              e.ts,
              "seq=" + e.seq,
              "session=" + (e.session || "-"),
              "outcome=" + e.outcome,
              e.commit ? "commit=" + e.commit : null,
              e.auditRange && e.auditRange.count
                ? "audit=" + e.auditRange.fromSeq + ".." + e.auditRange.toSeq + "(" + e.auditRange.count + ")"
                : tr("audit=(none)", "audit=(нет)"),
              "\n  summary: " + e.summary,
              e.next ? "\n  next: " + e.next : null,
            ]
              .filter(Boolean)
              .join("  ")
          )
          .join("\n");
        return textResult(
          [
            "file: " + JOURNAL_FILE,
            "total_entries: " + raw.length,
            "returned: " + out.length + tr(" (newest first)", " (новые сверху)"),
            "--- entries ---",
            fmt || tr("(no entries match the filter)", "(нет записей по фильтру)"),
          ].join("\n")
        );
      } catch (e) {
        return errResult(e);
      }
    }
  );

  return server;
}

/* ---------------------------------- app ---------------------------------- */

const app = express();
app.set("trust proxy", 1);

// Rate limits run FIRST, before any body parsing or route work (audit
// 2026-11-10: the limiter used to be mounted after the OAuth and /local routes).
const RATE_LIMIT_DEFAULTS = { standardHeaders: true, legacyHeaders: false, message: { error: "rate_limited" } };
// Generous global limit: only stops runaway loops, never normal fast agents.
app.use(rateLimit({ ...RATE_LIMIT_DEFAULTS, windowMs: 60 * 1000, limit: 1200 }));
// Failed MCP authentications (HTTP 401) per client address: blunts token
// guessing and audit/CPU flooding. Successful requests are not counted.
app.use(rateLimit({
  ...RATE_LIMIT_DEFAULTS,
  windowMs: 5 * 60 * 1000,
  limit: 60,
  skipSuccessfulRequests: true,
  requestWasSuccessful: (req, res) => res.statusCode !== 401,
  message: { error: "rate_limited", reason: "too_many_failed_auth" },
}));
// Public OAuth surface: discovery, registration, consent, token exchange.
// The consent page polls /oauth/authorize/status every 2 s, far below this.
app.use(["/oauth", "/.well-known"], rateLimit({ ...RATE_LIMIT_DEFAULTS, windowMs: 60 * 1000, limit: 120 }));
// Every registration is stored on disk; every authorize may show a tray card.
const oauthRegisterLimiter = rateLimit({ ...RATE_LIMIT_DEFAULTS, windowMs: 15 * 60 * 1000, limit: 20 });
const oauthAuthorizeLimiter = rateLimit({ ...RATE_LIMIT_DEFAULTS, windowMs: 10 * 60 * 1000, limit: 30 });
// At most this many different consent cards may wait for the owner at once.
const MAX_PENDING_CONSENT_CARDS = 5;

// Bodies are parsed per route, never globally: before authentication only
// small bodies are parsed; the large limit applies to authenticated MCP calls.
const SMALL_BODY_LIMIT = "64kb";
const parseSmallJson = express.json({ limit: SMALL_BODY_LIMIT });
const parseSmallForm = express.urlencoded({ extended: false, limit: SMALL_BODY_LIMIT });
const parseMcpJson = express.json({ limit: "50mb" });

// Health check stays open so the tunnel can probe the service. Callers that
// came through a tunnel only learn that the server is alive: projectDir holds
// the Windows user name, so the details are for direct local callers only.
// Per-boot possession proof. No credentials, machine identifier or paths are public.
// The controller compares the public response with an authenticated localhost response
// BEFORE sending a Bearer token to a newly configured endpoint.
const connectionProofKey = crypto.randomBytes(32);
function connectionProof(nonce) {
  return crypto.createHmac("sha256", connectionProofKey).update(nonce).digest("hex");
}
function validConnectionNonce(value) { return typeof value === "string" && /^[a-f0-9]{64}$/.test(value); }
app.get("/local/connection-proof", (req, res) => {
  if (!checkControlAuth(req, res)) return;
  if (!validConnectionNonce(req.query.nonce)) return res.status(400).json({ error: "invalid nonce" });
  res.set("Cache-Control", "no-store").json({ proof: connectionProof(req.query.nonce) });
});
app.get("/mcp", (req, res, next) => {
  const nonce = req.query["zeroide-route-proof"];
  if (nonce === undefined) return next();
  if (!validConnectionNonce(nonce)) return res.status(400).json({ error: "invalid nonce" });
  res.set("Cache-Control", "no-store").json({ proof: connectionProof(nonce) });
});
app.get("/health", (req, res) => {
  const base = { ok: true, name: "zeroide-mcp", version: SERVER_VERSION };
  // Never infer privacy from proxy headers: arbitrary transports can rewrite Host.
  res.json(base);
});

// The TCP peer is this machine. NOT proof of a local caller on its own:
// every tunnel (ssh -R + cloudflared, Tailscale Serve/Funnel, ngrok, frp,
// a local reverse proxy) also delivers its traffic from 127.0.0.1.
function isLoopback(req) {
  const ra = req.socket.remoteAddress || "";
  return ["127.0.0.1", "::1", "::ffff:127.0.0.1"].includes(ra);
}

// Headers that HTTP reverse proxies and tunnels add on the way in. A remote
// client can add fake ones, but it cannot strip the ones its proxy adds.
const PROXY_HINT_HEADERS = [
  "forwarded", "via", "x-forwarded-for", "x-forwarded-host", "x-forwarded-proto",
  "x-forwarded-port", "x-real-ip", "x-original-host", "true-client-ip",
  "cf-connecting-ip", "cf-ray", "cf-visitor", "cdn-loop",
  "tailscale-funnel-request", "tailscale-user-login",
];

function hasLoopbackHost(req) {
  const host = String(req.headers.host || "").trim().toLowerCase();
  const name = host.startsWith("[") ? host.slice(1, host.indexOf("]")) : host.replace(/:\d+$/, "");
  return name === "127.0.0.1" || name === "localhost" || name === "::1";
}

// A caller on this PC talking to the port directly (the tray, local scripts):
// loopback peer + loopback Host header + no proxy/tunnel headers.
function isDirectLocal(req) {
  if (!isLoopback(req) || !hasLoopbackHost(req)) return false;
  return !PROXY_HINT_HEADERS.some((h) => req.headers[h] !== undefined);
}

function hasValidControlSecret(req) {
  const hdr = req.headers["x-zeroide-control"];
  return !!TRAY_CONTROL_SECRET && typeof hdr === "string" && safeEqual(hdr, TRAY_CONTROL_SECRET);
}

function checkControlAuth(req, res) {
  if (!isDirectLocal(req)) {
    res.status(403).json({ error: "loopback only" });
    return false;
  }
  if (!hasValidControlSecret(req)) {
    res.status(403).json({ error: "invalid control secret" });
    return false;
  }
  return true;
}

// Status for the tray supervisor: liveness, activity and the recent tool
// events (commands, paths), so it is never served through a tunnel. Trays
// built before 27.10 poll it without the control secret, so the secret is
// checked only when sent. TODO v1.0: always require it (checkControlAuth).
app.get("/local/status", (req, res) => {
  if (!isDirectLocal(req)) {
    res.status(403).json({ error: "loopback only" });
    return;
  }
  if (!hasValidControlSecret(req)) {
    res.status(403).json({ error: "invalid control secret" });
    return;
  }
  pruneGrants();
  res.json({
    ok: true,
    name: "zeroide-mcp",
    version: SERVER_VERSION,
    platform: process.platform,
    projectDir: PROJECT_DIR,
    jobs: jobs.size,
    lastActivityAt: lastMcpActivityAt,
    activeCalls: activeToolCalls,
    activitySeq,
    activeGrantsCount: activeGrants.size,
    tokensCount: loadTokens().tokens.length,
    auth: { failClosed: true, legacyToken: !!TOKEN, queryTokenAllowed: ALLOW_QUERY_TOKEN, credentialsConfigured: credentialsConfigured() },
    missedCount: missedApprovals.length,
    oauth: (() => { const s = loadOAuth(); return { clients: s.clients.length, families: s.families.filter((x) => !x.revoked).length, enabled: OAUTH_ENABLED }; })(),
    confinement: (() => { const p = loadPermissions(); return p.confinement && p.confinement.enabled ? { enabled: true, root: p.confinement.root || null } : { enabled: false, root: null }; })(),
    recentEvents: recentEvents.slice(-30),
    activeAgents: Array.from(agentRegistry.values()).map((a) => {
      const trulyWaiting =
        a.standbyUntilMs > Date.now() &&
        (a.standbyActiveCalls > 0 || a.standbyPulseGraceUntilMs > Date.now());
      return {
        agentId: a.agentId,
        agentName: a.agentName,
        tokenLabel: a.tokenLabel,
        title: a.title,
        current: a.current,
        done: a.done,
        next: a.next,
        status: trulyWaiting ? "waiting" : (a.taskState || "running"),
        startedAt: a.startedAt,
        completedAt: a.completedAt || "",
        updatedAt: a.updatedAt,
        lastTool: a.lastTool,
        lastSeenAt: a.lastSeenAt,
        userMessageText: a.userMessageText,
        userMessageKind: a.userMessageKind,
        standbyRemainingSec: trulyWaiting ? Math.max(1, Math.ceil((a.standbyUntilMs - Date.now()) / 1000)) : 0,
      };
    }),
    ownerMessages: ownerMessageQueue.slice(-15),
    chatHistory: chatHistory.slice(-15),
    holdAfterTask,
  });
});

app.post("/local/hold-after-task", parseSmallJson, (req, res) => {
  if (!checkControlAuth(req, res)) return;
  const requested = Boolean(req.body && req.body.enabled);
  holdAfterTask = requested && isAwaitOwnerEnabled();
  audit({ event: "hold_after_task.toggled", enabled: holdAfterTask, ok: true });
  res.json({ ok: true, holdAfterTask });
});

app.post("/local/standby/release", parseSmallJson, (req, res) => {
  if (!checkControlAuth(req, res)) return;
  const agentId = normalizeAgentId((req.body && req.body.agentId) || "");
  for (const slot of agentRegistry.values()) {
    if (!agentId || slot.agentId === agentId) {
      slot.standbyReleased = true;
      slot.standbyUntilMs = 0;
    }
  }
  notifyOwnerListeners();
  res.json({ ok: true });
});

app.post("/local/owner-message", parseSmallJson, (req, res) => {
  if (!checkControlAuth(req, res)) return;
  const body = req.body || {};
  const text = String(body.text || "").trim().slice(0, 2000);
  if (!text) {
    res.status(400).json({ error: "empty_text" });
    return;
  }
  const to = String(body.to || "*").trim() || "*";
  const stopCommand = Boolean(body.stopCommand);
  const msg = enqueueOwnerMessage({ to, text, stopCommand, from: "owner" });
  res.json({ ok: true, message: msg });
});

app.post("/local/owner-message/cancel", parseSmallJson, (req, res) => {
  if (!checkControlAuth(req, res)) return;
  const id = String((req.body && req.body.id) || "");
  let cancelled = 0;
  for (const m of ownerMessageQueue) {
    if (m.status === "queued" && (!id || m.id === id)) {
      m.status = "cancelled";
      cancelled++;
    }
  }
  res.json({ ok: true, cancelled });
});

app.post("/local/agent-task/clear", parseSmallJson, (req, res) => {
  if (!checkControlAuth(req, res)) return;
  const agentId = normalizeAgentId((req.body && req.body.agentId) || "");
  if (agentId && agentRegistry.has(agentId)) {
    const slot = agentRegistry.get(agentId);
    slot.title = "";
    slot.current = "";
    slot.done = [];
    slot.next = [];
    slot.startedAt = "";
    slot.hasEverSetTask = false;
  } else if (!agentId) {
    for (const slot of agentRegistry.values()) {
      slot.title = "";
      slot.current = "";
      slot.done = [];
      slot.next = [];
      slot.startedAt = "";
      slot.hasEverSetTask = false;
    }
  }
  res.json({ ok: true });
});

app.get("/local/grants", (req, res) => {
  if (!checkControlAuth(req, res)) return;
  pruneGrants();
  const list = Array.from(activeGrants.values()).map((g) => ({
    key: g.key,
    tool: g.tool,
    scope: g.scope,
    argsHash: g.argsHash,
    target: g.target,
    createdAt: g.createdAt,
    expiresAt: g.expiresAtIso,
    remainingSec: Math.max(0, Math.round((g.expiresAt - Date.now()) / 1000)),
  }));
  res.json({ ok: true, grants: list });
});

app.get("/local/missed", (req, res) => {
  if (!checkControlAuth(req, res)) return;
  res.json({ ok: true, missed: missedApprovals.slice() });
});

app.post("/local/missed/clear", (req, res) => {
  if (!checkControlAuth(req, res)) return;
  const count = missedApprovals.length;
  missedApprovals.length = 0;
  res.json({ ok: true, cleared: count });
});

/* --------------------------- OAuth endpoints --------------------------- */

function oauthGuard(req, res) {
  if (!OAUTH_ENABLED) { res.status(404).json({ error: "not_found" }); return false; }
  return true;
}

app.get(["/.well-known/oauth-protected-resource", "/.well-known/oauth-protected-resource/mcp"], (req, res) => {
  if (!oauthGuard(req, res)) return;
  const issuer = issuerOf(req);
  res.json({
    resource: issuer + "/mcp",
    authorization_servers: [issuer],
    bearer_methods_supported: ["header"],
    resource_name: "ZeroIDE MCP",
    scopes_supported: Object.keys(OAUTH_SCOPES),
  });
});

app.get("/.well-known/oauth-authorization-server", (req, res) => {
  if (!oauthGuard(req, res)) return;
  const issuer = issuerOf(req);
  res.json({
    issuer,
    authorization_endpoint: issuer + "/oauth/authorize",
    token_endpoint: issuer + "/oauth/token",
    registration_endpoint: issuer + "/oauth/register",
    revocation_endpoint: issuer + "/oauth/revoke",
    response_types_supported: ["code"],
    grant_types_supported: ["authorization_code", "refresh_token"],
    code_challenge_methods_supported: ["S256"],
    token_endpoint_auth_methods_supported: ["none"],
    revocation_endpoint_auth_methods_supported: ["none"],
    scopes_supported: Object.keys(OAUTH_SCOPES),
  });
});

// The owner decides which services may connect (their PC, their call). Any
// https redirect is registrable; plain http is loopback-only because codes
// must not travel over plaintext to a remote host. Unknown (non-listed)
// sites are NOT blocked - they get a risk warning on the consent card and
// on the waiting page instead (see riskFor).
const KNOWN_CLIENT_HOSTS = ["chatgpt.com", "claude.ai", "claude.com"];

function redirectUriAllowed(uri) {
  try {
    const u = new URL(uri);
    if (u.protocol === "https:") return true;
    if (u.protocol === "http:" && ["127.0.0.1", "localhost"].includes(u.hostname)) return true;
    return false;
  } catch { return false; }
}

function riskFor(redirectUri) {
  try {
    const u = new URL(redirectUri);
    if (KNOWN_CLIENT_HOSTS.includes(u.hostname)) return "known";
    if (["127.0.0.1", "localhost"].includes(u.hostname)) return "known";
    return "unknown_site";
  } catch { return "unknown_site"; }
}

app.post("/oauth/register", oauthRegisterLimiter, parseSmallJson, (req, res) => {
  if (!oauthGuard(req, res)) return;
  const body = req.body || {};
  const uris = Array.isArray(body.redirect_uris) ? body.redirect_uris : [];
  if (!uris.length || !uris.every(redirectUriAllowed)) {
    res.status(400).json({ error: "invalid_redirect_uri", error_description: "redirect_uris must be https URLs (any host) or loopback http URLs" });
    return;
  }
  if (body.token_endpoint_auth_method && body.token_endpoint_auth_method !== "none") {
    res.status(400).json({ error: "invalid_client_metadata", error_description: "only public clients (token_endpoint_auth_method=none) are supported" });
    return;
  }
  const client = {
    client_id: "client_" + crypto.randomBytes(8).toString("hex"),
    client_name: String(body.client_name || "Unknown client").slice(0, 120),
    redirect_uris: uris,
    createdAt: new Date().toISOString(),
  };
  const store = loadOAuth();
  store.clients.push(client);
  saveOAuth(store);
  audit({ event: "oauth.client_registered", clientId: client.client_id, clientName: client.client_name, ok: true });
  res.status(201).json({
    client_id: client.client_id,
    client_id_issued_at: Math.floor(Date.now() / 1000),
    client_name: client.client_name,
    redirect_uris: client.redirect_uris,
    grant_types: ["authorization_code", "refresh_token"],
    response_types: ["code"],
    token_endpoint_auth_method: "none",
  });
});

function oauthWaitingPage(consentId, clientName, risk, redirectHost) {
  return tr("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>ZeroIDE MCP — Connection</title></head>", "<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\"><title>ZeroIDE MCP — подключение</title></head>") +
    "<body style=\"font-family:system-ui;background:#0b0d12;color:#e8eaf0;display:flex;align-items:center;justify-content:center;height:100vh;margin:0\">" +
    "<div style=\"max-width:420px;text-align:center;padding:24px\">" +
    tr("<h2>Connection: ", "<h2>Подключение: ") + escapeHtml(clientName) + "</h2>" +
    tr("<p id=\"msg\">Waiting for a decision on the computer… Confirm the connection in the ZeroIDE MCP window.</p>", "<p id=\"msg\">Ожидание решения на компьютере… Подтвердите подключение в окне ZeroIDE MCP.</p>") +
    (risk === "unknown_site" ? tr("<p style=\"color:#ffb454\">Warning: site ", "<p style=\"color:#ffb454\">Внимание: сайт ") + escapeHtml(redirectHost || "") + tr(" is not on the list of known services. It will get access to this computer within the chosen scope. Connect only trusted resources.</p>", " не входит в список известных сервисов. Он получит доступ к этому компьютеру в выбранном объёме. Подключайте только доверенные ресурсы.</p>") : "") +
    "</div><script>" +
    "const id = " + JSON.stringify(consentId) + ";" +
    "async function poll(){ try { const r = await fetch('/oauth/authorize/status?id=' + encodeURIComponent(id), { headers: { 'Accept': 'application/json', 'ngrok-skip-browser-warning': '1' }, cache: 'no-store' }); const j = await r.json();" +
    " if (j.status === 'approved' || j.status === 'denied') { location.href = j.redirect; return; }" +
    tr(" if (j.status === 'error') { document.getElementById('msg').textContent = 'The ZeroIDE MCP app is unavailable. Start it and try again.'; return; }", " if (j.status === 'error') { document.getElementById('msg').textContent = 'Приложение ZeroIDE MCP недоступно. Запустите его и попробуйте снова.'; return; }") +
    "} catch(e) {} setTimeout(poll, 2000); } poll();" +
    "</script></body></html>";
}

app.get("/oauth/authorize", oauthAuthorizeLimiter, (req, res) => {
  if (!oauthGuard(req, res)) return;
  pruneOAuthState();
  const q = req.query || {};
  const store = loadOAuth();
  const client = store.clients.find((c) => c.client_id === q.client_id);
  if (!client) { res.status(400).send("Unknown client_id. Register the client first (RFC 7591)."); return; }
  const redirectUri = String(q.redirect_uri || "");
  if (!client.redirect_uris.includes(redirectUri)) { res.status(400).send("redirect_uri is not registered for this client."); return; }
  const back = (params) => {
    const u = new URL(redirectUri);
    for (const [k, v] of Object.entries(params)) u.searchParams.set(k, v);
    if (q.state) u.searchParams.set("state", String(q.state));
    return u.toString();
  };
  if (q.response_type !== "code") { res.redirect(back({ error: "unsupported_response_type" })); return; }
  if (!q.code_challenge || q.code_challenge_method !== "S256") { res.redirect(back({ error: "invalid_request", error_description: "PKCE S256 is required" })); return; }
  let oauthScope = "mcp.readwrite";
  if (typeof q.scope === "string") {
    const asked = q.scope.split(/\s+/).filter((s) => OAUTH_SCOPES[s]);
    if (asked.includes("mcp.full")) oauthScope = "mcp.full";
    else if (asked.includes("mcp.readwrite")) oauthScope = "mcp.readwrite";
    else if (asked.length) oauthScope = asked[0];
  }
  const scope = OAUTH_SCOPES[oauthScope];
  const consentId = "consent_" + crypto.randomBytes(6).toString("hex");
  let redirectHost = redirectUri;
  try { redirectHost = new URL(redirectUri).host; } catch {}
  pendingConsents.set(consentId, { status: "pending", createdAt: Date.now() });
  const risk = riskFor(redirectUri);
  const dedupeKey = client.client_id + "|" + scope + "|" + redirectUri;
  const dedupeExisting = pendingConsentByKey.get(dedupeKey);
  const dedupePrimary = dedupeExisting ? pendingConsents.get(dedupeExisting.primaryId) : null;
  if (dedupeExisting && dedupePrimary && dedupePrimary.status === "pending") {
    dedupeExisting.attached.push({ consentId, back, challenge: String(q.code_challenge) });
    audit({ event: "oauth.consent_deduped", id: consentId, attachedTo: dedupeExisting.primaryId, clientId: client.client_id, clientName: client.client_name, scope, redirectHost, risk, ok: true });
    res.type("html").send(oauthWaitingPage(consentId, client.client_name, risk, redirectHost));
    return;
  }
  if (pendingConsentByKey.size >= MAX_PENDING_CONSENT_CARDS) {
    pendingConsents.delete(consentId);
    audit({ event: "oauth.consent_throttled", clientId: client.client_id, clientName: client.client_name, pending: pendingConsentByKey.size, ok: false });
    res.status(429).type("text").send("Too many connection requests are already waiting for the owner. Try again in a few minutes.");
    return;
  }
  pendingConsentByKey.set(dedupeKey, { primaryId: consentId, attached: [] });
  audit({ event: "oauth.consent_requested", id: consentId, clientId: client.client_id, clientName: client.client_name, scope, redirectHost, risk, ok: true });
  // Ask the owner via the tray card channel (kind: oauth_consent). The tray
  // answers { decision: "allow", lifetimeDays: 1|30|null } or
  // { decision: "deny", by: "user"|"timeout" }.
  requestTrayApproval({
    id: consentId,
    kind: "oauth_consent",
    clientName: client.client_name,
    clientId: client.client_id,
    redirectHost,
    scope,
    scopeHuman: SCOPE_HUMAN_RU[scope],
    risk,
    warning: risk === "unknown_site" ? tr("Site ", "Сайт ") + redirectHost + tr(" is not on the list of known services (ChatGPT, Claude). It will get access to this PC within the scope: ", " не входит в список известных сервисов (ChatGPT, Claude). Он получит доступ к этому ПК в объёме: ") + SCOPE_HUMAN_RU[scope] + tr(". Connect only trusted resources.", ". Подключайте только доверенные ресурсы.") : null,
    timeoutSec: 120,
    createdAt: new Date().toISOString(),
  }, 120).then((resp) => {
    const rec = pendingConsents.get(consentId);
    if (!rec) return;
    if (resp && !resp.error && (resp.decision === "allow" || resp.decision === "allow_once")) {
      const finalScope = (resp.scope && SCOPE_HUMAN_RU[resp.scope]) ? resp.scope : scope;
      const finalOauthScope = "mcp." + finalScope;
      let lifetimeDays = resp.lifetimeDays === undefined ? 30 : resp.lifetimeDays;
      if (lifetimeDays !== null) lifetimeDays = [1, 30].includes(Number(lifetimeDays)) ? Number(lifetimeDays) : 30;
      const familyExpiresAt = lifetimeDays === null ? null : new Date(Date.now() + lifetimeDays * 86400000).toISOString();
      const codePlain = genSecret("apc_", 24);
      pendingCodes.set(hashSecret(codePlain), {
        clientId: client.client_id, redirectUri, challenge: String(q.code_challenge),
        scope: finalScope, oauthScope: finalOauthScope, familyExpiresAt, createdAt: Date.now(), used: false,
      });
      rec.status = "approved";
      rec.redirect = back({ code: codePlain });
      audit({ event: "oauth.consent_allowed", id: consentId, clientId: client.client_id, scope: finalScope, lifetimeDays, ok: true });
    } else if (resp && !resp.error) {
      rec.status = "denied";
      rec.redirect = back({ error: "access_denied", error_description: "The owner declined this connection" });
      audit({ event: "oauth.consent_denied", id: consentId, clientId: client.client_id, by: resp.by || "user", ok: false });
    } else {
      rec.status = "error";
      audit({ event: "oauth.consent_unavailable", id: consentId, clientId: client.client_id, error: resp && resp.error, ok: false });
    }
    // Fan the same decision out to requests that attached to this consent while
    // its card was pending (dedupe): each gets its own code/state/challenge.
    const dedupe = pendingConsentByKey.get(dedupeKey);
    if (dedupe && dedupe.primaryId === consentId) {
      pendingConsentByKey.delete(dedupeKey);
      for (const w of dedupe.attached) {
        const wrec = pendingConsents.get(w.consentId);
        if (!wrec) continue;
        if (resp && !resp.error && (resp.decision === "allow" || resp.decision === "allow_once")) {
          const finalScope = (resp.scope && SCOPE_HUMAN_RU[resp.scope]) ? resp.scope : scope;
          const finalOauthScope = "mcp." + finalScope;
          let lifetimeDays = resp.lifetimeDays === undefined ? 30 : resp.lifetimeDays;
          if (lifetimeDays !== null) lifetimeDays = [1, 30].includes(Number(lifetimeDays)) ? Number(lifetimeDays) : 30;
          const familyExpiresAt = lifetimeDays === null ? null : new Date(Date.now() + lifetimeDays * 86400000).toISOString();
          const codePlain = genSecret("apc_", 24);
          pendingCodes.set(hashSecret(codePlain), {
            clientId: client.client_id, redirectUri, challenge: w.challenge,
            scope: finalScope, oauthScope: finalOauthScope, familyExpiresAt, createdAt: Date.now(), used: false,
          });
          wrec.status = "approved";
          wrec.redirect = w.back({ code: codePlain });
          audit({ event: "oauth.consent_allowed", id: w.consentId, clientId: client.client_id, scope: finalScope, lifetimeDays, ok: true });
        } else if (resp && !resp.error) {
          wrec.status = "denied";
          wrec.redirect = w.back({ error: "access_denied", error_description: "The owner declined this connection" });
          audit({ event: "oauth.consent_denied", id: w.consentId, clientId: client.client_id, by: resp.by || "user", ok: false });
        } else {
          wrec.status = "error";
          audit({ event: "oauth.consent_unavailable", id: w.consentId, clientId: client.client_id, error: resp && resp.error, ok: false });
        }
      }
    }
  });
  res.type("html").send(oauthWaitingPage(consentId, client.client_name, risk, redirectHost));
});

app.get("/oauth/authorize/status", (req, res) => {
  if (!oauthGuard(req, res)) return;
  const rec = pendingConsents.get(String(req.query.id || ""));
  if (!rec) { res.json({ status: "error" }); return; }
  res.json({ status: rec.status, redirect: rec.redirect || undefined });
});

function oauthTokenError(res, error, description) {
  res.status(400).json({ error, error_description: description || undefined });
}

app.post("/oauth/token", parseSmallForm, parseSmallJson, (req, res) => {
  if (!oauthGuard(req, res)) return;
  pruneOAuthState();
  const body = req.body || {};
  if (body.grant_type === "authorization_code") {
    const codeHash = hashSecret(String(body.code || ""));
    const rec = pendingCodes.get(codeHash);
    if (!rec || rec.used || Date.now() - rec.createdAt > 60000) { oauthTokenError(res, "invalid_grant", "code is invalid, used or expired"); return; }
    rec.used = true;
    if (body.client_id !== rec.clientId) { oauthTokenError(res, "invalid_grant", "client_id mismatch"); return; }
    if (body.redirect_uri !== rec.redirectUri) { oauthTokenError(res, "invalid_grant", "redirect_uri mismatch"); return; }
    const verifier = String(body.code_verifier || "");
    const computed = crypto.createHash("sha256").update(verifier).digest("base64url");
    if (!verifier || computed !== rec.challenge) { oauthTokenError(res, "invalid_grant", "PKCE verification failed"); return; }
    const store = loadOAuth();
    const client = store.clients.find((c) => c.client_id === rec.clientId);
    const refreshPlain = genSecret("apr_", 32);
    const family = {
      id: "fam_" + crypto.randomBytes(5).toString("hex"),
      clientId: rec.clientId,
      scope: rec.scope,
      oauthScope: rec.oauthScope,
      createdAt: new Date().toISOString(),
      expiresAt: rec.familyExpiresAt,
      currentRefreshHash: hashSecret(refreshPlain),
      pastRefreshHashes: [],
      revoked: false,
    };
    store.families.push(family);
    saveOAuth(store);
    const { plain } = issueAccessEntry({ label: client ? client.client_name : rec.clientId, scope: rec.scope, clientId: rec.clientId, familyId: family.id, ttlSec: 3600 });
    audit({ event: "oauth.token_issued", clientId: rec.clientId, familyId: family.id, scope: rec.scope, ok: true });
    res.json({ access_token: plain, token_type: "Bearer", expires_in: 3600, refresh_token: refreshPlain, scope: rec.oauthScope });
    return;
  }
  if (body.grant_type === "refresh_token") {
    const presented = hashSecret(String(body.refresh_token || ""));
    const store = loadOAuth();
    let family = store.families.find((x) => x.currentRefreshHash === presented);
    if (!family) {
      const past = store.families.find((x) => (x.pastRefreshHashes || []).includes(presented));
      if (past && !past.revoked) {
        audit({ event: "oauth.refresh_reuse_detected", familyId: past.id, clientId: past.clientId, ok: false });
        revokeFamily(past, "refresh_reuse");
      }
      oauthTokenError(res, "invalid_grant", "refresh token is invalid");
      return;
    }
    if (family.revoked || (family.expiresAt && Date.now() > Date.parse(family.expiresAt))) {
      oauthTokenError(res, "invalid_grant", "refresh family is revoked or expired");
      return;
    }
    const client = store.clients.find((c) => c.client_id === family.clientId);
    const refreshPlain = genSecret("apr_", 32);
    family.pastRefreshHashes.push(family.currentRefreshHash);
    if (family.pastRefreshHashes.length > 20) family.pastRefreshHashes = family.pastRefreshHashes.slice(-20);
    family.currentRefreshHash = hashSecret(refreshPlain);
    family.lastRotatedAt = new Date().toISOString();
    saveOAuth(store);
    const { plain } = issueAccessEntry({ label: client ? client.client_name : family.clientId, scope: family.scope, clientId: family.clientId, familyId: family.id, ttlSec: 3600 });
    audit({ event: "oauth.refresh_rotated", familyId: family.id, clientId: family.clientId, ok: true });
    res.json({ access_token: plain, token_type: "Bearer", expires_in: 3600, refresh_token: refreshPlain, scope: family.oauthScope });
    return;
  }
  oauthTokenError(res, "unsupported_grant_type", "grant_type must be authorization_code or refresh_token");
});

app.post("/oauth/revoke", parseSmallForm, parseSmallJson, (req, res) => {
  if (!oauthGuard(req, res)) return;
  const presented = hashSecret(String((req.body && req.body.token) || ""));
  const reg = loadTokens();
  const entry = reg.tokens.find((t) => t.hash === presented);
  if (entry && !entry.revoked) {
    entry.revoked = true;
    saveTokens(reg);
    audit({ event: "oauth.revoked", tokenId: entry.id, ok: true });
  } else {
    const store = loadOAuth();
    const family = store.families.find((x) => x.currentRefreshHash === presented || (x.pastRefreshHashes || []).includes(presented));
    if (family && !family.revoked) revokeFamily(family, "revoke_endpoint");
  }
  res.status(200).end();
});

app.post("/local/grants/revoke", parseSmallJson, (req, res) => {
  if (!checkControlAuth(req, res)) return;
  pruneGrants();
  const { key, all } = req.body || {};
  if (all) {
    const count = activeGrants.size;
    activeGrants.clear();
    audit({ event: "grant.revoke_all", count, by: "user", ok: true });
    res.json({ ok: true, revoked: count });
    return;
  }
  if (key && activeGrants.has(key)) {
    activeGrants.delete(key);
    audit({ event: "grant.revoke", key, by: "user", ok: true });
    res.json({ ok: true, revoked: 1 });
    return;
  }
  res.json({ ok: true, revoked: 0 });
});

// Rate limits are mounted right after the app is created (before parsing and routes).

function safeEqual(a, b) {
  const ab = Buffer.from(String(a));
  const bb = Buffer.from(String(b));
  if (ab.length !== bb.length) return false;
  return crypto.timingSafeEqual(ab, bb);
}

app.use((req, res, next) => {
  if (req.path.startsWith("/oauth/") || req.path.startsWith("/.well-known/")) return next();
  const authRes = authenticateRequest(req);
  if (authRes.error) {
    res.set("WWW-Authenticate", 'Bearer resource_metadata="' + issuerOf(req) + '/.well-known/oauth-protected-resource"');
    res.status(401).json({ error: "Unauthorized", code: authRes.error, message: AUTH_ERROR_HINTS[authRes.error] });
    return;
  }
  req.caller = authRes.caller;
  return next();
});

/* ------------------------------ SSE transport ---------------------------- */

const transports = {};

app.get("/sse", async (req, res) => {
  lastMcpActivityAt = new Date().toISOString();
  activitySeq += 1;
  const transport = new SSEServerTransport("/messages", res);
  const server = makeServer({ sessionId: transport.sessionId, caller: req.caller || LEGACY_CALLER });
  server.server.oninitialized = () => recordConnectedClient(req, server, "/sse");

  transports[transport.sessionId] = transport;

  // Keep-alive: Cloudflare drops idle streams (~100s). Comment frames keep it warm.
  const keepAlive = setInterval(() => {
    try {
      res.write(": ping " + Date.now() + "\n\n");
    } catch {}
  }, KEEPALIVE_MS);
  if (keepAlive.unref) keepAlive.unref();

  res.on("close", () => {
    clearInterval(keepAlive);
    delete transports[transport.sessionId];
  });

  await server.connect(transport);
});

app.post("/messages", parseMcpJson, async (req, res) => {
  lastMcpActivityAt = new Date().toISOString();
  activitySeq += 1;
  const sessionId = req.query.sessionId;
  const transport = transports[sessionId];
  if (!transport) {
    res.status(400).send("No transport found for sessionId");
    return;
  }
  await transport.handlePostMessage(req, res, req.body);
});

/* ------------------------ Streamable HTTP transport ---------------------- */

const streamableTransports = {};

// Onboarding evidence is separate from token-use timestamps and local verification.
// Record only after the client's initialized notification, never merely HTTP reachability.
function recordConnectedClient(req, server, endpointPath) {
  try {
    const client = server.server.getClientVersion();
    if (!client || client.name === "ZeroIDE connection verifier") return;
    const host = String(req.headers["x-forwarded-host"] || req.headers.host || "").trim();
    if (!/^[a-zA-Z0-9.\-:\[\]]{1,255}$/.test(host)) return;
    const file = path.join(path.dirname(TOKENS_FILE), "connection-client.json");
    const tmp = file + ".tmp-" + process.pid;
    fs.writeFileSync(tmp, JSON.stringify({ at: new Date().toISOString(), host, path: endpointPath }), { encoding: "utf8", mode: 0o600 });
    fs.renameSync(tmp, file);
  } catch { /* Missing evidence means 'not confirmed', never block an MCP client. */ }
}

app.post("/mcp", parseMcpJson, async (req, res) => {
  lastMcpActivityAt = new Date().toISOString();
  activitySeq += 1;
  const sessionId = req.headers["mcp-session-id"];
  let transport;
  if (sessionId && streamableTransports[sessionId]) {
    transport = streamableTransports[sessionId];
  } else if (sessionId) {
    // MCP Streamable HTTP requires 404 for an expired/unknown session so the
    // client can discard its stale session ID and initialize a fresh session.
    res.status(404).json({
      jsonrpc: "2.0",
      error: { code: -32001, message: "Session not found" },
      id: req.body?.id ?? null,
    });
    return;
  } else {
    const newSessionId = crypto.randomUUID();
    const server = makeServer({ sessionId: newSessionId, caller: req.caller || LEGACY_CALLER });
    server.server.oninitialized = () => recordConnectedClient(req, server, "/mcp");
    transport = new StreamableHTTPServerTransport({
      sessionIdGenerator: () => newSessionId,
      onsessioninitialized: (sid) => {
        streamableTransports[sid] = transport;
      },
    });
    transport.onclose = () => {
      if (transport.sessionId) delete streamableTransports[transport.sessionId];
    };
    await server.connect(transport);
  }
  await transport.handleRequest(req, res, req.body);
});

app.get("/mcp", async (req, res) => {
  const sessionId = req.headers["mcp-session-id"];
  if (!sessionId || !streamableTransports[sessionId]) {
    res.status(404).json({
      jsonrpc: "2.0",
      error: { code: -32001, message: "Session not found" },
      id: null,
    });
    return;
  }
  const transport = streamableTransports[sessionId];
  const keepAlive = setInterval(() => {
    try {
      res.write(": ping " + Date.now() + "\n\n");
    } catch {}
  }, KEEPALIVE_MS);
  if (keepAlive.unref) keepAlive.unref();
  res.on("close", () => clearInterval(keepAlive));
  await transport.handleRequest(req, res);
});

app.delete("/mcp", async (req, res) => {
  const sessionId = req.headers["mcp-session-id"];
  if (sessionId && streamableTransports[sessionId]) {
    await streamableTransports[sessionId].close();
    delete streamableTransports[sessionId];
  }
  res.status(200).end();
});

// Errors from body parsers and other middleware: a short JSON answer, never
// Express's default HTML page (it prints stack traces with local file paths,
// which contain the Windows user name).
app.use((err, req, res, next) => {
  if (res.headersSent) return next(err);
  const status = Number(err && (err.status || err.statusCode)) || 500;
  const code = err && err.type === "entity.too.large" ? "payload_too_large"
    : err && err.type === "entity.parse.failed" ? "invalid_json"
      : status >= 400 && status < 500 ? "bad_request" : "server_error";
  if (status >= 500) console.error("request error: " + (err && err.message));
  res.status(status >= 400 && status < 600 ? status : 500).json({ error: code });
});

/* ---------------------- tray heartbeat: no unsupervised server ----------------
   The tray is the supervisor: if it dies, this server must not keep serving.
   Every TRAY_HEARTBEAT_SEC seconds we send a minimal GET to the tray control
   port (any HTTP status proves the tray is alive; the tray answers 404/403
   without logging). After TRAY_HEARTBEAT_MISSES consecutive failures
   (connection refused / timeout) we audit and exit; the tray restarts a
   crashed server, so a false trip self-heals, while a truly dead tray leaves
   no orphan server behind. Disabled when TRAY_CONTROL_SECRET is empty
   (unsupervised/headless mode: nothing to check against). The tray-side
   kill-on-close job object is the primary guarantee; this is the fallback. */
const TRAY_HEARTBEAT_SEC = Math.max(1, parseInt(process.env.TRAY_HEARTBEAT_SEC || "5", 10));
const TRAY_HEARTBEAT_MISSES = Math.max(1, parseInt(process.env.TRAY_HEARTBEAT_MISSES || "3", 10));
function trayHeartbeatOnce() {
  return new Promise((resolve) => {
    const headers = { Connection: "close" };
    if (TRAY_CONTROL_SECRET) headers["X-ZeroIDE-Control"] = TRAY_CONTROL_SECRET;
    const req = http.get(
      { hostname: "127.0.0.1", port: TRAY_CONTROL_PORT, path: "/health", headers, agent: false },
      (res) => {
        res.resume();
        res.on("end", () => resolve(res.statusCode >= 200 && res.statusCode < 600));
      }
    );
    req.setTimeout(2000, () => { try { req.destroy(new Error("tray_heartbeat_timeout")); } catch {} resolve(false); });
    req.on("error", () => resolve(false));
  });
}
function startTrayHeartbeat() {
  if (!TRAY_CONTROL_SECRET) {
    console.log("Tray heartbeat: OFF (no TRAY_CONTROL_SECRET — unsupervised mode)");
    return;
  }
  console.log(`Tray heartbeat: ON (every ${TRAY_HEARTBEAT_SEC}s, exit after ${TRAY_HEARTBEAT_MISSES} misses)`);
  let misses = 0;
  setInterval(async () => {
    let ok = false;
    try { ok = await trayHeartbeatOnce(); } catch { ok = false; }
    if (ok) { misses = 0; return; }
    misses += 1;
    if (misses >= TRAY_HEARTBEAT_MISSES) {
      console.error(`Tray heartbeat lost (${misses} consecutive failures) — exiting so no unsupervised server is left behind`);
      try { audit({ tool: "__tray_heartbeat_lost__", misses, ok: false }); } catch {}
      process.exit(1);
    }
  }, TRAY_HEARTBEAT_SEC * 1000);
}

app.listen(PORT, HOST, () => {
  console.log("MCP server v2 (zeroide-mcp) listening on " + HOST + ":" + PORT);
  console.log("Project dir: " + PROJECT_DIR);
  console.log("Audit log: " + AUDIT_FILE);
  audit({ tool: "__startup__", version: SERVER_VERSION, port: PORT, ok: true });
  if (!credentialsConfigured()) {
    console.error("AUTH: no credentials configured (no MCP_TOKEN, no active tokens, OAuth disabled): every MCP request is rejected with 401 NO_CREDENTIALS_CONFIGURED");
    audit({ event: "auth.no_credentials", ok: false });
  }
  if (ALLOW_QUERY_TOKEN) console.warn("AUTH: MCP_ALLOW_QUERY_TOKEN=1, legacy ?token= is accepted (deprecated: URLs leak into tunnel and proxy logs)");
  startTrayHeartbeat();
});
