# MaplePrint

A local **print server** for Windows. It accepts print jobs over HTTP, spools
them to the printers attached to that PC, and reports what happened.

- One Windows-only `.exe`, no install required
- Lives in the notification area; no main window
- Knows nothing about who is calling it
- Opens the cash drawer wired to a receipt printer, per job

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

**It never judges a job.** No content inspection, no rate limit, no
de-duplication, no quiet hours. A caller that sends the same slip forty times
overnight gets forty slips, because the alternative is a printer that decides
which of your receipts mattered — and the day it guesses wrong, it does so
silently, at the counter. A runaway caller is a bug in the caller, and it is
fixed there; MaplePrint's job is to make it visible in the activity log, not to
paper over it. The one exception is a re-kicked cash drawer, which is suppressed
per job id, and only because an open till is a safety problem rather than a
wasted slip.

```
    any caller                          Counter PC (Windows)
┌──────────────────┐               ┌──────────────────────────┐
│ MapleCoffee      │               │ MaplePrint (tray)        │
│ Laravel POS      │  HTTP + key   │   :9110                  │
│ curl             │──────────────►│   POST /print            │
│ anything         │◄──────────────│     ↓ spool              │
└──────────────────┘   result      │   USB001      USB002     │
                                   │   Star        Zebra      │
                                   │    └ DK port             │
                                   │      cash drawer         │
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
      "installed": true, "enabled": true,
      "drawer": { "commandSet": "star", "pulseMs": 200, "unit": 1 } },
    { "name": "label", "kind": "raw", "contentType": "raw", "language": "zpl",
      "queue": "DRINK LABEL", "dotWidth": 448, "renderDpi": 203,
      "installed": true, "enabled": true }
  ],
  "queues": ["Star TSP100 Cutter (TSP143)", "DRINK LABEL", "…"]
}
```

`/printers` is a deprecated alias for the same response.

`drawer` is absent on a destination with no cash drawer, which is every one
until somebody sets one up — so a POS can find out at integration time whether
this counter has a till, rather than at the counter from a drawer that does not
move.

### `POST /print`

```json
{
  "destination": "receipt",
  "contentType": "image",
  "payloadBase64": "iVBORw0KGgo…",
  "copies": 1,
  "description": "Order #1042",
  "jobId": "optional-caller-id",
  "openDrawer": true,
  "reason": "sale:1042",
  "user": "e.ngo"
}
```

`contentType` is optional — the destination's kind implies it. Response:

```json
{ "jobId": "a947f7…", "success": true,
  "destination": "receipt", "queue": "Star TSP100 Cutter (TSP143)",
  "drawerOpened": true }
```

