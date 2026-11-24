#!/usr/bin/env bash
# Render livekit.generated.yaml from .env + template.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
ENV_FILE="${ROOT}/.env"
TEMPLATE="${ROOT}/livekit.yaml.template"
OUT="${ROOT}/livekit.generated.yaml"

if [[ ! -f "$ENV_FILE" ]]; then
  echo "Missing $ENV_FILE — copy .env.example and fill secrets." >&2
  exit 1
fi

# shellcheck disable=SC1090
set -a
source "$ENV_FILE"
set +a

: "${PUBLIC_IP:?PUBLIC_IP required}"
: "${LIVEKIT_API_KEY:?LIVEKIT_API_KEY required}"
: "${LIVEKIT_API_SECRET:?LIVEKIT_API_SECRET required}"

sed \
  -e "s|__PUBLIC_IP__|${PUBLIC_IP}|g" \
  -e "s|__LIVEKIT_API_KEY__|${LIVEKIT_API_KEY}|g" \
  -e "s|__LIVEKIT_API_SECRET__|${LIVEKIT_API_SECRET}|g" \
  "$TEMPLATE" >"$OUT"

echo "Wrote $OUT"
