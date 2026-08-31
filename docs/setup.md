# Setup and pairing

The root [README](../README.md) is the tested Docker-first path from a clean
checkout to MCP initialize, tool discovery, typed registration, human-approved
pairing and a read-only call. This document covers the alternatives and the
details behind that flow.

## Requirements

- An **LG TV running webOS** with SSAP network control enabled. Broadly, LG
  models from webOS 3.0 onward expose SSAP on port `3000` or TLS port `3001`.
  The setting is commonly named "LG Connect Apps" under Network, but names vary
  by firmware.
- The TV and server on the same LAN. SSDP discovery and Wake-on-LAN broadcast do
  not cross routed boundaries by default.
- Wake-on-LAN enabled on the TV if `tv_power_on` is needed. The setting is often
  named "Mobile TV On", "Turn on via Wi-Fi" or "Turn on via Ethernet".
- Docker with Compose for the primary path, or the .NET 10 SDK for stdio/source
  use.

Example addresses in these docs (`192.0.2.10`, `192.0.2.255` and
`00:11:22:33:44:55`) are documentation-only placeholders.

## Docker layout

Start from the maintained example:

```bash
cp docker-compose.example.yml docker-compose.yml
mkdir -p secrets
openssl rand -hex 32 > secrets/http_token
chmod 600 secrets/http_token
docker compose build
docker compose up -d
```

The image runs as uid `10001`, uses `/app` as its working directory and starts
`dotnet /app/webos-mcp.dll http`. The example deliberately has:

- `network_mode: host`, so Wake-on-LAN broadcast can leave the host normally;
- no `ports:` mapping;
- HTTP on `127.0.0.1:8765` with a bearer token mounted from a secret file;
- a named volume at `/var/lib/webos-mcp`;
- `WEBOSMCP__DEVICESTOREPATH=/var/lib/webos-mcp/devices.json`;
- `WEBOSMCP__CLIENTKEYPATH=/var/lib/webos-mcp/clientkey.json`.

The device book is not a secret. It contains registered TV addresses and the
active selection. The client key **is** a secret and grants control of the TV.
Both must be durable and writable by the process; neither belongs in Git.

## Typed device registration

The flow starts with `tv_discover_devices` when the address is unknown, then
`tv_register_device` makes one result active and durable. Selection, correction
and removal also remain typed operations; see the complete
[device-tool reference](tools.md#device-setup).

One TV is active at a time and tools never take a device selector. Registration
is setup, not per-call multi-device routing.

In a container, multicast scan discovery may return no results. Pass a known TV
address to `tv_discover_devices`, or call `tv_register_device` directly. Both use
unicast and work across a Docker network boundary.

Registration still succeeds when the container cannot derive a MAC from the
host neighbour table. The response reports `wakeOnLanAvailable: false`; all
control except `tv_power_on` remains available. Add the MAC later with
`tv_update_device`.

An empty multicast discovery result includes a hint explaining the Docker and
LAN-boundary limitation rather than returning an unexplained empty list.

Explicit `WEBOSMCP__HOST`, `WEBOSMCP__MACADDRESS` and
`WEBOSMCP__BROADCASTADDRESS` values override the stored active device. Leave
them unset when using the typed registration flow.

## Pairing from the operator CLI

Pairing requires a person to accept the prompt on the TV:

```bash
docker compose run --rm \
  -e WEBOSMCP__HOST=192.0.2.10 \
  webos-mcp pair
```

The explicit host is for this one-off operator process. The long-running server
already has the active TV from `tv_register_device`, and both containers share
the key volume.

Other operator commands are:

```bash
docker compose run --rm webos-mcp discover
docker compose run --rm \
  -e WEBOSMCP__HOST=192.0.2.10 \
  webos-mcp status
```

`discover` needs SSDP multicast and therefore benefits from the example's host
network. Direct typed registration is more deterministic when the address is
already known.

The pairing key is never printed. The command reports only its storage path and
verifies that the persisted value can be read back. A stored key survives
restarts. If a factory reset or TV-side revocation invalidates it, calls return
`PAIRING_REQUIRED`; pair again explicitly.

## Pairing through MCP (opt-in)

The default deployment has no pairing tool: `pair_device` is not registered and
does not appear in `tools/list`. Most deployments should pair once through the
operator CLI and leave it disabled.

For a deployment without shell access, opt in deliberately:

```yaml
environment:
  WEBOSMCP__ENABLEPAIRINGTOOL: "true"
  WEBOSMCP__PAIRINGTIMEOUTSECONDS: "60"
  WEBOSMCP__CLIENTKEYPATH: "/var/lib/webos-mcp/clientkey.json"
```

Then recreate the container and call `pair_device`. Its guarantees are:

- a human must still approve the on-screen prompt;
- the client key is never returned or logged;
- success is reported only after an atomic write and disk read-back;
- read-only storage fails before contacting the TV with
  `KEY_STORAGE_READONLY`;
- an existing working key returns `already_paired`; `force: true` is required
  to replace it deliberately;
- an unanswered prompt returns `PAIRING_TIMEOUT`.

Enabling the tool widens the trust boundary because any authenticated MCP client
can raise a prompt on the TV. It still cannot approve that prompt or read the
resulting key.

The operator command and MCP tool share the same pairing service; there is no
second implementation with different persistence or approval behavior.

## Key source and persistence

The key read order is:

1. `WEBOSMCP__CLIENTKEY`
2. `WEBOSMCP__CLIENTKEYFILE`
3. `WEBOSMCP__CLIENTKEYPATH`

The inline value and mounted secret file are operator-owned read-only sources.
Pairing writes only to `CLIENTKEYPATH`. If a mounted secret is used and pairing
must remain possible, `CLIENTKEYPATH` still needs a separate writable durable
volume.

The TV grants a key against the permission manifest presented at pairing time.
An upgrade that requests a new permission does not widen an old grant. A denied
new capability may require an explicit re-pair; existing granted commands keep
working. See [re-pairing](troubleshooting.md#re-pairing-after-permission-changes).

## Local stdio setup

Build from source:

```bash
git clone https://github.com/anurmatov/webos-mcp.git
cd webos-mcp
dotnet build -c Release
```

Configure and pair:

```bash
export WEBOSMCP__HOST=192.0.2.10
export WEBOSMCP__MACADDRESS=00:11:22:33:44:55
export WEBOSMCP__BROADCASTADDRESS=192.0.2.255

dotnet run --project src/WebosMcp.Server -- discover
dotnet run --project src/WebosMcp.Server -- pair
dotnet run --project src/WebosMcp.Server -- status
```

For a desktop client, point the command at the published DLL:

```json
{
  "mcpServers": {
    "webos-mcp": {
      "command": "dotnet",
      "args": [
        "/path/to/webos-mcp/src/WebosMcp.Server/bin/Release/net10.0/webos-mcp.dll",
        "stdio"
      ],
      "env": {
        "WEBOSMCP__HOST": "192.0.2.10",
        "WEBOSMCP__MACADDRESS": "00:11:22:33:44:55",
        "WEBOSMCP__BROADCASTADDRESS": "192.0.2.255"
      }
    }
  }
}
```

`stdio` is the default command. Stdout is reserved for MCP protocol messages;
all logging goes to stderr.

YouTube Lounge credentials are carried in requests to Google's service,
including one event-stream query. HTTP client logging is raised to Warning so
request URIs are never written to normal logs. YouTube control is the only
feature that needs outbound internet; all other control stays on the LAN.

## Next steps

- [Configuration reference](configuration.md)
- [Complete tool surface](tools.md)
- [Errors and troubleshooting](troubleshooting.md)
