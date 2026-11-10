#!/usr/bin/env node
/**
 * zeroide-mcp token registry CLI (v0.4).
 * Manages tokens.json directly; the running server hot-reloads it (mtime).
 *
 * Usage:
 *   node tokens-cli.mjs list [--file <tokens.json>]
 *   node tokens-cli.mjs add --label <name> [--scope read|readwrite|full]
 *        [--root <abs path>] [--ttl-days <n>] [--max-requests <n>] [--file <...>]
 *   node tokens-cli.mjs revoke --id <token-id> [--file <...>]
 *   node tokens-cli.mjs rotate --id <token-id> [--grace-min <n>] [--file <...>]
 *
 * The plaintext token is printed exactly once by add/rotate and never stored.
 */
import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";

function defaultFile() {
  if (process.env.MCP_TOKENS_FILE) return process.env.MCP_TOKENS_FILE;
  if (process.platform === "win32" && process.env.LOCALAPPDATA)
    return path.join(process.env.LOCALAPPDATA, "ZeroIDE-MCP", "tokens.json");
  return path.join(process.cwd(), "tokens.json");
}

function parseArgs(argv) {
  const out = { _: [] };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith("--")) { out[a.slice(2)] = argv[i + 1]; i++; }
    else out._.push(a);
  }
  return out;
}

const args = parseArgs(process.argv.slice(2));
const cmd = args._[0];
const file = args.file || defaultFile();

function load() {
  try {
    const parsed = JSON.parse(fs.readFileSync(file, "utf8"));
    return { version: 1, tokens: Array.isArray(parsed.tokens) ? parsed.tokens : [] };
  } catch { return { version: 1, tokens: [] }; }
}

function save(reg) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const tmp = file + ".tmp-" + process.pid;
  fs.writeFileSync(tmp, JSON.stringify(reg, null, 2), "utf8");
  fs.renameSync(tmp, file);
}

function sha256(s) { return crypto.createHash("sha256").update(s, "utf8").digest("hex"); }
function genToken() { return "apw_" + crypto.randomBytes(24).toString("base64url"); }
function genId() { return "tok_" + crypto.randomBytes(4).toString("hex"); }

const SCOPES = ["read", "readwrite", "full"];

function newEntry(label, opts, rotatedFrom) {
  const now = new Date();
  const ttlDays = opts.ttlDays ? Number(opts.ttlDays) : null;
  return {
    id: genId(),
    label,
    hash: null, // filled by caller
    scope: SCOPES.includes(opts.scope) ? opts.scope : "full",
    root: opts.root ? path.resolve(opts.root) : null,
    source: "manual",
    createdAt: now.toISOString(),
    expiresAt: ttlDays ? new Date(now.getTime() + ttlDays * 86400000).toISOString() : null,
    ttlDays,
    maxRequests: opts.maxRequests ? Number(opts.maxRequests) : null,
    usedRequests: 0,
    lastUsedAt: null,
    revoked: false,
    rotatedFrom: rotatedFrom || null,
  };
}

if (cmd === "list") {
  const reg = load();
  if (!reg.tokens.length) { console.log("(registry is empty: " + file + ")"); process.exit(0); }
  for (const t of reg.tokens) {
    console.log([
      t.id, "label=" + t.label, "scope=" + t.scope,
      "root=" + (t.root || "-"),
      "expires=" + (t.expiresAt || "never"),
      "used=" + (t.usedRequests || 0) + "/" + (t.maxRequests || "inf"),
      t.revoked ? "REVOKED" : "active",
    ].join("  "));
  }
} else if (cmd === "add") {
  if (!args.label) { console.error("add requires --label"); process.exit(1); }
  const plain = genToken();
  const entry = newEntry(args.label, { scope: args.scope, root: args.root, ttlDays: args["ttl-days"], maxRequests: args["max-requests"] }, null);
  entry.hash = sha256(plain);
  const reg = load();
  reg.tokens.push(entry);
  save(reg);
  console.log("id:    " + entry.id);
  console.log("label: " + entry.label + "  scope: " + entry.scope + "  root: " + (entry.root || "-"));
  console.log("TOKEN (shown once, store it now): " + plain);
} else if (cmd === "revoke") {
  const reg = load();
  const t = reg.tokens.find((x) => x.id === args.id);
  if (!t) { console.error("no token with id " + args.id); process.exit(1); }
  t.revoked = true;
  save(reg);
  console.log("revoked: " + t.id + " (" + t.label + ")");
} else if (cmd === "rotate") {
  const reg = load();
  const old = reg.tokens.find((x) => x.id === args.id);
  if (!old) { console.error("no token with id " + args.id); process.exit(1); }
  const graceMin = args["grace-min"] ? Number(args["grace-min"]) : 10;
  const plain = genToken();
  const entry = newEntry(old.label, { scope: old.scope, root: old.root, ttlDays: old.ttlDays, maxRequests: old.maxRequests }, old.id);
  entry.hash = sha256(plain);
  const graceEnd = new Date(Date.now() + graceMin * 60000).toISOString();
  old.expiresAt = old.expiresAt && old.expiresAt < graceEnd ? old.expiresAt : graceEnd;
  reg.tokens.push(entry);
  save(reg);
  console.log("old token " + old.id + " stays valid until " + old.expiresAt);
  console.log("new id: " + entry.id);
  console.log("TOKEN (shown once, store it now): " + plain);
} else {
  console.error("commands: list | add --label L | revoke --id ID | rotate --id ID");
  process.exit(1);
}
