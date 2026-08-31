# webos-mcp

A typed [Model Context Protocol](https://modelcontextprotocol.io) server for
controlling an LG TV over the local network. It exposes power, apps, inputs,
audio, navigation, notifications, YouTube control, status and an explicit
on-demand screenshot tool over Streamable HTTP or stdio.

**Compatibility:** supported and physically tested on **LG TVs running webOS**.
Televisions from other brands using webOS or webOS Hub are untested and may work
only on a best-effort basis; this project makes no compatibility claim for them.

The primary path is Docker. The image runs as a non-root user, keeps device and
pairing state in a durable volume, binds HTTP to loopback by default, and refuses
any non-loopback bind without bearer authentication.

## What it can do

| Area | Examples |
|---|---|
| Observe | Power, volume, foreground app, device info, apps, inputs and channels |
| Control | Power, screen, volume, media, apps, browser, inputs and remote buttons |
| YouTube | Play, pause, seek, queue, receiver state, autoplay and playback speed |
| Setup | Discover, register, select, update and remove a TV through typed tools |
| Capture | One read-only screenshot, only after an explicit request for it |

There are **51 tools by default**. An opt-in, human-approved `pair_device` tool
is the 52nd. See the complete [tool reference](docs/tools.md).

## Safety boundaries

- There is **no raw SSAP passthrough**. Every endpoint and input is compiled,
  typed and validated.
- Pairing always requires a person to accept the prompt on the TV. The MCP
  pairing tool is absent unless explicitly enabled.
- State-changing calls must follow an explicit user request. Do not infer that a
  user wants power, volume, input, playback or on-screen state changed.
- `tv_take_screenshot` is the only capture path. It is read-only, on-demand and
  must never be called proactively, repeatedly, on a schedule or in the
  background.
- No hidden/service-menu commands, screen recording or multi-TV orchestration.
- Client keys are never returned by a tool or written to logs.

## Docker quick start

### Prerequisites

- Docker with Compose support
- `curl` for the protocol checks below
- OpenSSL to create the local bearer-token file
- An LG TV running webOS on the same LAN, with network control enabled
- The TV's IP address; its MAC address is needed only for Wake-on-LAN

The documentation uses RFC 5737 placeholders such as `192.0.2.10` and
`00:11:22:33:44:55`. Replace them with values from your own LAN.

### 1. Build and start

```bash
git clone https://github.com/anurmatov/webos-mcp.git
cd webos-mcp

cp docker-compose.example.yml docker-compose.yml
mkdir -p secrets
openssl rand -hex 32 > secrets/http_token
chmod 600 secrets/http_token

docker compose build
docker compose up -d
```

The example uses `network_mode: host`, which is the supported Docker path for
Wake-on-LAN broadcasts. It publishes no port mapping: the process itself listens
on `127.0.0.1:8765` and requires the token mounted from
`secrets/http_token`.

The named `webos_keys` volume is mounted at `/var/lib/webos-mcp`. It stores both
`devices.json` and `clientkey.json`, so typed registration and pairing survive
container replacement.

### 2. Verify MCP initialize and tool discovery

```bash
MCP_URL=http://127.0.0.1:8765/
TOKEN=$(cat secrets/http_token)

curl -sS -N "$MCP_URL" \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  --data '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"readme-smoke","version":"1.0"}}}'

curl -sS -N "$MCP_URL" \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  --data '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
```

The first response identifies `webos-mcp`; the second lists 51 tools. Pairing is
off by default, so `pair_device` is intentionally absent.

### 3. Register the TV with a typed tool

Keep the TV powered on for registration, then replace the example address:

```bash
curl -sS -N "$MCP_URL" \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  --data '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"tv_register_device","arguments":{"host":"192.0.2.10","name":"Living room"}}}'
```

Registration makes the TV active and stores it in the durable device book. A
container may be unable to derive its MAC address; that affects only
`tv_power_on`. Use `tv_update_device` later to supply the MAC if needed.

### 4. Pair with human approval

Run the operator pairing command against the same address and accept the prompt
that appears on the TV:

```bash
docker compose run --rm \
  -e WEBOSMCP__HOST=192.0.2.10 \
  webos-mcp pair
```

The one-off host override tells the pairing process which registered TV to use;
the key is written to the shared `/var/lib/webos-mcp/clientkey.json` volume. The
key grants control of the TV, so do not print, copy or commit it.

### 5. Make a read-only smoke call

```bash
curl -sS -N "$MCP_URL" \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  --data '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"tv_get_power_state","arguments":{}}}'
```

A paired, reachable TV returns an `ok: true` result with its power state.

### 6. State changes require an explicit request

Only after a user has explicitly asked for this exact change, a client can call
a state-changing tool such as:

```json
{
  "jsonrpc": "2.0",
  "id": 5,
  "method": "tools/call",
  "params": {
    "name": "tv_set_volume",
    "arguments": { "volume": 12 }
  }
}
```

Send it with the same URL and headers used above. Tool results use a consistent
`ok/result` or `ok/error` envelope; see [tools](docs/tools.md) and
[troubleshooting](docs/troubleshooting.md).

## Connect an MCP client

Configure a Streamable HTTP MCP server at `http://127.0.0.1:8765/` and send:

```text
Authorization: Bearer <contents of secrets/http_token>
```

Keep the endpoint on loopback unless you intentionally add network isolation
and TLS termination. A non-loopback bind also requires a token or the server
refuses to start. Full transport settings are in
[configuration](docs/configuration.md).

## Stdio, secondary path

Stdio is available for a local MCP client that starts the process itself:

```bash
dotnet build -c Release

WEBOSMCP__HOST=192.0.2.10 \
WEBOSMCP__MACADDRESS=00:11:22:33:44:55 \
dotnet run --project src/WebosMcp.Server -- pair
```

Then configure the client to run:

```text
dotnet /path/to/webos-mcp.dll stdio
```

`stdio` is the default command. Stdout is reserved for MCP; logs go to stderr.
See [setup](docs/setup.md) for a complete client example and persistence notes.

## Documentation

- [Setup and pairing](docs/setup.md) — Docker, stdio, registration, pairing and persistence
- [Tool reference](docs/tools.md) — all tools, return semantics and screenshot handling
- [Configuration](docs/configuration.md) — environment variables, timeouts and HTTP security
- [Troubleshooting](docs/troubleshooting.md) — error codes, re-pairing, WOL and platform limits
- [Development](docs/development.md) — build, tests, architecture and verification scope

## License

MIT. See [LICENSE](LICENSE).
