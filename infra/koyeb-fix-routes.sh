#!/usr/bin/env bash
# Remove public routes from internal Koyeb services.
#
# Problem: Koyeb auto-adds route "/" to all WEB services with protocol=http.
# When multiple services in the same app share the same route, Koyeb's edge
# routes traffic to the wrong service (e.g., gRPC storage instead of proxy) → 502.
#
# Solution: Internal services must use protocol=tcp (mesh-only, no public route).
# Only the proxy keeps protocol=http with route "/".
#
# Usage:
#   source infra/env-setup.sh   # loads KOYEB_TOKEN
#   bash infra/koyeb-fix-routes.sh
#
# Requires: curl, python3, KOYEB_TOKEN or ~/.koyeb.yaml

set -euo pipefail

# --- Resolve API token ---
if [[ -z "${KOYEB_TOKEN:-}" ]]; then
  if [[ -f "$HOME/.koyeb.yaml" ]]; then
    KOYEB_TOKEN=$(grep 'token:' "$HOME/.koyeb.yaml" | awk '{print $2}')
  fi
fi
if [[ -z "${KOYEB_TOKEN:-}" ]]; then
  echo "Error: KOYEB_TOKEN not set. Run: source infra/env-setup.sh" >&2
  exit 1
fi

API="https://app.koyeb.com/v1"
APP_NAME="${KOYEB_APP_NAME:-docx-mcp}"

# --- Resolve service IDs ---
echo "Fetching services for app '$APP_NAME'..."
SERVICES_JSON=$(curl -sf -H "Authorization: Bearer $KOYEB_TOKEN" "$API/services?limit=20")

get_service_id() {
  local name="$1"
  echo "$SERVICES_JSON" | python3 -c "
import sys, json
data = json.load(sys.stdin)
for svc in data.get('services', []):
    if svc.get('name') == '$name':
        print(svc['id']); sys.exit(0)
sys.exit(1)
" 2>/dev/null
}

get_deployment_def() {
  local svc_id="$1"
  local dep_id
  dep_id=$(curl -sf -H "Authorization: Bearer $KOYEB_TOKEN" "$API/services/$svc_id" \
    | python3 -c "import sys,json; print(json.load(sys.stdin)['service']['active_deployment_id'])")
  curl -sf -H "Authorization: Bearer $KOYEB_TOKEN" "$API/deployments/$dep_id" \
    | python3 -c "import sys,json; print(json.dumps(json.load(sys.stdin)['deployment']['definition']))"
}

redeploy_with_def() {
  local svc_id="$1"
  local new_def="$2"
  # Koyeb PATCH /services/:id with full definition triggers a new deployment
  curl -sf -X PATCH -H "Authorization: Bearer $KOYEB_TOKEN" \
    -H "Content-Type: application/json" \
    "$API/services/$svc_id" \
    -d "{\"definition\": $new_def}"
}

# Internal services: switch ports to tcp + remove routes
INTERNAL_SERVICES=("mcp-http" "storage" "gdrive")

for svc_name in "${INTERNAL_SERVICES[@]}"; do
  svc_id=$(get_service_id "$svc_name") || true
  if [[ -z "$svc_id" ]]; then
    echo "  SKIP $svc_name (not found)"
    continue
  fi

  current_def=$(get_deployment_def "$svc_id")
  current_protocol=$(echo "$current_def" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('ports',[{}])[0].get('protocol',''))")
  current_routes=$(echo "$current_def" | python3 -c "import sys,json; d=json.load(sys.stdin); print(len(d.get('routes',[])))")

  if [[ "$current_protocol" == "tcp" && "$current_routes" == "0" ]]; then
    echo "  OK   $svc_name — already tcp, no routes"
    continue
  fi

  echo "  FIX  $svc_name — protocol=$current_protocol, routes=$current_routes → tcp, no routes"

  # Build new definition: change ports.protocol to tcp, remove routes
  new_def=$(echo "$current_def" | python3 -c "
import sys, json
d = json.load(sys.stdin)
d['routes'] = []
for p in d.get('ports', []):
    p['protocol'] = 'tcp'
print(json.dumps(d))
")

  result=$(redeploy_with_def "$svc_id" "$new_def")
  new_version=$(echo "$result" | python3 -c "import sys,json; print(json.load(sys.stdin)['service']['version'])")
  echo "         → deployed (version $new_version)"
done

echo ""
echo "Verifying proxy keeps http + route /..."
proxy_id=$(get_service_id "proxy") || true
if [[ -n "$proxy_id" ]]; then
  proxy_def=$(get_deployment_def "$proxy_id")
  echo "$proxy_def" | python3 -c "
import sys, json
d = json.load(sys.stdin)
proto = d.get('ports',[{}])[0].get('protocol','')
routes = d.get('routes', [])
print(f'  proxy: protocol={proto}, routes={json.dumps(routes)}')
"
fi

echo ""
echo "Done. Wait for deployments to become HEALTHY, then test:"
echo "  curl -s https://mcp.docx.lapoule.dev/health"
