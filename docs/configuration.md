# Configuration reference

All runtime configuration comes from environment variables. `WebosMcpOptions`
uses the standard .NET double-underscore separator; HTTP transport settings use
their documented flat names.

## TV connection and device state

| Variable | Default | Description |
|---|---|---|
| `WEBOSMCP__HOST` | none | TV hostname or IP. When set, overrides the stored active device. |
| `WEBOSMCP__PORT` | `3000` | SSAP port; `3000` plaintext or commonly `3001` with TLS. |
| `WEBOSMCP__USETLS` | `false` | Use `wss://`; normally paired with port `3001`. |
| `WEBOSMCP__MACADDRESS` | none | TV MAC required by `tv_power_on`. Overrides the stored value. |
| `WEBOSMCP__BROADCASTADDRESS` | `255.255.255.255` | WOL target. Prefer the LAN subnet broadcast. |
| `WEBOSMCP__DEVICESTOREPATH` | `~/.webos-mcp/devices.json` | Writable JSON device book used by typed registration. |

The Docker example sets the device store to
`/var/lib/webos-mcp/devices.json`, inside its durable named volume. The book is
not a secret, but it must remain writable for register, update, select and
remove operations.

## Pairing and client key

| Variable | Default | Description |
|---|---|---|
| `WEBOSMCP__CLIENTKEY` | none | Client key supplied inline. Avoid where a secret file is available. |
| `WEBOSMCP__CLIENTKEYFILE` | none | Read-only mounted file containing a client key. |
| `WEBOSMCP__CLIENTKEYPATH` | `~/.webos-mcp/clientkey.json` | Durable writable location where pairing persists a key. |
| `WEBOSMCP__ENABLEPAIRINGTOOL` | `false` | Register the opt-in `pair_device` MCP tool. |
| `WEBOSMCP__PAIRINGTIMEOUTSECONDS` | `60` | Time allowed for a human to approve the TV prompt. |

Reading resolves in the order `CLIENTKEY`, `CLIENTKEYFILE`, then
`CLIENTKEYPATH`. Writing is separate: pairing writes to `CLIENTKEYPATH` only.
An inline key or mounted secret remains operator-owned and is never overwritten.

The Docker example stores the writable key at
`/var/lib/webos-mcp/clientkey.json`. The key grants full TV control: never
commit, print or expose it to a caller.

## DIAL and YouTube Lounge

`tv_youtube_play` locates the TV's DIAL application endpoint in this order:

1. Use `WEBOSMCP__DIALAPPLICATIONURL` exactly when configured.
2. Probe `WEBOSMCP__HOST` directly on every configured DIAL port.
3. Send unicast SSDP directly to the TV.
4. Fall back to multicast SSDP.

The first three paths do not depend on multicast, which is useful inside a
container.

| Variable | Default | Description |
|---|---|---|
| `WEBOSMCP__DIALPORTS` | `2038,1754,3000,8080,9080` | Ordered, comma-separated direct-probe ports. Invalid ports fail loudly. |
| `WEBOSMCP__DIALAPPLICATIONURL` | none | Absolute `http` or `https` applications URL that bypasses discovery. |
| `WEBOSMCP__DIALSSDPTIMEOUTSECONDS` | `3` | Duration of each SSDP search window. |
| `WEBOSMCP__LOUNGEBASEURL` | `https://www.youtube.com` | YouTube Lounge service; the only outbound-internet dependency. |
| `WEBOSMCP__LOUNGEDEVICENAME` | `webos-mcp` | Name presented by this remote to the receiver. |
| `WEBOSMCP__LOUNGEVERIFYTIMEOUTSECONDS` | `30` | Wait for the receiver to confirm a command. |
| `WEBOSMCP__LOUNGESUBSCRIBETIMEOUTSECONDS` | `10` | Wait for the receiver event stream to be actively read before sending. |

If no DIAL endpoint can be found, set `DIALAPPLICATIONURL` from the
`LOCATION` returned by an SSDP search performed on the same LAN. A TV with no
DIAL endpoint gets `TV_UNSUPPORTED_CAPABILITY`, not an unverified fallback.

