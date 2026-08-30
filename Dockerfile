# syntax=docker/dockerfile:1

# ---------------------------------------------------------------- build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the project graph first so dependency layers cache well.
COPY Directory.Build.props WebosMcp.slnx ./
COPY src/WebosMcp.Domain/WebosMcp.Domain.csproj src/WebosMcp.Domain/
COPY src/WebosMcp.Application/WebosMcp.Application.csproj src/WebosMcp.Application/
COPY src/WebosMcp.Infrastructure/WebosMcp.Infrastructure.csproj src/WebosMcp.Infrastructure/
COPY src/WebosMcp.Server/WebosMcp.Server.csproj src/WebosMcp.Server/
COPY tests/WebosMcp.Tests/WebosMcp.Tests.csproj tests/WebosMcp.Tests/
RUN dotnet restore src/WebosMcp.Server/WebosMcp.Server.csproj

COPY . .
RUN dotnet publish src/WebosMcp.Server/WebosMcp.Server.csproj \
        -c Release \
        -o /app \
        --no-restore

# -------------------------------------------------------------- runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Runs as a non-root user. The client key lives under this user's home so the
# key file is owned by the process that reads it, not by root.
RUN useradd --create-home --uid 10001 webosmcp
COPY --from=build --chown=webosmcp:webosmcp /app .
USER webosmcp

ENV DOTNET_EnableDiagnostics=0 \
    WEBOS_MCP_HTTP_BIND=127.0.0.1 \
    WEBOS_MCP_HTTP_PORT=8765

# No EXPOSE and no published port by default. The HTTP transport binds to
# loopback unless the operator explicitly supplies both a non-loopback bind
# address AND an auth token; the server refuses to start otherwise.
#
# No secrets are baked in. Supply WEBOSMCP__HOST, WEBOSMCP__MACADDRESS and the
# client key at run time via environment variables or a mounted secret file.

ENTRYPOINT ["dotnet", "/app/webos-mcp.dll"]
CMD ["http"]
