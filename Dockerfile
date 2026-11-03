# Specarr: build the app, then run it on the ASP.NET image with ffprobe, mediainfo and dovi_tool.
ARG FFMPEG_VERSION=8.1
ARG DOVI_TOOL_VERSION=2.3.4

# Static ffprobe from BtbN (linked from ffmpeg.org), the same build Muxarr uses.
FROM debian:13-slim AS ffprobe
ARG FFMPEG_VERSION
ARG TARGETARCH
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates xz-utils \
    && rm -rf /var/lib/apt/lists/*
RUN set -eux; \
    case "${TARGETARCH:-amd64}" in \
        amd64) arch=linux64 ;; \
        arm64) arch=linuxarm64 ;; \
        *) echo "Unsupported TARGETARCH: ${TARGETARCH}" >&2; exit 1 ;; \
    esac; \
    curl -fsSL "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n${FFMPEG_VERSION}-latest-${arch}-gpl-${FFMPEG_VERSION}.tar.xz" -o /tmp/ffmpeg.tar.xz; \
    mkdir -p /out; \
    tar -xJf /tmp/ffmpeg.tar.xz -C /out --strip-components=2 --wildcards '*/bin/ffprobe'; \
    /out/ffprobe -version

# dovi_tool static (musl) build from quietvoid, the same one Muxarr uses, for the Dolby Vision RPU.
FROM debian:13-slim AS dovi
ARG DOVI_TOOL_VERSION
ARG TARGETARCH
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*
RUN set -eux; \
    case "${TARGETARCH:-amd64}" in \
        amd64) target=x86_64-unknown-linux-musl ;; \
        arm64) target=aarch64-unknown-linux-musl ;; \
        *) echo "Unsupported TARGETARCH: ${TARGETARCH}" >&2; exit 1 ;; \
    esac; \
    curl -fsSL "https://github.com/quietvoid/dovi_tool/releases/download/${DOVI_TOOL_VERSION}/dovi_tool-${DOVI_TOOL_VERSION}-${target}.tar.gz" -o /tmp/dovi_tool.tar.gz; \
    mkdir -p /out; \
    tar -xzf /tmp/dovi_tool.tar.gz -C /out; \
    /out/dovi_tool --version

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json ./
COPY src/Specarr/Specarr.csproj src/Specarr/
RUN dotnet restore src/Specarr/Specarr.csproj
COPY src/ src/
RUN dotnet publish src/Specarr/Specarr.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends mediainfo tzdata \
    && rm -rf /var/lib/apt/lists/*
COPY --from=ffprobe /out/ffprobe /usr/local/bin/ffprobe
COPY --from=dovi /out/dovi_tool /usr/local/bin/dovi_tool

WORKDIR /app
COPY --from=build /app ./

# The cache (specarr.db) lives in /config: mount a folder there so a restart does not read every file again.
RUN mkdir -p /config && chown $APP_UID /config
VOLUME /config

ENV ASPNETCORE_HTTP_PORTS=8185 \
    TZ=Etc/UTC
EXPOSE 8185

# Read-only: Specarr never writes to the library. Compose can pick the user (user: 1000:1000).
USER $APP_UID
ENTRYPOINT ["dotnet", "Specarr.dll"]