YouTube playback and state read-back use Google's undocumented Lounge service.
All non-YouTube tools remain LAN-only.

The Lounge token is a form field during bind, a header on commands and a query
parameter on the receiver event subscription because that is the verified
request shape. HTTP client logging categories are raised to Warning so the
event-stream URI and token never enter normal logs; warnings and errors remain.

## Operation timeouts

| Variable | Default | Description |
|---|---|---|
| `WEBOSMCP__CONNECTTIMEOUTSECONDS` | `10` | SSAP connection timeout. |
| `WEBOSMCP__REQUESTTIMEOUTSECONDS` | `15` | General per-operation timeout. |
| `WEBOSMCP__LAUNCHVERIFYTIMEOUTSECONDS` | `20` | Wait for a launched app to reach the foreground. |
| `WEBOSMCP__LAUNCHPOLLINTERVALSECONDS` | `2` | Poll interval while verifying app launch. |
| `WEBOSMCP__POWERONVERIFYTIMEOUTSECONDS` | `60` | Wait for `tv_power_on` to observe Active state. |
| `WEBOSMCP__POWERONPOLLINTERVALSECONDS` | `3` | Power-on verification poll interval. |
| `WEBOSMCP__FALLBACKSTEPDELAYMILLISECONDS` | `400` | Delay between bounded fallback input steps. |

Sending a packet or accepting an app-launch command is not success by itself.
Power and launch operations use these budgets to verify observable state.

## Screenshot limits

| Variable | Default | Accepted range | Description |
|---|---:|---:|---|
| `WEBOSMCP__SCREENSHOTTIMEOUTSECONDS` | `15` | `1`–`300` | Timeout for the frame download after SSAP returns its URI. |
| `WEBOSMCP__SCREENSHOTMAXBYTES` | `8388608` | `1024`–`67108864` | Streamed maximum body size before abort. |

Both values are validated at startup. An out-of-range value stops the server
and names the offending setting; values are never silently clamped. The download
uses its own timeout and HTTP handler so it cannot hold the SSAP session or relax
TLS rules for another client.

See [screenshot handling](tools.md#screenshot) for URI, redirect and image-body
validation.

## Streamable HTTP

| Variable | Default | Description |
|---|---|---|
| `WEBOS_MCP_HTTP_BIND` | `127.0.0.1` | Listen address. Every non-loopback value requires a token. |
| `WEBOS_MCP_HTTP_PORT` | `8765` | Listen port. |
| `WEBOS_MCP_HTTP_TOKEN` | none | Bearer token supplied inline. |
| `WEBOS_MCP_HTTP_TOKEN_FILE` | none | Mounted token file; takes precedence over the inline value. |

The token is never accepted as a command-line argument because process command
lines are visible to other software on the host.

### Security posture

- Loopback is the default and requires no token.
- A non-loopback bind without a token makes the process refuse to start.
- When a token exists, every request is authenticated before MCP tool logic.
- The token is one shared bearer credential over plain HTTP. Put any remote
  exposure behind a trusted network, firewall and TLS-terminating proxy.
- Do not expose the service to an untrusted network on the strength of the token
  alone.

The maintained Docker example uses a mounted token file, host networking and a
loopback bind. It intentionally declares no `ports:` mapping.

## Wake-on-LAN networking

Wake-on-LAN normally uses a subnet broadcast. Docker bridge NAT does not
reliably forward that broadcast to the physical LAN, so `network_mode: host` is
the supported container path.

The server also sends a directed unicast magic packet to the last known TV
address. That fallback is best-effort and depends on the router, switch and TV;
it is not a compatibility guarantee. `tv_power_on` reports both targets and
verifies whether the TV actually reached Active state.

## Full example

Use [docker-compose.example.yml](../docker-compose.example.yml) as the canonical
container example. It contains only documentation-safe placeholder addresses and
an operator-created secret file; no credential is baked into the image.
