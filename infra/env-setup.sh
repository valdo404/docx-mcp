#!/usr/bin/env bash
# Source this file to export Cloudflare env vars from Pulumi outputs.
#   source infra/env-setup.sh
#
# Also requires CLOUDFLARE_API_TOKEN in env (not stored in Pulumi outputs).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-${(%):-%x}}")" && pwd)"
STACK="${PULUMI_STACK:-prod}"

_out() {
  pulumi stack output "$1" --stack "$STACK" --cwd "$SCRIPT_DIR" --show-secrets 2>/dev/null
}

export CLOUDFLARE_ACCOUNT_ID="$(_out cloudflare_account_id)"
export R2_BUCKET_NAME="$(_out r2_bucket_name)"
export KV_NAMESPACE_ID="$(_out storage_kv_namespace_id)"
export D1_DATABASE_ID="$(_out auth_d1_database_id)"
export R2_ACCESS_KEY_ID="$(_out r2_access_key_id)"
export R2_SECRET_ACCESS_KEY="$(_out r2_secret_access_key)"
export CLOUDFLARE_API_TOKEN="$(pulumi config get cloudflare:apiToken --stack "$STACK" --cwd "$SCRIPT_DIR" 2>/dev/null)"
export OAUTH_GOOGLE_CLIENT_ID="$(_out oauth_google_client_id)"
export OAUTH_GOOGLE_CLIENT_SECRET="$(_out oauth_google_client_secret)"

echo "Env loaded from Pulumi stack '$STACK':"
echo "  CLOUDFLARE_ACCOUNT_ID=$CLOUDFLARE_ACCOUNT_ID"
echo "  R2_BUCKET_NAME=$R2_BUCKET_NAME"
echo "  R2_ACCESS_KEY_ID=$R2_ACCESS_KEY_ID"
echo "  R2_SECRET_ACCESS_KEY=(set)"
echo "  KV_NAMESPACE_ID=$KV_NAMESPACE_ID"
echo "  D1_DATABASE_ID=$D1_DATABASE_ID"
echo "  CLOUDFLARE_API_TOKEN=(set)"
echo "  OAUTH_GOOGLE_CLIENT_ID=${OAUTH_GOOGLE_CLIENT_ID:-(not set)}"
echo "  OAUTH_GOOGLE_CLIENT_SECRET=${OAUTH_GOOGLE_CLIENT_SECRET:+****(set)}"