`openDrawer`, `reason` and `user` are all optional and all default to nothing,
so a caller written before drawers existed behaves exactly as it always did.
See [Cash drawer](#cash-drawer).

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

The drawer fields have no JSON to live in here, so they are headers:

| Header | Meaning |
|---|---|
| `X-MaplePrint-Open-Drawer` | `true`, `yes` or `1` |
| `X-MaplePrint-Reason` | `sale:1042`, `no-sale:e.ngo`, `reprint:1042` |
| `X-MaplePrint-User` | who asked |
| `X-MaplePrint-Job-Id` | the caller's own id — see [retries](#retries-and-the-drawer) |
| `X-MaplePrint-Description` | what shows in the activity log |

An empty body with the drawer header is a no-sale — which is what makes the
whole path provable in one line:

```bash
curl -X POST http://192.168.1.20:9110/print/receipt \
     -H "X-MaplePrint-Open-Drawer: true" \
     -H "X-MaplePrint-User: e.ngo" \
     --data-binary ""
```

### Status codes

| Code | Meaning |
|---|---|
| `200` | spooled |
| `400` | malformed request — bad base64, empty body, bad `copies`, or a job that neither prints nor opens the drawer |
| `401` | missing or wrong key |
| `404` | no such destination, or its queue isn't installed |
| `409` | destination is turned off, has no printer assigned, or has no cash drawer set up |
| `415` | wrong content type for that destination's kind |
| `502` | the printer refused it or timed out — *retry-able* |
| `503` | that printer is still busy with an earlier job |

The 4xx/5xx split is deliberate: `4xx` means fix your request, `502` means the
request was fine and the device let us down.

No new codes were added for the drawer, deliberately: the shipped MapleCoffee
client switches on this exact set and renders anything else as a bare number.
A failed drawer-only job is the one 502 that carries `"retryable": false` in the
body — see [Retries and the drawer](#retries-and-the-drawer).

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

## Cash drawer

The drawer is a solenoid on the **receipt printer's** RJ11/RJ12 DK port. It is
not a network device, has no driver and no API: opening it means sending a few
bytes to the printer it is plugged into.

**Assumed throughout:** the drawer is always on a receipt printer. It is never
hung off a Clover Station or a PAX terminal — payment terminals are payment-only
here. If a merchant ever wires a drawer to a Clover, that is a new piece of work,
not a settings change.

### Three things a caller can ask for

| | Payload | `openDrawer` | Result |
|---|---|---|---|
| Sale, cash | receipt bytes | `true` | receipt prints, drawer opens |
| Sale, card | receipt bytes | `false` | receipt prints, drawer stays shut |
| No sale | *empty* | `true` | drawer opens, no paper |

These are not three code paths — one job type, a payload that may be empty and a
boolean. A job with neither is a `400`; it is never sent.

### Setting one up

In **Settings → Destinations**, click **Show detail**:

| Column | Notes |
|---|---|
| **Drawer** | tick it if a drawer is wired into this printer |
| **Drawer command** | guessed from the printer name — confirm it with the button |
| **Pulse ms** | 200 by default; some latches need 250–300 |
| **Unit** | 1, or 2 for a second drawer on the same printer |
| **Open drawer** | fires it now, using what is on screen — no need to save first |

Ticking **Drawer** fills the command in from the queue name: anything containing
`star` or `tsp` is treated as a Star, everything else as ESC/POS, which is most
of the market. **That is a guess, and it is meant to be checked.** Press **Open
drawer**. If nothing moves, switch to the other command set and press it again.
If it clicks but stays shut, the latch wants a longer pulse — try 250 or 300.

Blank means no drawer. There is no separate on/off switch, because "configured
but disabled" is one more state to get wrong at a counter.

A drawer configured wrongly — an unknown command set, a pulse the printer cannot
express — stops the server at startup with the reason in the tray, rather than
failing quietly at the counter mid-sale.

### The two command sets

The kick command is **not standard across brands**:

| Brand | Set |
|---|---|
| Star TSP100 / TSP143III | **Star native** |
| Epson (TM-T88, TM-m30), Bixolon, Citizen, SNBC, Xprinter, Rongta, generic | **ESC/POS** |

```
Star     ESC BEL n1 n2   1B 07 n1 n2    set pulse, unit = 10ms
         BEL             07             drive peripheral unit 1
         SUB             1A             drive peripheral unit 2

ESC/POS  ESC p m t1 t2   1B 70 m t1 t2  m = 00 -> drawer 1, 01 -> drawer 2
                                        t1/t2 = on/off, unit = 2ms
```

**The time unit differs — 10ms on Star, 2ms on ESC/POS.** The same byte `0x14`
is 200ms on a Star and 40ms on an Epson, and a 40ms pulse does not throw most
latches: the solenoid clicks, the drawer stays shut, and it presents as *"the
drawer randomly doesn't open"*. That is why configuration stores **milliseconds,
never bytes**, and why the pulse ceiling differs — 1270ms on Star, 510ms on
ESC/POS. A pulse outside what the chosen set can express is refused at startup
rather than clamped silently.

The full five-byte sequence goes out every time, never a bare `07`: the
pulse-width command is sticky on some TSP100 firmware, so a lone kick would
inherit whatever width the previous job left behind.

The Star TSP100 series is raster-only and will not accept ESC/POS at all. Some
other Star models expose an ESC/POS emulation switch — do not rely on it; treat
a Star as a Star.

### How the bytes get there

On a `raw` destination the kick is **prepended to the receipt** — one job, drawer
firing as the paper starts moving.

A `document` destination cannot be prepended to: its receipt is a rendered page
drawn through the driver, and there is no byte stream in front of it. The field
TSP143 is exactly that case, since the futurePRNT models are raster-only. There
the kick goes as its **own RAW job immediately ahead of the page** — two spooler
jobs, one hold of that printer's queue, order preserved. Both use the RAW
datatype, which is what puts the bytes in front of the printer's own command
parser instead of the driver's renderer.

**Do not** solve this with the driver's *Peripheral Unit 1 = Document Top*
setting. That fires the drawer on every job, which breaks the card and gift-card
cases and destroys the audit trail.

### Retries and the drawer

A re-printed receipt wastes a slip. A re-kicked drawer is a till standing open
with nobody in front of it. So the two are not retried alike:

- **Send a `jobId` and reuse it across retries.** MaplePrint remembers which job
  ids have opened the drawer (10 minutes) and a repeat reprints **without**
  kicking, answering `"drawerOpened": false`. Without a `jobId` there is nothing
  to recognise the retry by, and the till opens again.
- The claim is made *before* the bytes go out, so a kick that succeeded and a
  payload that then failed counts as already-opened. Safe direction.
- A failed **drawer-only** job answers `502` with `"retryable": false`. It is for
  a person to look at, not for a retry loop.
- MaplePrint never retries anything by itself.

**The shipped MapleCoffee client does neither.** It sets no `jobId`, and its HTTP
timeout (5s) is shorter than MaplePrint's own (up to 75s) — so a job can time out
at the caller and still print, though its catch block assumes the opposite. Today
that costs a duplicate receipt; with a drawer attached the same path is a second
till opening. **Set a stable `jobId` before enabling a drawer on a station that
client prints to.**

### Who decides whether the drawer opens

MaplePrint does not. It takes a boolean, because it knows nothing about orders,
tenders or people — that is the whole design. The decision belongs in the POS,
computed from the payment lines rather than offered as a checkbox. Left as a
cashier-facing toggle, merchants set "always open" and the audit trail becomes
worthless.

| Tender | Drawer |
|---|---|
| Cash, or cash with change due | open |
| Card (PAX A35, Clover) | closed |
| Gift card (magstripe) | closed |
| Split payment with any cash component | open |
| Refund paid out in cash | open |

Likewise **no-sale gating** — is a cashier signed in, may this person open a till
without a sale — is the POS's to enforce. MaplePrint has no user accounts, no
roles and no password check; its only authentication is one shared key for the
whole PC. `user` is a string a caller sends us. We write down what we were told.

### Audit

Every open, suppression, failure and refusal is appended to
`%APPDATA%\MaplePrint\drawer.log` — one JSON object per line:

```json
{"at":"2026-08-22T14:32:07+07:00","station":"COUNTER-1","destination":"receipt",
 "queue":"Star TSP100 Cutter (TSP143)","outcome":"opened","commandSet":"star",
 "pulseMs":200,"unit":1,"user":"e.ngo","reason":"no-sale:e.ngo","jobId":"a947f7"}
```

Separate from `activity.log`, which rolls at 1 MB and keeps one previous file —
fine for *"why didn't a receipt come out this morning"*, useless for *"no-sale
opens by employee, by shift, last quarter"*, which is what a merchant eventually
asks for. Nothing here is ever deleted; a full file is archived under a dated
name. Setup tests are recorded too, as `reason: test:setup` — an audit with the
testing missing has a hole in it exactly where somebody would look first.

### Hardware verification

None of the above proves a solenoid moves. Run this against real hardware —
**one Star and one ESC/POS printer**, each on LAN and on USB:

- [ ] Sale with cash — receipt prints **and** drawer opens.
- [ ] Sale with card — receipt prints, drawer stays shut.
- [ ] No sale — drawer opens, **no paper moves at all**. On the `document`
      (raster / futurePRNT) path especially: if a bare kick feeds or prints
      anything, say so rather than working around it — futurePRNT has utility
      hooks that can intercept RAW jobs.
- [ ] A drawer that needs a longer pulse — set 300ms, confirm it releases.
- [ ] Printer switched off mid-job — the caller gets `502`, and a drawer-only
      job gets `"retryable": false`.
- [ ] Two jobs sent to one printer at the same instant — two whole receipts, in
      order, never interleaved, and exactly one drawer open each.
- [ ] Same `jobId` sent twice — receipt prints twice, drawer opens **once**.

**The signature to watch for:** a drawer that opens on the Star and not on the
Epson at the same configured pulse width is the unit-conversion bug, not a bad
drawer.

### Not built

- **Drawer-open status** via the ASB status block. A future hook; the RAW path
  used here is one-way and reads no reply.
- **Second drawer** (peripheral unit 2). The byte is in place and the unit is
  configurable, but nothing builds a workflow around it.
- **Direct TCP to port 9100.** Every printer in the field is reached as a Windows
  queue. The endpoint type exists in the code but is not implemented; the trigger
  to build it is the hardware check above finding that futurePRNT intercepts RAW
  jobs, which would leave LAN units needing a way round the driver.

## Setup

1. Download `MaplePrint-{version}.exe` and double-click it. No installer.
   Settings live in `%APPDATA%\MaplePrint\` and survive upgrades.
2. Right-click the printer icon → **Settings…**

   | Field | Notes |
   |---|---|
   | Station name | defaults to the PC name |
   | Listen on port | default `9110` |
   | Accept jobs from other machines | off = loopback only |
   | Destinations | name, printer, kind, dots, DPI, paper form, cash drawer |
   | API key | blank = no key required; **Generate** one only if you need it |

3. Paste the address (tray menu → **Copy server address**) into whatever is
   going to print.
4. Tick **Start MaplePrint when I sign in**. The entry is re-checked and
   repaired at every start, so it survives dropping a new .exe in a new folder.

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

**Printer commands…**, in that window, asks a printer to perform one of its own
system functions — report its configuration, calibrate, restart, default.
Offered per destination and chosen by the destination's language, so a ZPL
printer gets the ZPL commands and a receipt printer gets `ESC @`; a `document`
destination is offered nothing, having no command language to speak to.

Two things it is honest about. Every command is one-way — these printers answer
status queries only over a channel the spooler does not give us — so a success
means the bytes were accepted, never that they were obeyed, and each command
says what should appear on paper if it was. And the one command that discards
settings a shop tuned asks first, stating what is lost rather than merely
whether you are sure.

Anything not on the menu can still be sent: a `raw` destination takes arbitrary
device bytes over `POST /print/{destination}`, which is all the menu is doing.
It exists because knowing *which* bytes is the hard part, not sending them.

### Headless diagnostics

All three write their result to `%TEMP%\mapleprint-selftest.txt`, and none needs
the tray or touches settings — meant for "run this line and send me what it
produced":

```
MaplePrint.exe --render-test "C:\temp\slip.png" [dotWidth]
MaplePrint.exe --print-test  "Star TSP100 Cutter (TSP143)" [outputFile] [paperForm]
MaplePrint.exe --self-test   [resultFile]
```

`--self-test` needs no printer at all and exits non-zero on failure — the first
thing to run on a counter PC that is behaving unlike an identical one beside it.

### It didn't start after the PC rebooted

**Start MaplePrint when I sign in** writes one value under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, pointing at the .exe's
current path. Per-user rather than a service, deliberately: the agent prints
through the spooler on behalf of a signed-in user, and a service in session 0
may not see that user's printer connections at all.

Because it stores a path, two ordinary things break it — moving the .exe (an
upgrade dropped in a new folder leaves the old path registered) and cleanup
tools that prune `Run` keys. So MaplePrint re-checks the entry at every start
and rewrites it if it is missing or points elsewhere, logging a line when it
does. It only ever heals in the enabled direction: an entry somebody added by
hand is left alone.

If it still doesn't come back, the usual causes are that nobody is signed in
(the PC boots to a lock screen — this starts at *sign-in*, not at boot), or
group policy is blocking `Run` keys. Confirm what is registered with:

```
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v MaplePrint
```

### Exit leaves it running in Task Manager

Fixed in 1.3.0, and worth knowing what it was, because the symptom is confusing:
the tray icon disappeared, so the app looked closed, while the process stayed
resident and kept answering print jobs on the port. A second copy then refused
to start, saying MaplePrint was already running, with no icon to be found.

Two things caused it, both on the way out. Shutdown was awaited on the UI thread
after its message loop had already ended, so the continuation had nowhere to
run; and Kestrel's default console lifetime — pointless in a tray app with no
console — hooks `ProcessExit` and blocks there waiting on a host it thinks is
still up. Exit now drains off the UI thread with a 12-second ceiling, installs
no console lifetime, and calls `Environment.Exit` once the message loop returns.
Exit means exit: there is nobody at a counter to open Task Manager.

### The drawer doesn't open

Settings → **Open drawer** on that row is the split, the same way **Test print**
splits a printing fault: it sends the kick and nothing else, using what is on
screen rather than what is saved.

| What happens | What it is |
|---|---|
| Nothing at all, no sound | Wrong command set. A Star does not understand ESC/POS and the reverse. Switch it and press again — that is one dropdown, and it is why the button sits in the row. |
| A click, but the drawer stays shut | The latch needs a longer pulse. Try 250, then 300. |
| Opens on the Star, not on the Epson, at the same pulse | The unit-conversion bug: 10ms units on Star, 2ms on ESC/POS. Check the printer has the right **Drawer command**, not just a plausible pulse number. |
| Opens on **every** job, including card sales | Somebody enabled *Peripheral Unit 1 = Document Top* in the printer driver. Turn it off. MaplePrint drives the drawer per job; the driver setting fires it on all of them, which is what makes the audit trail worthless. |
| Never opens, and the button works | The caller is not asking for it. MaplePrint takes `openDrawer` and does what it is told — deciding cash-versus-card is the POS's job. |
| The caller reported a failure, but the till opened | Likely both are right. A caller whose HTTP timeout is shorter than the printer takes will give up on a job that then completes; `drawer.log` records what actually happened. |

`%APPDATA%\MaplePrint\drawer.log` answers the rest — one line per open,
suppression, failure and refusal, with the command set and pulse actually used.

### A blank label between every label

One Zebra prints correctly and a second of the same model prints the label and
then feeds a blank one, both perfectly registered. Same order, same destination,
same bytes.

**It is not the agent.** A `raw` destination is spooled verbatim: `RawPrinter`
hands the payload to `WritePrinter` as datatype `RAW`, appends nothing, and does
not even apply `copies`. Prove it from the shop PC rather than arguing about it
— with **Save a copy of each job** on, exactly what went to the printer is in
`%LOCALAPPDATA%\MaplePrint\jobs\*.zpl`. One file per label means one label was
sent, and the extra one is the printer's doing.

**It is the printer's stored label length.** ZPL's `^LL` is honoured only on
continuous media, so on gap media the feed distance comes from the printer's own
calibration, not from the job. A unit whose stored length is longer than the
label carries on past the first web to satisfy it and stops at the next one:
printed label, blank label, every time. Registration stays perfect throughout,
because the gap sensor is fine — only the length is wrong. This is why two
identical printers disagree about identical bytes: the number lives in the
printer.

Zebra's own one-second check — **press Feed. Exactly one blank label should
feed.** Two is this fault, and no order needs to be rung up to see it.

A label format can decline to depend on printer state, by naming the media
tracking mode (`^MNY` for gap media) and putting `^LL` *before* the first field
rather than after the last one. That belongs to whichever caller composes the
label; MaplePrint never edits a payload.

#### Feed button modes

Powered on and cover closed, hold Feed and release after the flash group you
want. Overshoot past eight and it exits having done nothing, so a miscount costs
nothing.

| Flashes | Action |
|---|---|
| 1 | Print configuration status label |
| 2 | **Standard media calibration** — detects media type and length, feeds 1–4 labels |
| 3 | Serial port configuration (serial-interface printers only) |
| 4 | **Factory defaults**, then a standard media calibration |
| 5 | Print width adjustment |
| 6 | Print darkness (density) adjustment |
| 7 | Manual media calibration |
| 8+ | Exits, no action |

Two traps in that table:

- **Manual calibration (7) disables auto calibration.** Only a factory default
  (4) turns it back on. A printer someone once calibrated by hand is stuck on a
  stored length — which is exactly this fault.
- **Factory defaults (4) also clears darkness and print width.** Print the
  configuration label from *both* printers first (1), so the working one's
  settings can be put back on the other. After releasing at four flashes the
  light turns amber for three seconds; do nothing and the printer defaults on
  its own. Holding Feed again during those seconds chooses what is reset —
  1 flash network only, 2 printer only, 3 both — which is how a networked
  printer loses the IP address that was the only way to reach it.

So, in order: **1** on both and compare `LABEL LENGTH`, `MEDIA TYPE` and
`SENSOR SELECT` → **2** on the offender, then press Feed and count the blanks →
**4** only if it survives that, and re-apply darkness and width afterwards.

The first, second and fourth of those are on the **Printer commands…** menu in
the Activity window, for a shop with nobody standing at the printer:

| Menu | ZPL | Does |
|---|---|---|
| Print configuration | `~WC` | Print the configuration label |
| Calibrate media | `^XA~JC^XZ` | Measure the media and set the sensors to it |
| Restore factory settings | `^XA^JUF^JUS^XZ` | Reload factory settings and save them — printer only; `^JUN`, which defaults the network and takes the IP address with it, is deliberately not offered |

The five- and six-flash width and darkness adjustments have no command
equivalent, and the four-flash button reset defaults more than `^JUF` does.
Those stay a walk to the printer.

Sequences and semantics are from the
[LP 2824 Plus User Guide](https://cpws.zebra.com/cpws/docs/2824plus/lp2824plus_Feed_Buttons.pdf)
(980644-001 Rev. A) and Zebra's
[^JU application note](https://www.zebra.com/content/dam/support-dam/en/documentation/unrestricted/application-notes/zpl-ju-command-app-note.pdf).

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
labels print off-register or feed a blank between each one — see
[A blank label between every label](#a-blank-label-between-every-label), which
is the same fault reached from the printer's side.

**Cash drawer** — wired into the receipt printer's DK port, and driven per job
from code rather than from the driver's *Peripheral Unit* setting, so a card
payment can print a receipt without opening it and a no-sale can open it without
printing. See [Cash drawer](#cash-drawer).

## Development

```
dotnet build MaplePrint/MaplePrint.csproj      # net8.0-windows
MaplePrint.exe --self-test                     # checks that need no printer
pwsh ./publish.ps1                             # single self-contained .exe → ./dist
python3 tools/make_icon.py                     # regenerate the tray icon
```

`--self-test` writes to `%TEMP%\mapleprint-selftest.txt` (or a path you name)
and exits non-zero on failure, so CI can gate on it. It covers the drawer byte
sequences for both command sets, the three scenarios, retry suppression,
per-printer serialization and the wire shapes the shipped MapleCoffee client
depends on. There is no test project on purpose — MaplePrint ships as one
self-contained .exe with no external packages, and a test runner would be the
first one.

Only one instance may run per machine (a mutex enforces it) — two servers would
fight over the port, and the second would silently not be listening.
c