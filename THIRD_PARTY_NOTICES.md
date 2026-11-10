# Third-party notices

ZeroIDE MCP itself is licensed under MIT No Attribution (see `LICENSE`). The release package contains or downloads the third-party components listed below. Each keeps its own license. License texts ship with the components; for the server dependencies they are in `server/node_modules/<package>/`.

## Tray app (`tray/build/ZeroIDE-MCP.exe`)

| Component | License |
|---|---|
| .NET 8 runtime and Windows Forms, embedded in the self-contained exe (© .NET Foundation and Contributors) | MIT |
| System.Management 8.0.0 from NuGet (© .NET Foundation and Contributors) | MIT |
| Inter font (© The Inter Project Authors), see `tray/fonts/LICENSE.Inter.txt` | SIL Open Font License 1.1 |

## Downloaded by the installer

| Component | License |
|---|---|
| Node.js, portable Windows x64 build; version and SHA-256 are pinned in `installer/install.ps1` | MIT, with bundled components under their own licenses: https://github.com/nodejs/node/blob/main/LICENSE |

## Downloaded only if the owner chooses that connection (not bundled)

| Component | License |
|---|---|
| ngrok agent | ngrok terms of service |
| cloudflared | Apache-2.0 |

## Server dependencies (`server/node_modules`, shipped in the release package)

Generated from `server/node_modules` as installed by `npm ci --omit=dev` from `server/package-lock.json`.

| Package | Version | License |
|---|---|---|
| @hono/node-server | 2.1.5 | MIT |
| @modelcontextprotocol/sdk | 1.32.2 | MIT |
| accepts | 1.3.8 | MIT |
| accepts | 2.0.0 | MIT |
| ajv | 8.20.0 | MIT |
| ajv-formats | 3.0.1 | MIT |
| array-flatten | 1.1.1 | MIT |
| body-parser | 1.20.9 | MIT |
| body-parser | 2.4.0 | MIT |
| bytes | 3.1.2 | MIT |
| call-bind-apply-helpers | 1.0.2 | MIT |
| call-bound | 1.0.4 | MIT |
| content-disposition | 0.5.4 | MIT |
| content-disposition | 2.0.1 | MIT |
| content-type | 1.0.5 | MIT |
| content-type | 2.1.0 | MIT |
| cookie | 0.7.2 | MIT |
| cookie-signature | 1.0.7 | MIT |
| cookie-signature | 1.2.2 | MIT |
| cors | 2.8.6 | MIT |
| cross-spawn | 7.0.6 | MIT |
| debug | 2.6.9 | MIT |
| debug | 4.4.3 | MIT |
| depd | 2.0.0 | MIT |
| destroy | 1.2.0 | MIT |
| dunder-proto | 1.0.1 | MIT |
| ee-first | 1.1.1 | MIT |
| encodeurl | 2.0.0 | MIT |
| escape-html | 1.0.3 | MIT |
| es-define-property | 1.0.1 | MIT |
| es-errors | 1.3.0 | MIT |
| es-object-atoms | 1.1.2 | MIT |
| etag | 1.8.1 | MIT |
| eventsource | 3.0.7 | MIT |
| eventsource-parser | 3.1.1 | MIT |
| express | 4.22.3 | MIT |
| express | 5.3.0 | MIT |
| express-rate-limit | 7.5.1 | MIT |
| express-rate-limit | 8.7.1 | MIT |
| fast-deep-equal | 3.1.3 | MIT |
| fast-uri | 3.1.9 | BSD-3-Clause |
| finalhandler | 1.3.2 | MIT |
| finalhandler | 2.1.1 | MIT |
| forwarded | 0.2.0 | MIT |
| fresh | 0.5.2 | MIT |
| fresh | 2.0.0 | MIT |
| function-bind | 1.1.2 | MIT |
| get-intrinsic | 1.3.0 | MIT |
| get-proto | 1.0.1 | MIT |
| gopd | 1.2.0 | MIT |
| hasown | 2.0.4 | MIT |
| has-symbols | 1.1.0 | MIT |
| hono | 4.13.13 | MIT |
| http-errors | 2.0.1 | MIT |
| iconv-lite | 0.4.24 | MIT |
| iconv-lite | 0.7.3 | MIT |
| inherits | 2.0.4 | ISC |
| ipaddr.js | 1.9.1 | MIT |
| ip-address | 10.7.4 | MIT |
| isexe | 2.0.0 | ISC |
| is-promise | 4.0.0 | MIT |
| jose | 6.2.13 | MIT |
| json-schema-traverse | 1.0.0 | MIT |
| json-schema-typed | 8.0.2 | BSD-2-Clause |
| math-intrinsics | 1.1.0 | MIT |
| media-typer | 0.3.0 | MIT |
| media-typer | 1.1.1 | MIT |
| merge-descriptors | 1.0.3 | MIT |
| merge-descriptors | 2.0.0 | MIT |
| methods | 1.1.2 | MIT |
| mime | 1.6.0 | MIT |
| mime-db | 1.52.0 | MIT |
| mime-db | 1.54.0 | MIT |
| mime-types | 2.1.35 | MIT |
| mime-types | 3.0.2 | MIT |
| ms | 2.0.0 | MIT |
| ms | 2.1.3 | MIT |
| negotiator | 0.6.3 | MIT |
| negotiator | 1.1.0 | MIT |
| object-assign | 4.1.1 | MIT |
| object-inspect | 1.13.4 | MIT |
| once | 1.4.0 | ISC |
| on-finished | 2.4.2 | MIT |
| parseurl | 1.3.3 | MIT |
| path-key | 3.1.1 | MIT |
| path-to-regexp | 0.1.13 | MIT |
| path-to-regexp | 8.4.2 | MIT |
| pkce-challenge | 5.0.1 | MIT |
| proxy-addr | 2.0.8 | MIT |
| qs | 6.16.1 | BSD-3-Clause |
| range-parser | 1.2.1 | MIT |
| raw-body | 2.5.3 | MIT |
| raw-body | 3.0.2 | MIT |
| require-from-string | 2.0.2 | MIT |
| router | 2.2.0 | MIT |
| safe-buffer | 5.2.1 | MIT |
| safer-buffer | 2.1.2 | MIT |
| send | 0.19.2 | MIT |
| send | 1.2.1 | MIT |
| serve-static | 1.16.3 | MIT |
| serve-static | 2.2.1 | MIT |
| setprototypeof | 1.2.0 | ISC |
| shebang-command | 2.0.0 | MIT |
| shebang-regex | 3.0.0 | MIT |
| side-channel | 1.1.1 | MIT |
| side-channel-list | 1.0.1 | MIT |
| side-channel-map | 1.0.1 | MIT |
| side-channel-weakmap | 1.0.2 | MIT |
| statuses | 2.0.2 | MIT |
| toidentifier | 1.0.1 | MIT |
| type-is | 1.6.18 | MIT |
| type-is | 2.1.0 | MIT |
| unpipe | 1.0.0 | MIT |
| utils-merge | 1.0.1 | MIT |
| vary | 1.1.2 | MIT |
| which | 2.0.2 | ISC |
| wrappy | 1.0.2 | ISC |
| zod | 3.25.76 | MIT |
| zod-to-json-schema | 3.25.2 | ISC |
