# webos-mcp

A typed [MCP](https://modelcontextprotocol.io) server for controlling a single
LG webOS TV over the local network — power, apps, browser, YouTube, inputs,
media, navigation and notifications.

Built on .NET 10. One shared tool layer is served over **stdio** (local MCP
clients) and **Streamable HTTP** (container / network use), backed by three
protocols: the LG **SSAP** WebSocket for control, **Wake-on-LAN** for power-on,
**DIAL** to discover the YouTube receiver, and the YouTube **Lounge** protocol to
control it and read back what is actually playing.

- **Local-first, with one documented exception.** Everything talks to the TV on
  your LAN. YouTube playback control is the exception: it uses Google's Lounge
  service, so it needs outbound internet. See
  [YouTube control](#youtube-control-and-the-one-cloud-dependency).
- **Typed and validated.** Every tool takes a typed request and validates its
  inputs before touching the TV.
- **Single TV by design.** No device registry, no per-call routing.
- **Deliberately narrow.** No raw command passthrough, no screenshot capture,
  no hidden/service-menu actions. See [Hard boundaries](#hard-boundaries).
- **Pairing is opt-in.** By default no pairing tool is exposed at all. It can be
  enabled explicitly — see [Pairing over MCP](#pairing-over-mcp-opt-in).

---

## Table of contents

- [Requirements](#requirements)
- [Quick start — stdio](#quick-start--stdio)
- [Quick start — HTTP and Docker](#quick-start--http-and-docker)
- [Device setup without environment variables](#device-setup-without-environment-variables)
- [Discovery and pairing](#discovery-and-pairing)
- [Pairing over MCP (opt-in)](#pairing-over-mcp-opt-in)
- [Configuration reference](#configuration-reference)
- [Tools](#tools)
- [Error contract](#error-contract)
- [Hard boundaries](#hard-boundaries)
- [Limitations](#limitations)
- [Compatibility statement](#compatibility-statement)
- [Troubleshooting](#troubleshooting)
- [Development](#development)
- [License](#license)

---

## Requirements

- **A webOS TV with SSAP enabled.** Broadly, LG smart TVs from webOS 3.0
  onward expose the SSAP control endpoint on TCP **3000** (plaintext) or
  **3001** (TLS). This is the same channel the LG mobile remote app uses.
- **The TV and the server on the same LAN segment.** Discovery uses SSDP
  multicast, and Wake-on-LAN uses a subnet broadcast; neither crosses a
  routed boundary by default.
- **"LG Connect Apps" / network control enabled** on the TV
  (*Settings → Network*, naming varies by firmware).
- **Wake-on-LAN enabled** if you want `tv_power_on` to work from standby.
  On most models this is *Settings → General → Mobile TV On* (sometimes
  "Turn on via Wi-Fi" / "Turn on via Ethernet").
- **.NET 10 SDK** to build from source, or Docker to run the container.

> Example addresses in this document — `192.0.2.10`, `192.0.2.255`,
> `00:11:22:33:44:55`, `<client-key>` — are documentation-only placeholders
> ([RFC 5737](https://datatracker.ietf.org/doc/html/rfc5737)). Substitute your
> own.

---

## Quick start — stdio

Build it:

```bash
git clone https://github.com/anurmatov/webos-mcp.git
cd webos-mcp
dotnet build -c Release
```

Find your TV and pair with it:

```bash
export WEBOSMCP__HOST=192.0.2.10
export WEBOSMCP__MACADDRESS=00:11:22:33:44:55

dotnet run --project src/WebosMcp.Server -- discover
dotnet run --project src/WebosMcp.Server -- pair      # accept the prompt on the TV
dotnet run --project src/WebosMcp.Server -- status
```

Then register the server with your MCP client. For Claude Desktop, in
`claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "webos-mcp": {
      "command": "dotnet",
      "args": ["/path/to/webos-mcp/src/WebosMcp.Server/bin/Release/net10.0/webos-mcp.dll", "stdio"],
      "env": {
        "WEBOSMCP__HOST": "192.0.2.10",
        "WEBOSMCP__MACADDRESS": "00:11:22:33:44:55",
        "WEBOSMCP__BROADCASTADDRESS": "192.0.2.255"
      }
    }
  }
}
```

`stdio` is the default, so bare `webos-mcp` with no argument does the same
thing.

> On stdio, stdout is the protocol channel. All logging goes to stderr.

> The YouTube Lounge token is sent as a form field on the bind and as an
> `X-YouTube-LoungeId-Token` header on commands. The **event subscription also
> carries it in the query**, because that is the request shape the receiver was
> proven to accept — so a live credential does reach a request URI on that one
> path. All three entry points therefore raise the HTTP logging categories to
> `Warning`, which is what keeps request URIs out of the log stream. Warnings and
> errors still surface; they carry no URI.

---

## Quick start — HTTP and Docker

```bash
cp docker-compose.example.yml docker-compose.yml
mkdir -p secrets
openssl rand -hex 32 > secrets/http_token

# Pair once. The key is written to the named volume, so it survives the
# container being replaced.
docker compose run --rm webos-mcp pair
docker compose up -d
```

Point an HTTP MCP client at `http://127.0.0.1:8765/` with an
`Authorization: Bearer <token>` header.

**The HTTP transport binds to loopback only unless you configure a token.**
Setting a non-loopback bind address without `WEBOS_MCP_HTTP_TOKEN` or
`WEBOS_MCP_HTTP_TOKEN_FILE` makes the server **refuse to start** — it will not
silently serve unauthenticated, state-changing TV control to your network. See
[Limitations](#limitations).

**The compose example uses `network_mode: host`.** That is the supported path
for Wake-on-LAN over Docker — see the [WOL caveat](#wake-on-lan-in-bridge-mode).

---

## Device setup without environment variables

You can point the server at a TV entirely through MCP — no `WEBOSMCP__HOST`,
`MACADDRESS` or `BROADCASTADDRESS` needed:

| Tool | What it does |
|---|---|
| `tv_discover_devices` | Scan the LAN by SSDP, or pass an address to probe it directly. Registers nothing. |
| `tv_register_device` | Register an address and make it active. Derives MAC and broadcast; re-registering a known address updates it. |
| `tv_list_devices` | Registered TVs and which is active. |
| `tv_select_device` | Switch the active TV. Takes effect immediately, no restart. |
| `tv_update_device` | Override a derived value — usually a MAC the network could not supply. |
| `tv_remove_device` | Forget a TV. Removing the active one promotes another. |

The device book is JSON at `~/.webos-mcp/devices.json` (override with
`WEBOSMCP__DEVICESTOREPATH`; in a container point it at a writable volume). It
holds **no secret** — the pairing key stays in its own file.

**Explicit environment configuration always wins.** If you set `WEBOSMCP__HOST`,
a stored device never overrides it. Silent precedence in the other direction is
the kind of thing that costs an afternoon to debug.

**One TV is active at a time and no tool takes a device argument.** This is device
*setup*, not per-call routing — the single-TV design is unchanged.

**Accepting the pairing prompt on the TV is still a human step**, deliberately. It
is the boundary that stops anything pairing unattended.

**In a container, scan discovery finds nothing — that is expected.** SSDP is
multicast and does not cross a Docker bridge network. Pass the TV's address to
`tv_discover_devices` to probe it directly (a unicast TCP connect, which does
cross), or skip straight to `tv_register_device`. Neither needs multicast. An
empty scan says all this in its `hint` rather than returning a bare empty list,
which reads as "there is no TV".

**Registration never fails because an address detail could not be derived.** On a
bridge network the TV is not on the same segment, so its MAC is not in the
neighbour table — and a minimal container image has no `ping` binary either. The
device is persisted regardless, and the response reports
`wakeOnLanAvailable: false` with what to do about it: everything works except
`tv_power_on`, which needs a MAC supplied via `tv_update_device`.

---

## Discovery and pairing

Pairing is an **operator bootstrap step**, run from a terminal. It is not
reachable as an MCP tool unless you explicitly opt in — see
[Pairing over MCP](#pairing-over-mcp-opt-in). Either way it requires someone
physically present to accept the on-screen prompt; nothing pairs unattended.

```bash
webos-mcp discover   # SSDP scan; prints addresses of webOS TVs on the segment
webos-mcp pair       # connects, requests registration, waits for you to accept
webos-mcp status     # config summary, pairing state and current power state
```

`pair` writes the client key to `~/.webos-mcp/clientkey.json` (mode `0600`)
unless you configured `WEBOSMCP__CLIENTKEY` or `WEBOSMCP__CLIENTKEYFILE`, in
which case that operator-owned value is used as-is and never overwritten.

**The client key grants full control of the TV.** It is never printed by any
command, never returned by any tool, and never written to a log line — `pair`
reports only *where* it was stored. Treat it like a password.

Pairing survives restarts. If the TV later rejects a stored key (a factory
reset, for instance), every tool returns `PAIRING_REQUIRED` and you re-run
`pair`.

**The key is granted against a permission set.** The TV records which permissions
were requested at pairing time, and a key keeps exactly those capabilities — a
later version of this server asking for more does **not** widen an existing grant.
The server notices when a stored key predates its current permission set and says
so when a command is denied; it never re-pairs on its own. See
[changing the permission set](#changing-the-permission-set-requires-an-explicit-re-pair).

> **Upgrading from an earlier version:** the permission set now also requests
> `READ_RUNNING_APPS`, which is the grant covering running-app reads such as
> `tv_get_foreground_app`. Existing pairings keep their old grant, so that tool
> may keep returning `TV_PERMISSION_DENIED` until you re-run `pair` and accept the
> prompt once. Everything else keeps working in the meantime.

---

## Pairing over MCP (opt-in)

By default this server exposes **no pairing surface at all**: `pair_device` is
not registered, does not appear in `tools/list`, and cannot be called. That is
the safe default and most deployments should leave it alone — pair once with the
CLI and be done.

Enable it when an MCP client needs to (re-)pair without shell access, for
example a headless container you cannot easily exec into:

```bash
export WEBOSMCP__ENABLEPAIRINGTOOL=true
export WEBOSMCP__CLIENTKEYPATH=/var/lib/webos-mcp/clientkey.json   # must be writable
```

What the tool does and does not do:

- **It cannot pair unattended.** A human must accept the on-screen prompt. The
  tool waits up to `WEBOSMCP__PAIRINGTIMEOUTSECONDS` and then returns
  `PAIRING_TIMEOUT`.
- **It never returns or logs the client key.** The response carries the storage
  location and a status, nothing else.
- **It reports success only after the key is verified on disk.** The write is
  atomic (temp file, flush, rename) and the file is then re-read and compared. A
  key that did not land is an error, not a success.
- **It refuses up front when storage is read-only**, with
  `KEY_STORAGE_READONLY`, rather than pairing and then losing the key.
- **Already paired is not an error.** If a working key exists it returns
  `status: "already_paired"` without raising a prompt. Pass `force: true` to
  re-pair deliberately.

It is the same `PairingService` the `pair` CLI command uses — one code path, so
the two cannot drift.

**Enabling it widens the trust boundary**: any client that can reach the tool
can initiate a pairing prompt on the TV. It cannot complete one alone, and it
cannot read the key, but on the HTTP transport it should be treated as another
reason to keep the bearer token and network placement tight.

---

## Configuration reference

All configuration comes from the environment. Nested keys use the standard
.NET `__` separator.

### TV connection

| Variable | Default | Description |
|---|---|---|
| `WEBOSMCP__HOST` | *(required)* | TV hostname or IP, e.g. `192.0.2.10`. |
| `WEBOSMCP__PORT` | `3000` | SSAP port. `3000` plaintext, `3001` TLS. |
| `WEBOSMCP__USETLS` | `false` | Use `wss://`. Set together with port `3001`. |
| `WEBOSMCP__MACADDRESS` | *(none)* | TV MAC, e.g. `00:11:22:33:44:55`. Required for `tv_power_on`. |
| `WEBOSMCP__BROADCASTADDRESS` | `255.255.255.255` | WOL broadcast target. Prefer your subnet's, e.g. `192.0.2.255`. |

### Pairing

| Variable | Default | Description |
|---|---|---|
| `WEBOSMCP__CLIENTKEY` | *(none)* | Client key supplied inline. |
| `WEBOSMCP__CLIENTKEYFILE` | *(none)* | Path to a mounted secret holding the key. Preferred for containers. |
| `WEBOSMCP__CLIENTKEYPATH` | `~/.webos-mcp/clientkey.json` | The **durable writable** location pairing persists to. |
| `WEBOSMCP__ENABLEPAIRINGTOOL` | `false` | Opt in to the `pair_device` MCP tool. Off by default. |
| `WEBOSMCP__PAIRINGTIMEOUTSECONDS` | `60` | How long to wait for a human to accept the prompt. |

Reading resolves in the order `CLIENTKEY` → `CLIENTKEYFILE` → `CLIENTKEYPATH`.

**Writing is a separate question.** `CLIENTKEY` and `CLIENTKEYFILE` are
operator-owned and read-only to the process, so pairing writes to
`CLIENTKEYPATH`. In a container that reads its key from a read-only mounted
secret, `CLIENTKEYPATH` **must** point at a writable volume or pairing fails
with `KEY_STORAGE_READONLY` — deliberately *before* anyone is sent to the TV.

### DIAL (YouTube playback)

`tv_youtube_play` needs the TV's DIAL endpoint. It is resolved in four steps,
stopping at the first that works:

1. `WEBOSMCP__DIALAPPLICATIONURL`, if you set it — no discovery at all.
2. A direct HTTP probe of `WEBOSMCP__HOST` on each `WEBOSMCP__DIALPORTS` port.
3. A **unicast** SSDP `M-SEARCH` sent straight to the TV.
4. A multicast SSDP `M-SEARCH`.

Steps 1–3 need no multicast, which matters because a container on a bridge
network normally cannot receive it. Step 4 is last for that reason.

| Variable | Default | Description |
|---|---|---|
| `WEBOSMCP__DIALPORTS` | `2038,1754,3000,8080,9080` | Ports probed directly on the TV. `2038` is first because that is the port LG webOS has been observed advertising DIAL on. |
| `WEBOSMCP__DIALAPPLICATIONURL` | *(none)* | Skip discovery entirely, e.g. `http://192.0.2.10:2038/apps/`. The deterministic escape hatch when neither probing nor SSDP reaches the TV. |
| `WEBOSMCP__DIALSSDPTIMEOUTSECONDS` | `3` | How long each SSDP search window stays open. |
| `WEBOSMCP__LOUNGEBASEURL` | `https://www.youtube.com` | YouTube Lounge service. The one outbound-internet dependency. |
| `WEBOSMCP__LOUNGEDEVICENAME` | `webos-mcp` | Name this remote presents to the receiver. |
| `WEBOSMCP__LOUNGEVERIFYTIMEOUTSECONDS` | `30` | How long to wait for the receiver to confirm a command. |
| `WEBOSMCP__LOUNGESUBSCRIBETIMEOUTSECONDS` | `10` | How long to wait for the receiver's event stream to start being read, before any command is sent. Readiness is a read outstanding on the stream, not response headers — the receiver announces once, to whoever is listening at that instant. Bounded separately from verification: a stream that never started delivering means nothing was sent, which is a different failure from a command that went out unconfirmed. |
| `WEBOSMCP__DEVICESTOREPATH` | `~/.webos-mcp/devices.json` | Where registered devices are stored. Must be writable to register a TV. |

To find the right value for a stubborn TV, run `M-SEARCH` from a host on the
same LAN and read the `LOCATION` header of the reply; that host and port are
what to put in `WEBOSMCP__DIALAPPLICATIONURL` (or add the port to
`WEBOSMCP__DIALPORTS`). Where no DIAL endpoint exists at all, `tv_youtube_play`
reports `TV_UNSUPPORTED_CAPABILITY` rather than a false success.

### Timeouts

| Variable | Default | Description |
|---|---|---|
| `WEBOSMCP__CONNECTTIMEOUTSECONDS` | `10` | SSAP connect timeout. |
| `WEBOSMCP__REQUESTTIMEOUTSECONDS` | `15` | Per-operation timeout. |
| `WEBOSMCP__POWERONVERIFYTIMEOUTSECONDS` | `60` | How long `tv_power_on` polls for an Active state. |
| `WEBOSMCP__POWERONPOLLINTERVALSECONDS` | `3` | Interval between those polls. |
| `WEBOSMCP__FALLBACKSTEPDELAYMILLISECONDS` | `400` | Pacing between steps of a bounded fallback sequence. |

### HTTP transport

| Variable | Default | Description |
|---|---|---|
| `WEBOS_MCP_HTTP_BIND` | `127.0.0.1` | Bind address. **Any non-loopback value requires a token.** |
| `WEBOS_MCP_HTTP_PORT` | `8765` | TCP port. |
| `WEBOS_MCP_HTTP_TOKEN` | *(none)* | Bearer token, supplied inline. |
| `WEBOS_MCP_HTTP_TOKEN_FILE` | *(none)* | Path to a mounted secret holding the token. **Takes precedence** over `WEBOS_MCP_HTTP_TOKEN`. |

The token is never accepted as a CLI argument — command lines are visible in
process listings.

---

## Tools

34 tools across seven groups, plus one opt-in pairing tool that is **not
registered by default**. Every tool returns
`{ "ok": true, "result": … }` or `{ "ok": false, "error": { "code", "message" } }`.

### Status

| Tool | Description |
|---|---|
| `tv_get_power_state` | Current power state: `Active`, `ScreenOff`, `Standby`, `Unreachable`, `Unknown`. |
| `tv_get_device_info` | Model, firmware and product information. **Partial-result safe** — see below. |
| `tv_get_foreground_app` | The app currently in the foreground. |
| `tv_list_apps` | Installed apps with their launch ids. |
| `tv_get_status` | Combined snapshot: power, foreground app and volume state. **Partial-result safe** — see below. |

#### Partial results, and where they stop

`tv_get_status` and `tv_get_device_info` each make several reads. When the session
is healthy and the TV refuses **one** of them, the others are still returned:

```json
{
  "power": "Active",
  "foregroundApp": null,
  "volume": { "volume": 12, "muted": false },
  "warnings": [
    { "field": "foregroundApp", "code": "TV_PERMISSION_DENIED", "message": "..." }
  ]
}
```

A denied field is **present and null**, not omitted, so a caller can tell "the TV
refused this" from "this tool does not return that". `warnings[].code` is always a
typed wire code — `TV_PERMISSION_DENIED`, `TV_UNSUPPORTED_CAPABILITY` or
`TV_ERROR` — never free text. When everything succeeds the `warnings` field is
absent entirely, so an all-success response is byte-identical to what these tools
returned before.

**This applies only to command-level failures.** If a read fails at the
connection or session level — `PAIRING_REQUIRED`, `TV_OFF`, `TV_UNREACHABLE`,
`TIMEOUT` — the **whole call fails with that code** and the remaining reads are
not attempted. A snapshot of nulls for a TV that is switched off would be a call
reporting success when nothing was ever read, which is the more dangerous failure
of the two.

### Power and display

| Tool | Description |
|---|---|
| `tv_power_on` | Wake via WOL **and verify** the TV reaches an Active state. Idempotent. |
| `tv_power_off` | Graceful power off (standby). |
| `tv_screen_off` | Panel off without powering down, where supported. |
| `tv_screen_on` | Panel back on. |

### Audio and media

| Tool | Description |
|---|---|
| `tv_get_volume` | Volume, mute state and active sound output. |
| `tv_set_volume` | Set volume; validated to 0–100. |
| `tv_set_mute` | Mute or unmute. |
| `tv_list_sound_outputs` | Sound outputs the TV reports. |
| `tv_set_sound_output` | Switch output; validated against the reported list. |
| `tv_media_control` | `Play`, `Pause`, `Stop`, `Rewind`, `FastForward`. |

### Apps and content

| Tool | Description |
|---|---|
| `tv_launch_app` | Launch an app by id. |
| `tv_close_app` | Close a running app. |
| `tv_open_url` | Open an **HTTPS-only** URL in the webOS browser. |
| `tv_youtube_play` | Play a specific video by id, `youtu.be` link or watch URL. Loads it into the running receiver over **Lounge** and succeeds only once the receiver reports that video id playing. |
| `tv_youtube_now_playing` | What the receiver says it is playing: video id, state, position. Pure observation. |
| `tv_youtube_pause` / `tv_youtube_resume` | Pause or resume; succeeds only once the receiver reports the new state. |
| `tv_youtube_seek` | Seek to a position in seconds. |
| `tv_youtube_next` / `tv_youtube_previous` | Move through the receiver's queue. |
| `tv_youtube_queue_add` | Append a video to the queue. `observed: false` — see below. |
| `tv_youtube_set_receiver_volume` | The receiver's own volume, distinct from TV volume. |
| `tv_youtube_set_autoplay` | Enable/disable autoplay. |
| `tv_youtube_set_playback_speed` | 0.25–2.0. `observed: false` — see below. |
| `tv_youtube_search` | **Not supported** — always returns `TV_UNSUPPORTED_CAPABILITY`. See below. |

Content tools report which path ran — `"path": "DeepLink"` or `"path": "Dial"`.

### YouTube control, and the one cloud dependency

**YouTube playback control does not run on your LAN.** DIAL is used only to
discover the receiver's screen id; loading a specific video and reading back what
is playing both go through Google's Lounge service at `youtube.com`. The server
therefore needs outbound internet for YouTube tools — and only for those. Every
other tool is LAN-only.

This is a deliberate reversal of an earlier decision, made because DIAL provably
cannot do the job:

- A DIAL launch aimed at an **already-running** YouTube session is accepted and
  ignored. The previous video keeps playing.
- DIAL exposes **no read-back** of the playing video, so nothing on that path can
  tell a correct launch from a wrong one.
- Stopping and relaunching YouTube to force the video is **not** an acceptable
  workaround: on a real TV it lands on the account/profile picker. The server
  never does it.

`tv_youtube_play` therefore succeeds only when the **receiver itself** reports the
requested video id in a playing state. A different video, a merely cued or paused
one, or a silent receiver are all reported as failures.

Two details of *how* that report is read, both of which caused false negatives:

- **The id and the playing state arrive in separate events.** The receiver sends
  `nowPlaying` carrying the video id — usually still buffering — and then
  `onStateChange` carrying the playing state with **no video id at all**. The server
  correlates them: an id-less state applies to the video most recently announced.
  Announcing a *new* id resets that state rather than inheriting the previous one, so
  a stale "playing" can never be attributed to a video that has only just appeared.
- **Something must already be reading when the command goes out.** The receiver
  announces once, to whoever is listening at that instant. The event stream is opened
  and actively pumped before `setPlaylist` is sent; response headers coming back is
  *not* treated as readiness, because nothing is reading the body at that point.

If the announcement is missed anyway, one bounded `getNowPlaying` read-back asks the
receiver directly. It is **defense in depth only** — it is judged by the identical
requested-id-plus-playing rule, so it can recover a missed event but can never turn a
wrong video into a success. The response's `confirmedVia` field names which path
actually confirmed, so a run that needed the fallback is visible as such rather than
blurred into the normal case.

**Read the `observed` field.** Every YouTube control response carries it:

- `observed: true` — the receiver confirmed the effect by reporting its own state.
- `observed: false` — the command was **accepted only**. The receiver announces no
  event for it, so nothing was verified. `tv_youtube_queue_add` and
  `tv_youtube_set_playback_speed` are always in this category. Physical probing
  shows they do work; the server still refuses to call an unobserved effect a
  confirmed one.

**Not shipped:** captions and skip-ad. Both were accepted by the receiver during
physical probing but produced no confirming event, and neither is worth a tool
that can only ever say "accepted, no idea". They can be added if the receiver's
event set turns out to cover them.

**`tv_youtube_search` is deliberately unsupported.** Physical testing showed
YouTube's custom on-screen keyboard silently ignoring
`ssap://com.webos.service.ime/insertText`: the call succeeded, nothing was
typed, and the tool reported success while the TV sat on the home screen. DIAL
carries a video id but has no documented search parameter, so there is nothing
to verify a search against either. Rather than ship a tool that lies, it
returns `TV_UNSUPPORTED_CAPABILITY` until a verifiable mechanism exists. Use
`tv_youtube_play` with a video id.

### Navigation and input

| Tool | Description |
|---|---|
| `tv_send_button` | Press a button from a fixed allowlist; `repeat` 1–20. |
| `tv_type_text` | Type into the focused field; up to 512 characters. Returns `TV_UNSUPPORTED_CAPABILITY` when the foreground app uses a custom on-screen keyboard (YouTube does) rather than typing nothing and claiming success. |
| `tv_delete_characters` | Delete 1–20 characters. |
| `tv_send_enter` | Send Enter. |
| `tv_pointer_move` | Move the pointer; each axis bounded to ±500. |
| `tv_pointer_click` | Click at the current pointer position. |
| `tv_pointer_scroll` | Scroll; each axis bounded to ±500. |

Allowlisted buttons: directional keys, `Enter`, `Back`, `Exit`, `Home`,
`Menu`, `Info`, volume and channel keys, transport keys, the four colour keys,
digits `0`–`9`, and `Dash`. There is no free-text key-name path.

### TV and inputs

| Tool | Description |
|---|---|
| `tv_list_inputs` | External inputs with their connected state. |
| `tv_switch_input` | Switch input; validated against the reported list. |
| `tv_get_current_channel` | Current channel and programme. |
| `tv_channel_up` / `tv_channel_down` | Step channels. |
| `tv_tune_channel` | Tune to `7` or `7-1`. |

Channel tools return `TV_UNSUPPORTED_CAPABILITY` on inputs and models with no
tuner information.

### Notifications

| Tool | Description |
|---|---|
| `tv_show_toast` | On-screen toast, up to 512 characters. |

### Pairing (opt-in — absent unless enabled)

| Tool | Description |
|---|---|
| `pair_device` | Pair with the TV. Requires a human to accept the on-screen prompt. Persists the key atomically and verifies it on disk before reporting success. Never returns the key. |

Not registered unless `WEBOSMCP__ENABLEPAIRINGTOOL=true`. On a default
deployment it does not appear in `tools/list` and cannot be called.
See [Pairing over MCP](#pairing-over-mcp-opt-in).

---

## Error contract

Four states a caller must be able to tell apart. Each has a distinct,
machine-checkable code — you never have to string-match a message.

| Code | Meaning | What to do |
|---|---|---|
| `PAIRING_REQUIRED` | No valid client key, or the TV rejected the stored one. | Run `webos-mcp pair`. |
| `TV_OFF` | Reachable but powered off or in standby. | Call `tv_power_on`. |
| `TV_UNREACHABLE` | No route, no response, or the connection was lost. | Check the network and the configured host. |
| `TV_UNSUPPORTED_CAPABILITY` | Connected, but this model or input does not support the action. | Nothing — the capability is absent. |
| `TV_PERMISSION_DENIED` | The session is registered and healthy; the TV refused **this command** because the capability was not granted to this pairing. | Re-pair explicitly if the server's permission set has changed — see below. Otherwise the firmware does not grant it. |

Plus `INVALID_INPUT` (rejected before any connection is opened), `TIMEOUT`
and `TV_ERROR`.

### `TV_PERMISSION_DENIED` is not `PAIRING_REQUIRED`

These were once the same code, and conflating them was actively misleading: a
denied `tv_close_app` reported "No valid client key" **immediately after another
SSAP command had succeeded on the same registered session**, sending an operator
to re-pair a pairing that was never broken.

They are now separated by which frame failed:

- On the **registration** frame, an authorization refusal really does mean the
  supplied key was not accepted → `PAIRING_REQUIRED`.
- On an ordinary **command** frame, the key is present, the session is live, and
  the very next command may succeed. The refusal is about that one capability →
  `TV_PERMISSION_DENIED`, carrying the TV's own wording, which is what
  distinguishes one denied capability from another.

**That wording is sanitised before it is used.** It arrives from the network, so
it reaches a caller's response and a log line as text this server did not author.
Control characters — newlines that would forge a second log entry, ANSI escapes
that would rewrite a terminal, NULs that would truncate a consumer — are collapsed
to spaces, and the detail is capped at 200 characters. Classification still reads
the raw text, so a control character cannot hide the word it sits inside.
Sanitising neutralises *structure*, not content: ordinary wording such as
`401 insufficient permissions` passes through unchanged, because naming the
refused capability is the whole reason the detail is kept.

### Changing the permission set requires an explicit re-pair

The TV grants a key against the permission manifest presented at pairing time.
Adding a permission later **does not widen an existing grant** — the key keeps the
capabilities it was issued with until a human re-pairs.

The server records which permission set a key was granted under (a short digest,
not a secret) alongside the key. When a command is denied and the stored grant
predates the current manifest, the error says so and names the explicit action.

**Nothing re-pairs automatically, and nothing clears a working key.** Pairing needs
a person at the TV, so a background re-pair would either fail or condition someone
to approve prompts they did not ask for — and clearing a working key to force the
issue would break every command the old grant still covers. To pick up added
permissions, run `webos-mcp pair` (or call `pair_device` with `force=true` where it
is enabled) and accept the prompt.

A key stored by a version before this tracking existed has no recorded set, and is
treated as predating the current one — which is exactly right, because it does.

The opt-in pairing tool adds four more, each distinguishable:

| Code | Meaning |
|---|---|
| `PAIRING_DISABLED` | The pairing tool is not enabled. Normally you will not see this: when disabled the tool is not registered at all, so a call fails as an unknown tool. It exists as a second, independent refusal in case the tool is ever registered without the flag. |
| `PAIRING_DENIED` | A human actively declined the prompt on the TV. |
| `PAIRING_TIMEOUT` | Nobody answered the prompt in time. |
| `KEY_STORAGE_READONLY` | No durable writable key location is configured, or the write could not be made durable. |

The pairing check runs **before** any network contact, so "you haven't paired"
can never be masked by the TV happening to be off.

---

## Hard boundaries

Deliberately absent, and not open to reconsideration as features:

- **No raw or generic SSAP command tool.** The set of SSAP endpoints this
  server will call is closed and compiled in. An MCP client cannot supply a
  URI. This is the core safety boundary: without it, every other validation
  guarantee is meaningless.
- **No screenshot or frame capture of any kind.**
- **No hidden, service-menu or factory commands.**
- **No multi-device orchestration.** One configured TV per instance.
- **The client key is never returned by a tool, exposed in a log line, or
  written into an exception message** — callers get a storage location only.
- **Pairing is off by default and can never happen unattended.** The
  `pair_device` tool is not registered unless explicitly enabled, and even then
  a human must accept the prompt on the TV. This is the one boundary that is
  configurable rather than absolute; every other item on this list is fixed.

---

## Limitations

### HTTP transport security posture

The HTTP transport can perform state-changing actions on the TV over the
network, so it does not inherit stdio's "trust the local process" assumption.

- **Default bind is loopback-only** (`127.0.0.1`).
- **Binding to any non-loopback address requires an auth token.** Without one
  the server **fails to start** with an explicit error rather than serving
  unauthenticated control to the network.
- When a token is configured, **every** request without a valid
  `Authorization: Bearer` header is rejected with `401` before any tool logic
  runs. There is no unauthenticated path.
- **Network-level protection beyond that token is your responsibility.** The
  token is a single shared bearer credential over plain HTTP — put the server
  behind a firewall, on a trusted container network, or behind a TLS-terminating
  reverse proxy. Do not expose it to an untrusted network on the strength of
  the token alone.

### Wake-on-LAN in bridge mode

WOL magic packets are normally sent as a LAN broadcast, and a broadcast does
**not** reliably cross Docker's default bridge network out to the physical LAN.

- **Supported path: `network_mode: host`**, as in the compose example. The
  broadcast then leaves exactly as it would from a bare-metal process.
- **Bridge-mode fallback:** the server additionally sends a **directed unicast**
  magic packet to the TV's last-known IP address. Many consumer routers and
  switches forward a unicast magic packet to a sleeping NIC even when they
  will not forward a NAT'd broadcast — but **this is best-effort and
  hardware-dependent. It is not guaranteed to work on your equipment.**

Both legs are always attempted, and `tv_power_on` reports exactly which
targets were written to.

### Pairing over MCP widens the trust boundary

Disabled by default, and when enabled it still cannot pair unattended. But a
client that can reach `pair_device` can raise a pairing prompt on the TV, which
is a real (if minor) change in what a compromised or careless client can do. It
cannot complete the pairing, and it cannot read the key. Leave it off unless
something actually needs it, and keep the HTTP bearer token tight when it is on.

### Verification, not optimism

This applies to content launching too, and it was learned the hard way: the
first implementation treated SSAP launcher acceptance as playback, and physical
testing found it reporting success with the TV still on its home screen.
`tv_youtube_play` now confirms the foreground app before succeeding.

`tv_power_on` never reports success merely because a packet was sent. It polls
for an Active state and returns `"verified": true` only if it observed one;
otherwise it returns `"verified": false` with an explicit `UNVERIFIED`
explanation. A sent packet is not a woken TV.

### Other

- **Single TV per instance.** Run more instances for more TVs.
- **Duplicate/overlapping callers are serialized**, not rejected. Two
  concurrent button sequences resolve one after the other rather than
  interleaving on the wire.
- **Discovery needs SSDP multicast**, which many VPNs and some managed
  switches drop. Configure `WEBOSMCP__HOST` directly if `discover` finds
  nothing.

---

## Compatibility statement

Only behaviour built on stable, documented **SSAP**, **Wake-on-LAN** and
**DIAL** calls is claimed as supported. Capability varies by model, firmware and current input —
the server reports `TV_UNSUPPORTED_CAPABILITY` rather than guessing or
silently doing nothing.

Specifically **not** claimed:

- That the bridge-mode unicast WOL fallback works on your router or switch.
  It is explicitly best-effort.
- That channel tools work on every model or input. They need tuner support.
- That `tv_screen_off` / `tv_screen_on` exist on every model.
- That every TV exposes a DIAL endpoint. Where it does not, `tv_youtube_play`
  reports `TV_UNSUPPORTED_CAPABILITY` rather than falling back to something
  unverifiable.
- That YouTube control works without internet access. It uses Google's Lounge
  service; the rest of the server does not.
- That the Lounge protocol is stable. It is undocumented and can change without
  notice. When it does, YouTube tools fail loudly rather than silently degrading.
- That a receiver always accepts remote control. Where it advertises no screen id
  or refuses the session, YouTube tools return `TV_UNSUPPORTED_CAPABILITY`.
- That `observed: false` commands took effect. They were accepted; nothing more
  is claimed.
- That a MAC address can always be derived. It comes from the OS neighbour table,
  which a minimal container may not expose; registration succeeds without it and
  `tv_power_on` stays unavailable until one is supplied.
- That the built-in `WEBOSMCP__DIALPORTS` list covers every model. `2038` is
  what LG webOS was observed using, not a value from a specification. If your
  TV advertises DIAL somewhere else, set `WEBOSMCP__DIALAPPLICATIONURL` or add
  the port — the unicast SSDP step is there to find it without you having to.
- That text entry reaches every app. Apps with custom on-screen keyboards
  ignore standard SSAP text entry, and `tv_type_text` refuses for those rather
  than reporting a no-op as success.

The automated suite runs entirely against fakes, so it verifies this server's
logic — mapping, validation, error selection, serialization, reconnect,
timeouts — and not the behaviour of any particular TV. Physical-device
verification is tracked separately.

---

## Troubleshooting

**Every tool returns `PAIRING_REQUIRED`.**
No usable client key. Run `webos-mcp status` to see where the server is looking
for one, then `webos-mcp pair`. If you set `WEBOSMCP__CLIENTKEYFILE`, confirm
the file exists and is non-empty inside the container.

**Every tool returns `TV_OFF`, but the TV is clearly on.**
The SSAP port is probably wrong or network control is disabled. Check
`WEBOSMCP__PORT` (`3000` plaintext / `3001` with `WEBOSMCP__USETLS=true`) and
re-enable "LG Connect Apps" on the TV.

**`discover` finds nothing.**
SSDP multicast is being dropped, or the server is on a different segment.
Set `WEBOSMCP__HOST` directly — discovery is a convenience, not a requirement.

**`tv_power_on` returns `"verified": false`.**
The packet went out but the TV never came up. In order of likelihood: WOL is
disabled on the TV; you are in bridge mode and neither WOL leg reached it (use
`network_mode: host`); or `WEBOSMCP__BROADCASTADDRESS` is not your subnet's
broadcast address. The response lists the targets actually written to.

**`tv_youtube_play` returns `TV_UNSUPPORTED_CAPABILITY` ("no DIAL endpoint")
but the TV does support YouTube.**
The DIAL endpoint was not found. In a container this is almost always because
SSDP multicast does not cross the bridge network — which is why the TV is also
probed directly and searched by unicast. Check `WEBOSMCP__HOST` is set and
correct, since every non-multicast strategy depends on it. Then run an SSDP
`M-SEARCH` from a host on the same LAN, read the `LOCATION` header of the
reply, and either add that port to `WEBOSMCP__DIALPORTS` or set
`WEBOSMCP__DIALAPPLICATIONURL` to it directly. The server logs which ports it
probed when resolution fails.

**`tv_youtube_play` says the receiver advertises no screen id.**
DIAL found YouTube but the status document carries no `screenId`, so there is no
Lounge session to open. Nothing can be loaded or verified on this firmware.

**YouTube tools fail while everything else works.**
Check outbound access to `youtube.com`. YouTube control is the only part of this
server that leaves the LAN, so a network that blocks egress breaks exactly these
tools and nothing else.

**`tv_youtube_play` fails naming HTTP 403.**
The DIAL endpoint was found, and the TV refused the request. DIAL application
endpoints are origin-checked: the server sends `Origin: https://www.youtube.com`
on the YouTube status and launch calls for exactly this reason. A 403 that
survives that is an authorisation refusal from the TV — it does **not** mean
YouTube is missing, and the server no longer reports it as such. Check that
"LG Connect Apps" / network control is still enabled and that the TV is not in
a restricted or store-demo mode.

**The server refuses to start with "Refusing to start".**
You set a non-loopback `WEBOS_MCP_HTTP_BIND` without a token. Set
`WEBOS_MCP_HTTP_TOKEN` / `WEBOS_MCP_HTTP_TOKEN_FILE`, or bind to `127.0.0.1`.
This is intentional.

**HTTP requests return `401`.**
Send `Authorization: Bearer <token>`. If you use `WEBOS_MCP_HTTP_TOKEN_FILE`,
note it takes precedence over the inline variable — a stale file wins over a
freshly-exported env var.

**Remote buttons and pointer moves do nothing.**
Those go over a separate pointer input socket negotiated with the TV. If it
cannot be opened the server returns `TV_UNSUPPORTED_CAPABILITY`. A power cycle
of the TV usually clears it.

**The TV slept and now calls fail.**
They shouldn't — a dropped socket is detected and reconnected once,
transparently, without a process restart. If it persists, the TV is likely in
a standby state that stops serving SSAP entirely; that is `TV_OFF`.

---

## Development

```bash
dotnet build
dotnet test
docker build -t webos-mcp:local .
```

```
src/WebosMcp.Domain/          error contract, enums, models — no dependencies
src/WebosMcp.Application/     tool layer, session, validation, abstractions
src/WebosMcp.Infrastructure/  SSAP WebSocket, Wake-on-LAN, key store, discovery
src/WebosMcp.Server/          MCP tools, both transports, operator CLI
tests/WebosMcp.Tests/         the full suite — no physical TV required
```

Every network boundary — SSAP, WOL, discovery, delays, the key store — sits
behind an interface, which is what lets the whole suite run in CI. CI builds,
runs the tests, and builds the container image on every pull request.

Contributions are welcome, with one standing exception: pull requests adding a
raw command passthrough, a screenshot tool, hidden/service-menu commands, or
multi-device orchestration will be declined. Those are
[deliberate boundaries](#hard-boundaries), not gaps.

---

## License

MIT. See [LICENSE](LICENSE).
