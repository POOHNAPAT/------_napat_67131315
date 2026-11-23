#!/bin/sh
set -eu
app_port="${PORT:-8080}"
case "$app_port" in ''|*[!0-9]*) echo 'PORT must be numeric.' >&2; exit 1 ;; esac
if [ "$app_port" -lt 1 ] || [ "$app_port" -gt 65535 ]; then
  echo 'PORT must be between 1 and 65535.' >&2; exit 1
fi
case "${COURTBOOK_PORTAL:-Customer}" in
  Admin) export Portals__AdminPort="$app_port" ;;
  Customer) export Portals__AdminPort=0 ;;
  *) echo 'COURTBOOK_PORTAL must be Customer or Admin.' >&2; exit 1 ;;
esac
public_host="${RENDER_EXTERNAL_HOSTNAME:-${RAILWAY_PUBLIC_DOMAIN:-}}"
if [ -n "$public_host" ]; then
  export AllowedHosts="${AllowedHosts:-$public_host}"
fi
public_url="${RENDER_EXTERNAL_URL:-}"
if [ -z "$public_url" ] && [ -n "$public_host" ]; then public_url="https://$public_host"; fi
if [ -n "$public_url" ]; then
  if [ "${COURTBOOK_PORTAL:-Customer}" = Admin ]; then
    export Account__AdminOrigin="${Account__AdminOrigin:-$public_url}"
  else
    export Account__PublicOrigin="${Account__PublicOrigin:-$public_url}"
  fi
fi
exec dotnet CourtBook.dll --urls "http://0.0.0.0:$app_port"
