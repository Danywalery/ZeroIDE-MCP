import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";
const BASE = process.env.TEST_BASE;
const ROOT = process.env.TEST_ROOT;
if (!BASE || !ROOT) { console.error("Run through tests/isolated-v04.mjs (needs TEST_BASE and TEST_ROOT)."); process.exit(2); }
let failures = 0;
const check = (n, c, x = "") => { if (c) console.log("PASS: " + n); else { failures++; console.log("FAIL: " + n + " " + x); } };
async function register(name, uri) {
  const res = await fetch(ROOT + "/oauth/register", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ client_name: name, redirect_uris: [uri], token_endpoint_auth_method: "none" }) });
  let body = {}; try { body = await res.json(); } catch {}
  return { status: res.status, body };
}
let r1 = await register("RandomSvc", "https://random-service.example/cb");
check("unknown https site CAN register (owner decides)", r1.status === 201 && !!r1.body.client_id, "HTTP " + r1.status + " " + JSON.stringify(r1.body).slice(0, 90));
let r2 = await register("PlainHttp", "http://random-service.example/cb");
check("remote plain http still rejected (codes over plaintext)", r2.status === 400, "HTTP " + r2.status);
let r3 = await register("ChatGPT", "https://chatgpt.com/connector/cb");
check("known site registers", r3.status === 201, "HTTP " + r3.status);
async function authorizePage(clientId, redirect) {
  const challenge = crypto.createHash("sha256").update(crypto.randomBytes(32).toString("base64url")).digest("base64url");
  const u = new URL(ROOT + "/oauth/authorize");
  u.searchParams.set("client_id", clientId); u.searchParams.set("redirect_uri", redirect);
  u.searchParams.set("response_type", "code"); u.searchParams.set("code_challenge", challenge);
  u.searchParams.set("code_challenge_method", "S256"); u.searchParams.set("scope", "mcp.full");
  return (await fetch(u)).text();
}
const pageUnknown = await authorizePage(r1.body.client_id, "https://random-service.example/cb");
check("waiting page warns about unknown site", pageUnknown.includes("не входит в список известных"), pageUnknown.slice(0, 140));
const pageKnown = await authorizePage(r3.body.client_id, "https://chatgpt.com/connector/cb");
check("waiting page has NO warning for known site", !pageKnown.includes("не входит в список известных"));
await new Promise((res) => setTimeout(res, 800));
const auditText = fs.readFileSync(path.join(BASE, "logs", "audit.jsonl"), "utf8");
check("audit consent_requested carries risk=unknown_site", auditText.includes('"risk":"unknown_site"'));
check("audit consent_requested carries known risk", auditText.includes('"risk":"known"'));
console.log(failures === 0 ? "DCR-POLICY: ALL PASS" : "DCR-POLICY: " + failures + " FAILURES");
process.exit(failures ? 1 : 0);
