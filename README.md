at# MaplePrint

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

**It never composes a page.** Callers send either a rendered image or device
bytes, and MaplePrint conveys them. It is a paper path, not a renderer — which
is what keeps every layout decision on the server that has the order data,
where changing one is a deploy rather than a site visit.

```
    any caller                          Counter PC (Windows)
┌──────────────────┐               ┌──────────────────────────┐
│ MapleCoffee      │               │ MaplePrint (tray)        │
│ Laravel POS      │  HTTP + key   │   :9110                  │
│ curl             │──────────────►│   POST /print            │
│ anything         │◄──────────────│     ↓ spool              │
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

## Destinations

A **destination** is a name callers address — `receipt`, `label`, `kitchen`.
Each one is mapped on this PC, in Settings, to a Windows queue plus how that
printer is driven:

| Kind | What the caller sends | For |
|---|---|---|
| `document` | a rendered image | anything with a Windows driver — lasers, inkjets, and raster receipt printers like the Star TSP100 family, which have no command language at all |
| `raw` | device bytes | printers with a real command language — ZPL, ESC/POS, PCL |

Adding a third printer is a settings entry, not a code change. Two destinations
may share one queue (say a `raw` and a `document` view of the same label
printer); jobs still serialise onto the physical device.

**Kind is declared, never detected.** Nearly every Windows queue claims to
accept RAW, so sniffing would be worthless, and paper-size heuristics fail on
exactly the odd hardware you meet in the field. The person installing the
printer knows.

Each destination also advertises `dotWidth` and `renderDpi`, so callers render
at the printer's real size instead of hard-coding it at their end.

## API

Base URL `http://<pc>:9110`.

**Authentication is off by default.** The normal deployment is a shop LAN where
every caller is already trusted, and a key there is friction without a threat.
Set one in Settings when the port is reachable from outside the LAN — a tunnel,
a port forward — where *"anyone who can route to us may print"* stops being an
acceptable sentence. Once set, it may arrive as either header:

```
X-MaplePrint-Key: <key>
Authorization: Bearer <key>
```

### `GET /health` — always unauthenticated

```json
{ "status": "ok", "station": "COUNTER-1", "version": "1.1.0", "authRequired": false }
```

### `GET /destinations`

```json
{
  "station": "COUNTER-1",
  "destinations": [
    { "name": "receipt", "kind": "document", "contentType": "image",
      "queue": "Star TSP100 Cutter (TSP143)", "dotWidth": 576, "renderDpi": 203,
      "installed": true, "enabled": true },
    { "name": "label", "kind": "raw", "contentType": "raw", "language": "zpl",
      "queue": "DRINK LABEL", "dotWidth": 448, "renderDpi": 203,
      "installed": true, "enabled": true }
  ],
  "queues": ["Star TSP100 Cutter (TSP143)", "DRINK LABEL", "…"]
}
```

`/printers` is a deprecated alias for the same response.

### `POST /print`

```json
{
  "destination": "receipt",
  "contentType": "image",
  "payloadBase64": "iVBORw0KGgo…",
  "copies": 1,
  "description": "Order #1042",
  "jobId": "optional-caller-id"
}
```

`contentType` is optional — the destination's kind implies it. Response:

```json
{ "jobId": "a947f7…", "success": true,
  "destination": "receipt", "queue": "Star TSP100 Cutter (TSP143)" }
```

### `POST /print/{destination}`

The same thing with the payload as the raw request body — no JSON, no base64.
`contentType` is inferred from the HTTP `Content-Type` (`image/*` → image,
`application/octet-stream` → raw). Exists so the whole path can be proved with
one command:

```bash
curl -X POST http://192.168.1.20:9110/print/receipt \
     -H "Content-Type: image/png" \
     --data-binary @receipt.png
```

### Status codes

| Code | Meaning |
|---|---|
| `200` | spooled |
| `400` | malformed request — bad base64, empty body, bad `copies` |
| `401` | missing or wrong key |
| `404` | no such destination, or its queue isn't installed |
| `409` | destination is turned off, or has no printer assigned |
| `415` | wrong content type for that destination's kind |
| `502` | the printer refused it or timed out — *retry-able* |
| `503` | that printer is still busy with an earlier job |

The 4xx/5xx split is deliberate: `4xx` means fix your request, `502` means the
request was fine and the device let us down.

`415` is the one that earns its keep. Sending ESC/POS to a laser printer ejects
several pages of nonsense — silently, because the datatype is valid and the
driver simply misreads the bytes as its own language. Checks run request-shape
first and station-state second, so a caller integrating against a till whose
printer is unplugged is still told its content type is wrong, rather than having
its own bug masked by the station's.

## Rendering for a `document` destination

