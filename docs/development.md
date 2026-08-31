# Development

## Build and test

```bash
dotnet build
dotnet test
docker build -t webos-mcp:local .
```

## Architecture

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
raw command passthrough, screen recording or repeated/scheduled capture, OCR or
analysis of a captured frame, hidden/service-menu commands, or multi-device
orchestration will be declined. Those are
[deliberate boundaries](troubleshooting.md#hard-boundaries), not gaps.

## Documentation checks

Before opening a documentation pull request, run the build and tests above.
Also verify that `README.md` stays within its stated size target, every relative
Markdown link resolves, the tool count matches the registered MCP surface, and
the Docker quick start still reaches `initialize` and `tools/list` from a clean
checkout.

The automated suite uses fakes. It verifies mapping, validation, error
selection, serialization, reconnect behavior and timeouts, but not a particular
TV model or firmware. Physical-device acceptance is a separate check.
