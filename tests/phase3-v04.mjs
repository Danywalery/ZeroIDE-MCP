import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const BASE = process.env.TEST_BASE;
const ROOT = process.env.TEST_ROOT;
if (!BASE || !ROOT) { console.error("Run through tests/isolated-v04.mjs (needs TEST_BASE and TEST_ROOT)."); process.exit(2); }
const URL_ = ROOT + "/mcp";
const TF = path.join(BASE, "cfg", "tokens.json");
const CLI = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "server", "tokens-cli.mjs");
const TOKENS = JSON.parse(fs.readFileSync(path.join(BASE, "tokens-out.json"), "utf8").replace(/^\uFEFF/, ""));
let failures = 0;
const check = (n, c, x = "") => { if (c) console.log("PASS: " + n); else { failures++; console.log("FAIL: " + n + " " + x); } };

async function initStatus(token) {
  const res = await fetch(URL_, { method: "POST", headers: { "Content-Type": "application/json", Accept: "application/json, text/event-stream", Authorization: "Bearer " + token }, body: JSON.stringify({ jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "2025-03-26", capabilities: {}, clientInfo: { name: "p3", version: "1" } } }) });
  const text = await res.text();
  let code = null; try { code = JSON.parse(text).code; } catch {}
  return { status: res.status, code, sid: res.headers.get("mcp-session-id") };
}

// rotate plainfull
const out = execFileSync(process.execPath, [CLI, "rotate", "--id", TOKENS.plainfull.id, "--grace-min", "10", "--file", TF], { encoding: "utf8" });
const newToken = (out.match(/TOKEN \(shown once[^\n]*: (apw_\S+)/) || [])[1];
check("rotate produced a new token", !!newToken, out.slice(0, 120));
let s = await initStatus(TOKENS.plainfull.token);
check("old token still valid in grace period", s.status === 200, "HTTP " + s.status + " " + s.code);
s = await initStatus(newToken);
check("rotated token valid", s.status === 200, "HTTP " + s.status + " " + s.code);

// revoke readjail
execFileSync(process.execPath, [CLI, "revoke", "--id", TOKENS.readjail.id, "--file", TF], { encoding: "utf8" });
s = await initStatus(TOKENS.readjail.token);
check("revoked token -> 401 TOKEN_REVOKED", s.status === 401 && s.code === "TOKEN_REVOKED", "HTTP " + s.status + " " + s.code);

// audit carries token labels
const auditText = fs.readFileSync(path.join(BASE, "logs", "audit.jsonl"), "utf8");
check("audit has token.scope_deny", auditText.includes("token.scope_deny"));
check("audit has confinement.path_deny", auditText.includes("confinement.path_deny"));
check("audit labels caller (readjail)", auditText.includes('"token":"readjail"') || auditText.includes("readjail"));

console.log(failures === 0 ? "PHASE3: ALL PASS" : "PHASE3: " + failures + " FAILURES");
process.exit(failures ? 1 : 0);
