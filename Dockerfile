# Specarr: build the app, then run it on the ASP.NET image with ffprobe, mediainfo and dovi_tool,
# each at a pinned version. The weekly "Tool updates" workflow bumps these ARG lines in a pull
# request; nothing updates on its own. The Tools page shows the pins next to the newest versions.
ARG FFMPEG_VERSION=9.0
ARG MEDIAINFO_VERSION=26.10
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

# MediaInfo CLI, the static build MediaArea makes for AWS Lambda (Ubuntu's package is 24.01), as in Ripparr.
FROM debian:13-slim AS mediainfo
ARG MEDIAINFO_VERSION
ARG TARGETARCH
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates unzip \
    && rm -rf /var/lib/apt/lists/*
RUN set -eux; \
    case "${TARGETARCH:-amd64}" in \
        amd64) arch=x86_64 ;; \
        arm64) arch=arm64 ;; \
        *) echo "Unsupported TARGETARCH: ${TARGETARCH}" >&2; exit 1 ;; \
    esac; \
    curl -fsSL "https://mediaarea.net/download/binary/mediainfo/${MEDIAINFO_VERSION}/MediaInfo_CLI_${MEDIAINFO_VERSION}_Lambda_${arch}.zip" -o /tmp/mediainfo.zip; \
    mkdir -p /tmp/mediainfo /out; \
    unzip -q /tmp/mediainfo.zip -d /tmp/mediainfo; \
    install -m 0755 "$(find /tmp/mediainfo -type f -name mediainfo | head -n1)" /out/mediainfo; \
    /out/mediainfo --version

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json ./
COPY src/Specarr/Specarr.csproj src/Specarr/
RUN dotnet restore src/Specarr/Specarr.csproj
COPY src/ src/
RUN dotnet publish src/Specarr/Specarr.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG FFMPEG_VERSION
ARG MEDIAINFO_VERSION
ARG DOVI_TOOL_VERSION
RUN apt-get update && apt-get install -y --no-install-recommends tzdata \
    && rm -rf /var/lib/apt/lists/*
COPY --from=mediainfo /out/mediainfo /usr/local/bin/mediainfo
COPY --from=ffprobe /out/ffprobe /usr/local/bin/ffprobe
COPY --from=dovi /out/dovi_tool /usr/local/bin/dovi_tool

WORKDIR /app
COPY --from=build /app ./

# The cache (specarr.db) lives in /config: mount a folder there so a restart does not read every file again.
RUN mkdir -p /config && chown $APP_UID /config
VOLUME /config

# The pins show on the Tools page next to what each tool reports and the newest upstream version.
ENV SPECARR_PIN_FFPROBE=${FFMPEG_VERSION} \
    SPECARR_PIN_MEDIAINFO=${MEDIAINFO_VERSION} \
    SPECARR_PIN_DOVI_TOOL=${DOVI_TOOL_VERSION} \
    ASPNETCORE_HTTP_PORTS=8185 \
    TZ=Etc/UTC
EXPOSE 8185

# Read-only: Specarr never writes to the library. Compose can pick the user (user: 1000:1000).
USER $APP_UID
ENTRYPOINT ["dotnet", "Specarr.dll"]