MaplePrint maps the artwork at its **declared** DPI, not the device's:
`pixels ÷ renderDpi = inches`. A 576-dot receipt at 203 dpi is 2.84in wide on
any printer — exactly 1:1 on the thermal head it was drawn for, and correctly
scaled on a 600 dpi laser. (Mapping to device dots instead would print that same
receipt 0.96in wide on the laser, a stamp in the corner of a sheet.)

Four rules on the caller's side, and print-quality complaints almost always
trace to one of them:

1. **Render at the advertised `dotWidth`.** Read it from `/destinations`; don't
   hard-code it. Any other width forces MaplePrint to scale.
2. **No anti-aliasing on barcodes or QR.** Draw them pure black and white. Grey
   module edges get dithered by the driver, and scanners reject the result.
   Text may be anti-aliased; codes may not.
3. **Integer module size in dots.** At 203 dpi, 4 dots per QR module ≈ 0.5 mm.
   Fractional sizes make some modules a dot wider than others.
4. **Keep the quiet zone** — at least 4 modules of white around a QR. Cropping
   tight is a common cause of "it prints perfectly but won't scan."

Because the printer never has to understand the content, a QR code needs no
support from the printer's command set at all.

## Setup

1. Download `MaplePrint-{version}.exe` and double-click it. No installer.
   Settings live in `%APPDATA%\MaplePrint\` and survive upgrades.
2. Right-click the printer icon → **Settings…**

   | Field | Notes |
   |---|---|
   | Station name | defaults to the PC name |
   | Listen on port | default `9110` |
   | Accept jobs from other machines | off = loopback only |
   | Destinations | name, printer, kind, dots, DPI, paper form |
   | API key | blank = no key required; **Generate** one only if you need it |

3. Paste the address (tray menu → **Copy server address**) into whatever is
   going to print.
4. Tick **Start MaplePrint when I sign in**.

If **Accept jobs from other machines** is on, Windows Firewall may also need to
allow the port before another machine can reach it.

### Paper form

Per destination, for `document` printers:

- **blank** — leave the driver on its configured form. The safe default: it
  respects what the installer set up, and a receipt printer already set to
  `72mm x Receipt` is correct as-is.
- **a form name** — e.g. `72mm x Receipt`, `A4`. Selected exactly.
- **`auto`** — a page built to the size of the artwork. Best-effort: many
  drivers silently ignore custom page sizes, so this is opt-in rather than the
  default.

## Troubleshooting

**Test print** in the tray menu prints locally, without the API involved at all.
That is the first thing to try when a shop reports "nothing prints", because it
splits the problem cleanly:

- test works, real jobs don't → caller, network, or key
- test fails too → paper, power, cable, or driver

The test slip is a calibration slip on purpose. The rules top and bottom must
touch both paper edges; the scale bar must measure exactly one inch with a
ruler; and the 1-dot and 4-dot checkerboards must print sharp, not as grey mush.
That last check predicts whether a QR code will scan, because a QR is the same
problem — small hard-edged squares that must survive intact.

**Activity…** shows every job with the reason for any failure, and writes
`%APPDATA%\MaplePrint\activity.log` (rolled at 1 MB).

### Headless diagnostics

Both write their result to `%TEMP%\mapleprint-selftest.txt`, and neither needs
the tray or touches settings — meant for "run this line and send me what it
produced":

```
MaplePrint.exe --render-test "C:\temp\slip.png" [dotWidth]
MaplePrint.exe --print-test  "Star TSP100 Cutter (TSP143)" [outputFile] [paperForm]
```

## Printer notes

**Star TSP100 / TSP143** — 80 mm paper, 72 mm printable = **576 dots** at
203 dpi. The futurePRNT models are **raster only**: they have no command
language, so they are `document` destinations and ESC/POS sent to them prints
nothing useful. Let the driver cut (its *Document Cut Type* is already
`Partial Cut`) rather than sending a cut of your own.

**Zebra LP 2824 Plus** — 203 dpi, 2.20″ printable = **448 dots**. Speaks ZPL, so
it works either way: `raw` for native barcode elements and tiny payloads, or
`document` for QR codes, logos and real fonts without the printer needing to
understand them. Its **Stocks** tab must match the media actually loaded, or
labels print off-register or feed a blank between each one.

**Cash drawer** — usually wired into the receipt printer's DK port. Not
supported yet; it belongs to the station rather than to any printer, since a
card payment prints a receipt without opening the drawer and a no-sale opens the
drawer without printing.

## Development

```
dotnet build MaplePrint/MaplePrint.csproj      # net8.0-windows
pwsh ./publish.ps1                             # single self-contained .exe → ./dist
python3 tools/make_icon.py                     # regenerate the tray icon
```

Only one instance may run per machine (a mutex enforces it) — two servers would
fight over the port, and the second would silently not be listening.
