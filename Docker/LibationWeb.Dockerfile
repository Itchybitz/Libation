# LibationWeb: Libation's web portal (library, Audible scan, download queue) plus LibationCli for
# account setup inside the container. Build from the repository root:
#
#   docker build -f Docker/LibationWeb.Dockerfile -t libation-web .
#
# or with docker-compose.web.yml beside this repository's checkout (see docs/installation/web-portal.md).
FROM --platform=${BUILDPLATFORM} mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
# ReadyToRun makes the portal start faster on a slow NAS but roughly doubles the build. Pass
# --build-arg READYTORUN=false for a quick trial build.
ARG READYTORUN=true

COPY Source /Source
# Framework-dependent: the aspnet runtime image below carries both the ASP.NET Core and the base
# runtime, so neither app needs to bundle one. Both land in one folder so they share their libraries.
RUN dotnet publish \
    /Source/LibationWeb/LibationWeb.csproj \
    --os linux \
    --arch ${TARGETARCH} \
    --configuration Release \
    --output /publish \
    -p:PublishProtocol=FileSystem \
    -p:PublishReadyToRun=${READYTORUN} \
    -p:SelfContained=false \
 && dotnet publish \
    /Source/LibationCli/LibationCli.csproj \
    --os linux \
    --arch ${TARGETARCH} \
    --configuration Release \
    --output /publish \
    -p:PublishProtocol=FileSystem \
    -p:PublishReadyToRun=${READYTORUN} \
    -p:SelfContained=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG USER_UID=1001
ARG USER_GID=1001

# Set the character set that will be used for folder and filenames when liberating
ENV LANG=C.UTF-8
ENV LC_ALL=C.UTF-8

# The mounted config folder IS Libation's files folder in this image - nothing is staged into a copy, so
# an account added from the portal, its refreshed tokens, LibationWeb.json, the image cache and the search
# index all survive the container being recreated (see web-entrypoint.sh).
# LIBATION_FILES_DIR here also covers processes started with `docker exec`, so
# `docker exec libation-web LibationCli login-external ...` acts on /config without any arguments.
ENV LIBATION_CONFIG_DIR=/config
ENV LIBATION_FILES_DIR=/config
ENV LIBATION_BOOKS_DIR=/data
# Partly-downloaded files are scratch: keep them inside the container, off the NAS share.
ENV LIBATION_INPROGRESS_DIR=/tmp

# The portal's own knobs. LIBATION_WEB_PASSWORD (or LIBATION_WEB_ALLOW_ANONYMOUS=1) must be supplied at run time.
ENV LIBATION_WEB_URLS=http://0.0.0.0:8791
ENV LIBATION_WEB_CONCURRENCY=1

RUN apt-get update && apt-get -y upgrade && \
    apt-get install -y --no-install-recommends jq curl ca-certificates && \
    rm -rf /var/lib/apt/lists/* && \
    mkdir -m777 ${LIBATION_BOOKS_DIR}

COPY --from=build /publish /libation
# liberate.sh is carried for its log helpers and is_mounted; its own loop is not used here. No
# appsettings.json: LIBATION_FILES_DIR already names the folder, and a second answer only confuses.
COPY Docker/liberate.sh Docker/web-entrypoint.sh /libation/

RUN chmod +x /libation/liberate.sh /libation/web-entrypoint.sh && \
    ln -s /libation/LibationCli /usr/local/bin/LibationCli && \
    ln -s /libation/LibationWeb /usr/local/bin/LibationWeb

USER ${USER_UID}:${USER_GID}

EXPOSE 8791

HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
  CMD curl -fsS "http://127.0.0.1:${LIBATION_WEB_HEALTH_PORT:-8791}/api/health" >/dev/null || exit 1

ENTRYPOINT ["/libation/web-entrypoint.sh"]
