# SpawnDev.RTC.ServerApp - WebTorrent-compatible signaling server + optional STUN/TURN
#
# Build:
#   docker build -t spawndev/rtc-signaling -f SpawnDev.RTC.ServerApp/Dockerfile .
#
# Run (signaling only):
#   docker run -d -p 8080:8080 --name rtc-signaling spawndev/rtc-signaling
#
# Run (signaling + origin allowlist + ephemeral-credential TURN, tracker-gated):
#   docker run -d --name rtc-signaling \
#     -p 8080:8080 \
#     -p 3478:3478/udp -p 3478:3478 \
#     -e RTC__AllowedOrigins="https://hub.spawndev.com;https://*.spawndev.com" \
#     -e RTC__StunTurn__Enabled=true \
#     -e RTC__StunTurn__ListenAddress=0.0.0.0 \
#     -e RTC__StunTurn__RelayAddress=<public IP of host> \
#     -e RTC__StunTurn__EphemeralCredentialSharedSecret=<32+ byte secret> \
#     -e RTC__StunTurn__TrackerGated=true \
#     spawndev/rtc-signaling
#
# Production TLS for the WebSocket signaling endpoint via reverse proxy (nginx /
# Caddy / Traefik / Cloudflare) - see Docs/run-a-tracker.md in the repo. TURN
# itself uses its own HMAC-signed MESSAGE-INTEGRITY; no TLS termination needed
# (the 5-tuple + ephemeral credentials are the auth).

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
# WebSocket signaling + HTTP health/stats endpoints.
EXPOSE 8080
# STUN/TURN (when RTC__StunTurn__Enabled=true). Both UDP (STUN binding + TURN
# allocation) and TCP (optional fallback) on the same port per RFC 5766.
EXPOSE 3478/udp
EXPOSE 3478/tcp

HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 \
  CMD wget -q --spider http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "SpawnDev.RTC.ServerApp.dll"]
