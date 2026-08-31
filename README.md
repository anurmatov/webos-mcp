# webos-mcp

A typed [MCP](https://modelcontextprotocol.io) server for controlling a single
LG webOS TV over the local network — power, apps, browser, YouTube, inputs,
media, navigation and notifications.

Built on .NET 10. One shared tool layer is served over **stdio** (local MCP
clients) and **Streamable HTTP** (container / network use), backed by three
protocols: the LG **SSAP** WebSocket for control, **Wake-on-LAN** for power-on,
and **DIAL** for launching YouTube in a way that can actually be verified.

- **Local-first.** Talks to the TV on your LAN. No cloud service, no account.
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
| `tv_get_device_info` | Model, firmware and product information. |
| `tv_get_foreground_app` | The app currently in the foreground. |
| `tv_list_apps` | Installed apps with their launch ids. |
| `tv_get_status` | Combined snapshot: power, foreground app and volume state. |

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
| `tv_youtube_play` | Play a video by id, `youtu.be` link or watch URL. Launches over **DIAL**, cold-starting YouTube if it is already running, and confirms the app reached the foreground before reporting success. |
| `tv_youtube_search` | **Not supported** — always returns `TV_UNSUPPORTED_CAPABILITY`. See below. |

Content tools report which path ran — `"path": "DeepLink"` or `"path": "Dial"`.

**`tv_youtube_play` never reports success on an accepted launch alone.** After
the DIAL launch it polls the TV's own foreground-app report and only succeeds
once YouTube actually appears. A launch the TV accepted but never acted on is
reported as a failure, naming the app that was actually in the foreground.

**A running YouTube session is stopped and cold-started.** A DIAL launch aimed
at an app that is *already running* does not change what it is playing — the TV
accepts the request and the previous video keeps going. Physical testing hit
exactly that, and because YouTube was already in the foreground the old
foreground check passed instantly and it was reported as success. So when
YouTube is running, the tool now stops it over DIAL, waits for it to actually
stop, and starts it cold with the requested video. Where the TV does not permit
that — no `allowStop`, no instance link, or a stop that is accepted but never
takes effect — the tool returns `TV_UNSUPPORTED_CAPABILITY` and does **not**
launch. Expect the screen to go briefly to the home screen and back.

**`exactVideoConfirmed` is always `false` on the DIAL path, by design.** DIAL
exposes no way to read back which video is on screen. The video id is delivered
to a freshly started app, which is what makes it take effect, but that is not
the same as observing it play — so the response says so rather than letting a
bare "success" imply proof. `coldStarted` reports whether a running session had
to be restarted.

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

Plus `INVALID_INPUT` (rejected before any connection is opened), `TIMEOUT`
and `TV_ERROR`.

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
- That the video reported as launched is the video on screen. DIAL cannot read
  back the playing video; `exactVideoConfirmed` is always `false` on this path
  and is not a claim we make. Confirming exactness would need the YouTube Lounge
  API, which pairs through Google's cloud and is deliberately out of scope for a
  local-first server — see the rejected alternatives in issue #1.
- That a running YouTube session can always be stopped. `allowStop` is a
  per-app, per-firmware option; without it the requested video cannot be made to
  replace what is playing, and the tool says so instead of launching anyway.
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

**`tv_youtube_play` returns `TV_UNSUPPORTED_CAPABILITY` saying YouTube is
already running and cannot be stopped.**
The TV does not advertise `allowStop` for YouTube, or exposes no running-instance
link. A DIAL launch cannot change the video of a running session, so honouring
the request is impossible on this firmware. Stop YouTube on the TV (back out to
the home screen) and call the tool again — from a stopped app the launch payload
takes effect normally.

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
