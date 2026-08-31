# Errors, limits and troubleshooting

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

### Re-pairing after permission changes

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

The current permission manifest includes `READ_RUNNING_APPS`, used by reads such
as `tv_get_foreground_app`. A key created by an earlier release may keep returning
`TV_PERMISSION_DENIED` for that tool until a human re-pairs once; other granted
capabilities continue working meanwhile.

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
- **No screen recording, polling or repeated capture.** Frame capture exists as
  exactly one read-only, on-demand tool ([`tv_take_screenshot`](tools.md#screenshot)) and
  will not grow into a capture loop, a scheduled grab, or OCR/analysis of the
  captured frame. The tool takes no arguments at all, so nothing a caller supplies
  can influence what is requested — which is the same boundary as the closed SSAP
  list, applied to the one endpoint that returns a URI.
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

The canonical bind, token and remote-exposure rules live in
[configuration](configuration.md#streamable-http). The two user-visible failure
modes — a refused unsafe startup and `401` for a missing or stale token — are
covered in the troubleshooting entries below.

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

Supported and physically tested: **LG TVs running webOS**. Televisions from other
brands using webOS or webOS Hub are untested and best-effort only; no compatibility
claim is made for them.

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

## Related documentation

- [Setup and pairing](setup.md)
- [Configuration and transport security](configuration.md)
- [Complete tool surface](tools.md)
