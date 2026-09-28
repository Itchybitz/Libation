#!/bin/bash
# Entrypoint for the LibationWeb image: check the mounted config folder, then run the web portal.
#
# Unlike the scan/liberate image (liberate.sh's init_libation), this one does NOT stage a copy of the
# mounted config in a folder inside the container. The portal writes to Libation's files folder while it
# runs - an account added or removed from the Activity tab, refreshed Audible tokens, LibationWeb.json,
# the image cache, the search index - and a staged copy would throw all of that away the next time the
# container was recreated. So Libation runs against the mounted folder itself.
#
# The two folders a container has to relocate are applied by the portal itself, to its in-memory settings
# only (LIBATION_BOOKS_DIR / LIBATION_INPROGRESS_DIR, see Startup/LibationBootstrap.cs), so the mounted
# Settings.json is read and never rewritten with container paths.

# Functions only - info/warn/error and is_mounted. liberate.sh runs nothing when it is sourced.
# shellcheck source=liberate.sh
source /libation/liberate.sh

CONFIG=${LIBATION_CONFIG_DIR:-/config}
BOOKS=${LIBATION_BOOKS_DIR:-/data}

if [ ! -d "${CONFIG}" ]; then
  error "${CONFIG} is not a directory. Mount Libation's config folder there (the one holding Settings.json and AccountsSettings.json)."
  exit 1
fi

if [ ! -w "${CONFIG}" ]; then
  error "${CONFIG} is not writable by uid $(id -u):$(id -g). Libation keeps its database, its log and your accounts there."
  error "Set PUID/PGID to a user that may write to that folder, and give that user write access in Control Panel > Shared Folder > Edit > Permissions."
  exit 1
fi

if [ ! -f "${CONFIG}/Settings.json" ]; then
  info "no Settings.json in ${CONFIG}: creating one with Books set to ${BOOKS}"
  printf '{ "Books": "%s" }\n' "${BOOKS}" > "${CONFIG}/Settings.json"
fi

if [ ! -f "${CONFIG}/AccountsSettings.json" ]; then
  warn "${CONFIG}/AccountsSettings.json not found: no Audible account yet. Add one from the portal's Activity tab (it is saved in ${CONFIG} and survives a restart)."
fi

if ! is_mounted "${BOOKS}"; then
  warn "${BOOKS} does not appear to be mounted, books will not be saved"
fi

# Everything Libation writes goes to the mounted folder. Set in the image as well, so that
# `docker exec libation-web LibationCli ...` works against the same folder without any arguments.
export LIBATION_FILES_DIR="${CONFIG}"

# `docker run ... libation-web bash` (or LibationCli login-external ...) gets the same environment.
if [ "$#" -gt 0 ]; then
  info "executing '$*' instead of the web portal"
  exec "$@"
fi

if [[ -z "${LIBATION_WEB_PASSWORD}" && "${LIBATION_WEB_ALLOW_ANONYMOUS}" != "1" ]]; then
  error "LIBATION_WEB_PASSWORD is not set (or set LIBATION_WEB_ALLOW_ANONYMOUS=1 on a private network). Refusing to start an open portal."
  exit 1
fi

info "starting LibationWeb on ${LIBATION_WEB_URLS:-http://0.0.0.0:8791} against ${CONFIG}"
exec /libation/LibationWeb
