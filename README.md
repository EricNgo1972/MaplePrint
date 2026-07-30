# MaplePrint

A local **print server** for Windows. It accepts print jobs over HTTP, spools
them to the printers attached to that PC, and reports what happened.

- One Windows-only `.exe`, no install required
- Lives in the notification area; no main window
- Knows nothing about who is calling it

## What it is for

Web applications can't reach a USB printer. A POS running on a Linux server, a
kiosk in a browser, a scheduled job — none of them can open `USB001` on the
counter PC. MaplePrint is the piece that can: it runs on the machine the
printers are plugged into and exposes them as an HTTP API.

**It has no idea who its callers are.** There is no server URL to configure, no
account, and no outbound connection. Whoever wants to print is configured with
*MaplePrint's* address and key — the dependency points one way only. That keeps
it equally useful to MapleCoffee, to a Laravel POS, and to a one-line `curl` in
a support call, without a line of code changing.

**It never formats anything.** Callers send device bytes — ESC/POS for a
receipt, ZPL for a label — and MaplePrint puts them on the spooler untouched.
It is a paper path, not a renderer. One code path therefore serves every
printer, because at this layer a receipt and a label differ only in their bytes.

```
    any caller                          Counter PC (Windows)
┌──────────────────┐               ┌──────────────────────────┐
│ MapleCoffee      │               │ MaplePrint (tray)        │
│ Laravel POS      │  HTTP + key   │   :9110                  │
│ curl             │──────────────►│   POST /print            │
│ anything         │◄──────────────│     ↓ RAW via winspool   │
└──────────────────┘   result      │   USB001      USB002     │
                                   │   Star        Zebra      │
                                   └──────────────────────────┘
```

### Reachability

A passive listener can only be reached by a caller that can route to it. On a
counter PC behind a shop router that means one of:

- the caller is on the same LAN — works directly, tick **Accept jobs from other
  machines**
- expose the port with a tunnel (Cloudflare Tunnel, ngrok) — needed when the
  caller is a remote server
- VPN or port forward

This is a deployment choice, not something MaplePrint decides.

## API

Base URL `http://<pc>:9110`. Authenticate with the key from the settings
dialog, as either header:

```
X-MaplePrint-Key: <key>
Authorization: Bearer <key>
```

### `GET /health` — unauthenticated

```json
{ "status": "ok", "station": "COUNTER-1", "version": "1.0.0" }
```

### `GET /printers`

```json
{
  "station": "COUNTER-1",
  "printers": ["Star TSP100 Cutter (TSP143)", "DRINK LABEL", "Microsoft Print to PDF"],
  "receiptQueue": "Star TSP100 Cutter (TSP143)",
  "labelQueue": "DRINK LABEL"
}
```

### `POST /print`

```json
{
  "target": "receipt",
  "payloadBase64": "G0AbYQE...",
  "description": "Order #1042",
  "jobId": "optional-caller-id"
}
```

`target` is a **role** — `receipt` or `label` — not a Windows queue name, so a
shop can replace a printer without the calling app being touched. Anything that
isn't a known role is treated as a literal queue name.

```json
{ "jobId": "a947f7…", "success": true, "queue": "Star TSP100 Cutter (TSP143)" }
```

### `POST /print/{target}`

The same thing with the raw bytes as the request body — no JSON, no base64.
Exists so the whole path can be proved with one command:

```bash
curl -X POST http://192.168.1.20:9110/print/receipt \
     -H "X-MaplePrint-Key: <key>" \
     --data-binary @receipt.escpos
```

### Status codes

| Code | Meaning |
|---|---|
| `200` | spooled |
| `400` | malformed request (bad base64, empty body) |
| `401` | missing or wrong key |
| `404` | target maps to nothing, or that queue isn't installed |
| `502` | the printer refused it or timed out — *retry-able* |

The 4xx/5xx split is deliberate: `4xx` means fix your request, `502` means the
request was fine and the device let us down.

## Setup

1. Download `MaplePrint-{version}.exe` and double-click it. No installer.
   Settings live in `%APPDATA%\MaplePrint\` and survive upgrades.
2. Right-click the maple leaf → **Settings…**

   | Field | Notes |
   |---|---|
   | Listen on port | default `9110` |
   | API key | generated on first run — **Copy key** |
   | Station name | defaults to the PC name |
   | Receipt printer | the ESC/POS queue, e.g. `Star TSP100 Cutter (TSP143)` |
   | Label printer | the ZPL queue, e.g. `DRINK LABEL` |
   | Accept jobs from other machines | off = loopback only |

3. Paste the address and key into whatever is going to print.
4. Tick **Start MaplePrint when I sign in**.

If **Accept jobs from other machines** is on, Windows Firewall may also need to
allow the port before another machine can reach it.

## Troubleshooting

**Test receipt print** / **Test label print** in the tray menu print locally
without the API involved at all. That is the first thing to try when a shop
reports "nothing prints", because it splits the problem cleanly:

- test works, real jobs don't → caller, network, or key
- test fails too → paper, power, cable, or driver

**Activity…** shows every job with the reason for any failure, and writes
`%APPDATA%\MaplePrint\activity.log` (rolled at 1 MB).

## Printer notes

**Star TSP100 / TSP143** — 80 mm paper, 72 mm print width, 42 columns at font A,
203 dpi. Set the driver's *Document Cut Type* to **Partial Cut** and let the
driver cut; if the caller also sends an ESC/POS cut you get two cuts and waste a
receipt's worth of paper every time.

**Zebra LP 2824 Plus** — 203 dpi, 2.20″ print width = **448 dots** (`^PW448`).
It is dual-language, so make sure the installed driver is the **ZPL** one, not
EPL.

**Cash drawer** — usually wired into the receipt printer's DK port, so it opens
with an ESC/POS pulse (`ESC p 0 <t1> <t2>`) appended to the receipt bytes by the
caller. It is not a separate device and there is nothing to configure here.

## Development

```
dotnet build MaplePrint/MaplePrint.csproj      # net8.0-windows
pwsh ./publish.ps1                             # single self-contained .exe → ./dist
```

Only one instance may run per machine (a mutex enforces it) — two servers would
fight over the port, and the second would silently not be listening.
