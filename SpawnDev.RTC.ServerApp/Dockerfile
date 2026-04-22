# SpawnDev.RTC.ServerApp - WebTorrent-compatible signaling server
#
# Build:
#   docker build -t spawndev/rtc-signaling -f SpawnDev.RTC.ServerApp/Dockerfile .
# Run:
#   docker run -d -p 8080:8080 --name rtc-signaling spawndev/rtc-signaling
# Production TLS via reverse proxy (nginx / Caddy / Traefik / Cloudflare) - see
# Docs/run-a-tracker.md in the repo.

ARG DOTNET_VERSION=10.0

# -------- build stage --------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Copy the RTC sub-repository first so restore benefits from layer caching.
COPY SpawnDev.RTC/ SpawnDev.RTC/
COPY Src/ Src/

RUN dotnet publish SpawnDev.RTC/SpawnDev.RTC.ServerApp/SpawnDev.RTC.ServerApp.csproj \
    -c Release \
    -o /app \
    --self-contained false \
    /p:UseAppHost=false

# -------- runtime stage --------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 \
  CMD wget -q --spider http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "SpawnDev.RTC.ServerApp.dll"]
