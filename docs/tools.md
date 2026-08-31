# Tool reference

**52 tools**, counted at this commit: 51 registered by default, plus one opt-in
pairing tool that is **not registered by default**.

Every tool returns `{ "ok": true, "result": … }` or
`{ "ok": false, "error": { "code", "message" } }`. The one exception is
[`tv_take_screenshot`](#screenshot), whose success is a native MCP **image**
content block — the envelope is text and an image is not. Its failures use the
same envelope as everything else.

## Device setup

| Tool | Description |
|---|---|
| `tv_discover_devices` | Scan the LAN by SSDP, or probe one supplied address directly. Registers nothing. |
| `tv_register_device` | Register an address and make it active. Derives MAC and broadcast where possible; re-registering updates the existing record. |
| `tv_list_devices` | Registered TVs and which selection is active. |
| `tv_select_device` | Make a registered TV active immediately. |
| `tv_update_device` | Override a MAC address, broadcast address or label. |
| `tv_remove_device` | Forget a TV. Removing the active one promotes another; the pairing key is not deleted. |

The device book and typed setup flow are documented in
[setup](setup.md#typed-device-registration).

## Status

| Tool | Description |
|---|---|
| `tv_get_power_state` | Current power state: `Active`, `ScreenOff`, `Standby`, `Unreachable`, `Unknown`. |
| `tv_get_device_info` | Model, firmware and product information. **Partial-result safe** — see below. |
| `tv_get_foreground_app` | The app currently in the foreground. |
| `tv_list_apps` | Installed apps with their launch ids. |
| `tv_get_status` | Combined snapshot: power, foreground app and volume state. **Partial-result safe** — see below. |

### Partial results, and where they stop

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

## Screenshot

| Tool | Description |
|---|---|
| `tv_take_screenshot` | Capture the frame currently on screen and return it as an image. Read-only, no arguments. |

**Sensitive by nature.** A capture shows whatever the household is watching. The
tool description tells a model to invoke it **only in direct response to an
explicit request from the user right now** — never proactively, on a schedule, in
a loop, or in the background. That is a stated contract, not an enforced one: the
server has no caller-identity mechanism, and deliberately does not grow one to
fake enforcement here.

**A black image is a successful capture.** The screen may genuinely be black, or
the content may be DRM-protected and capture as black. Neither is distinguishable
from the outside, so the server does not inspect the frame and reports neither as
an error.

**Model- and firmware-dependent.** The capture uses `ssap://tv/executeOneShot`,
which LG does not document or guarantee. Sets whose firmware lacks it return
`TV_UNSUPPORTED_CAPABILITY`.

How the frame is handled, and why each rule is there:

- **In memory only, for the duration of the request.** Never written to the
  device store, a temp file, a cache, a log line or any telemetry. Nothing is
  logged about a capture beyond its size and detected format.
- **The announced URI is untrusted.** The TV answers with an `imageUri` that the
  server then fetches, which makes the TV an input rather than an authority. The
  **full** rule set — `http`/`https` only, no userinfo, length capped, host pinned
  to the selected TV — is applied to that URI *and independently to every redirect
  target*. A redirect is simply a second URI from the same untrusted source, so
  checking only the host on later hops would leave a `file://` target or embedded
  credentials reachable one redirect away from a URI that passed every check. Any
  violation is `INVALID_INPUT`, refused *before* the request goes out.
- **Bounded.** Its own timeout and a streamed maximum body size, both
  range-checked at startup; an oversized body is aborted mid-read, never buffered.
- **Validated as a structurally well-formed image by its bytes** — not by the
  `Content-Type` header, not by a leading magic number, and not by a signature plus
  a terminator. That last one is the subtle case: a body can begin with SOI, end
  with EOI, and be corrupt in between, so bracketing bytes prove nothing about what
  is between them. Every JPEG marker segment must declare a length that fits, with
  a frame header and a scan present and the scan running to EOI; every PNG chunk
  must declare a length that fits **and** a CRC32 that matches its own contents,
  ending at IEND with nothing after it. Empty, truncated, corrupt, oversized, HTML
  or otherwise non-image bodies are all `TV_ERROR`.

  It is deliberately not a pixel decode: no dependency is worth that here, and a
  decoder is a large attack surface to aim at untrusted bytes. The one gap is
  stated rather than hidden — corruption *inside* a JPEG's entropy-coded scan is
  invisible to any structural check, because that region is arbitrary bytes by
  definition. PNG has no such gap, since every byte of it sits inside a
  CRC-covered chunk.
- **JPEG and PNG only.** WebP is deliberately not supported: a RIFF length field
  can be made self-consistent over arbitrary content, so it cannot be validated to
  the standard the other two are held to, and the verified capture returns JPEG. A
  TV answering with WebP gets an honest `TV_ERROR` rather than a capture checked to
  a lower bar.
- **TLS validation is never globally disabled.** A self-signed certificate is
  tolerated only for the selected TV's own host, on this download's own HTTP
  handler, and nowhere else in the process.

## Power and display

| Tool | Description |
|---|---|
| `tv_power_on` | Wake via WOL **and verify** the TV reaches an Active state. Idempotent. |
| `tv_power_off` | Graceful power off (standby). |
| `tv_screen_off` | Panel off without powering down, where supported. |
| `tv_screen_on` | Panel back on. |

## Audio and media

| Tool | Description |
|---|---|
| `tv_get_volume` | Volume, mute state and active sound output. |
| `tv_set_volume` | Set volume; validated to 0–100. |
| `tv_set_mute` | Mute or unmute. |
| `tv_list_sound_outputs` | Sound outputs the TV reports. |
| `tv_set_sound_output` | Switch output; validated against the reported list. |
| `tv_media_control` | `Play`, `Pause`, `Stop`, `Rewind`, `FastForward`. |

## Apps and content

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

## YouTube control, and the one cloud dependency

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

## Navigation and input

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

## TV and inputs

| Tool | Description |
|---|---|
| `tv_list_inputs` | External inputs with their connected state. |
| `tv_switch_input` | Switch input; validated against the reported list. |
| `tv_get_current_channel` | Current channel and programme. |
| `tv_channel_up` / `tv_channel_down` | Step channels. |
| `tv_tune_channel` | Tune to `7` or `7-1`. |

Channel tools return `TV_UNSUPPORTED_CAPABILITY` on inputs and models with no
tuner information.

## Notifications

| Tool | Description |
|---|---|
| `tv_show_toast` | On-screen toast, up to 512 characters. |

## Pairing (opt-in — absent unless enabled)

| Tool | Description |
|---|---|
| `pair_device` | Pair with the TV. Requires a human to accept the on-screen prompt. Persists the key atomically and verifies it on disk before reporting success. Never returns the key. |

Not registered unless `WEBOSMCP__ENABLEPAIRINGTOOL=true`. On a default
deployment it does not appear in `tools/list` and cannot be called.
See [pairing through MCP](setup.md#pairing-through-mcp-opt-in).

---

## Related documentation

- [Docker, registration and pairing](setup.md)
- [Configuration and limits](configuration.md)
- [Error codes and platform limits](troubleshooting.md)
