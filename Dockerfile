# PlexBridge container image. Built for linux/amd64 and linux/arm64 by .github/workflows/release.yml
# once the repository is github.com/007darkmatter5/plexbridge.
#   docker build --build-arg VERSION=0.1.0-local -t plexbridge .

# Compile on the build machine's own architecture and cross-publish for the target (no emulation needed).
# Defaults keep a plain `docker build` working. Buildx overrides them per platform.
ARG BUILDPLATFORM=linux/amd64
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH=amd64
ARG VERSION=0.0.0-dev
WORKDIR /src

# Restore only after the full source is copied. Restoring from the .csproj alone (the usual layer-caching
# trick) makes the SDK leave out Blazor's framework scripts (_framework/blazor.web.js), and the app then
# renders but never becomes interactive.
COPY src/ src/
RUN dotnet restore src/PlexBridge/PlexBridge.csproj -a $TARGETARCH \
    && dotnet publish src/PlexBridge/PlexBridge.csproj -c Release -a $TARGETARCH --no-restore -o /out/app -p:Version=$VERSION \
    && test -f /out/app/wwwroot/_framework/blazor.web.js

# No RUN steps below: the final stage is for the target architecture and must build without emulation.
FROM busybox:1.36.1 AS busybox
FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG VERSION=0.0.0-dev

LABEL org.opencontainers.image.title="PlexBridge" \
      org.opencontainers.image.description="One-way migration companion for two Plex Media Servers" \
      org.opencontainers.image.version=$VERSION \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.source="https://github.com/007darkmatter5/plexbridge"

WORKDIR /app
COPY --from=build /out/app .
COPY --from=busybox /bin/busybox /bin/busybox
COPY docker/entrypoint.sh /usr/local/bin/plexbridge-entrypoint

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ConnectionStrings__DefaultConnection="Data Source=/config/plexbridge.db" \
    DataProtection__KeysPath=/config/keys \
    Logging__File__Path=/config/logs \
    PUID=1654 \
    PGID=1654 \
    TZ=Etc/UTC

# One volume: the SQLite database, the data-protection keys, and the log file.
VOLUME ["/config"]
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
    CMD /bin/busybox wget -q -O - http://127.0.0.1:8080/healthz || exit 1

# Starts as root only to give /config to PUID:PGID, then runs PlexBridge as that user (see entrypoint.sh).
ENTRYPOINT ["plexbridge-entrypoint"]
