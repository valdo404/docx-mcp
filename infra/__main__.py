"""Cloudflare + GCP infrastructure for docx-mcp."""

import hashlib
import json

import pulumi
import pulumi_cloudflare as cloudflare
import pulumi_gcp as gcp

config = pulumi.Config()
account_id = config.require("accountId")
gcp_project = config.get("gcpProject") or "lv-project-313715"

# =============================================================================
# R2 — Document storage (DOCX baselines, WAL, checkpoints)
# =============================================================================

storage_bucket = cloudflare.R2Bucket(
    "docx-storage",
    account_id=account_id,
    name="docx-mcp-storage",
    location="WEUR",
)

# =============================================================================
# R2 API Token — S3-compatible access for docx-storage-cloudflare
# Access Key ID = token.id, Secret Access Key = SHA-256(token.value)
# =============================================================================

r2_write_perms = cloudflare.get_api_token_permission_groups_list(
    name="Workers R2 Storage Write",
    scope="com.cloudflare.api.account",
)

r2_token = cloudflare.ApiToken(
    "docx-r2-token",
    name="docx-mcp-storage-r2",
    policies=[
        {
            "effect": "allow",
            "permission_groups": [{"id": r2_write_perms.results[0].id}],
            "resources": json.dumps({f"com.cloudflare.api.account.{account_id}": "*"}),
        }
    ],
)

r2_access_key_id = r2_token.id
r2_secret_access_key = r2_token.value.apply(
    lambda v: hashlib.sha256(v.encode()).hexdigest()
)

# =============================================================================
# KV — Storage index & locks (used by docx-storage-cloudflare)
# =============================================================================

storage_kv = cloudflare.WorkersKvNamespace(
    "docx-storage-kv",
    account_id=account_id,
    title="docx-mcp-storage-index",
)

# =============================================================================
# D1 — Auth database (used by SSE proxy + website)
# Import existing: 609c7a5e-34d2-4ca3-974c-8ea81bd7897b
# =============================================================================

auth_db = cloudflare.D1Database(
    "docx-auth-db",
    account_id=account_id,
    name="docx-mcp-auth",
    read_replication={"mode": "disabled"},
    opts=pulumi.ResourceOptions(protect=True),
)

# =============================================================================
# KV — Website sessions (used by Better Auth)
# Import existing: ab2f243e258b4eb2b3be9dfaf7665b38
# =============================================================================

session_kv = cloudflare.WorkersKvNamespace(
    "docx-session-kv",
    account_id=account_id,
    title="SESSION",
    opts=pulumi.ResourceOptions(protect=True),
)

# =============================================================================
# GCP — Google Drive API (for OAuth file sync)
# =============================================================================

drive_api = gcp.projects.Service(
    "drive-api",
    project=gcp_project,
    service="drive.googleapis.com",
    disable_on_destroy=False,
)

# OAuth Client ID — must be created manually in GCP Console (no API available since
# the IAP OAuth Admin API was deprecated in July 2025 with no replacement).
#   1. Go to: https://console.cloud.google.com/apis/credentials?project=lv-project-313715
#   2. Create OAuth 2.0 Client ID (type: Web application)
#   3. Add redirect URI: https://docx.lapoule.dev/api/oauth/callback/google-drive
#   4. Store credentials:
#        pulumi config set --secret docx-mcp-infra:oauthGoogleClientId "<CLIENT_ID>"
#        pulumi config set --secret docx-mcp-infra:oauthGoogleClientSecret "<CLIENT_SECRET>"
oauth_google_client_id = config.get_secret("oauthGoogleClientId") or ""
oauth_google_client_secret = config.get_secret("oauthGoogleClientSecret") or ""

# =============================================================================
# Cloudflare Pages — Website (secrets injection)
# Import existing: pulumi import cloudflare:index/pagesProject:PagesProject docx-website <account_id>/docx-mcp-website
# =============================================================================

better_auth_secret = config.get_secret("betterAuthSecret") or ""
oauth_github_client_id = config.get_secret("oauthGithubClientId") or ""
oauth_github_client_secret = config.get_secret("oauthGithubClientSecret") or ""

_pages_shared_config = {
    "compatibility_date": "2026-01-16",
    "compatibility_flags": ["nodejs_compat", "disable_nodejs_process_v2"],
    "d1_databases": {"DB": {"id": auth_db.id}},
    "kv_namespaces": {"SESSION": {"namespace_id": session_kv.id}},
    "env_vars": {
        "BETTER_AUTH_URL": {"type": "plain_text", "value": "https://docx.lapoule.dev"},
        "GCS_BUCKET_NAME": {"type": "plain_text", "value": "docx-mcp-sessions"},
    },
    "fail_open": True,
    "usage_model": "standard",
}

pages_project = cloudflare.PagesProject(
    "docx-website",
    account_id=account_id,
    name="docx-mcp-website",
    production_branch="main",
    deployment_configs={
        "production": {
            **_pages_shared_config,
            "env_vars": {
                **_pages_shared_config["env_vars"],
                "BETTER_AUTH_SECRET": {"type": "secret_text", "value": better_auth_secret},
                "OAUTH_GITHUB_CLIENT_ID": {"type": "secret_text", "value": oauth_github_client_id},
                "OAUTH_GITHUB_CLIENT_SECRET": {"type": "secret_text", "value": oauth_github_client_secret},
                "OAUTH_GOOGLE_CLIENT_ID": {"type": "secret_text", "value": oauth_google_client_id},
                "OAUTH_GOOGLE_CLIENT_SECRET": {"type": "secret_text", "value": oauth_google_client_secret},
            },
        },
        "preview": {
            **_pages_shared_config,
            "env_vars": {
                **_pages_shared_config["env_vars"],
                "OAUTH_GOOGLE_CLIENT_ID": {"type": "secret_text", "value": oauth_google_client_id},
                "OAUTH_GOOGLE_CLIENT_SECRET": {"type": "secret_text", "value": oauth_google_client_secret},
            },
        },
    },
    opts=pulumi.ResourceOptions(protect=True),
)

# =============================================================================
# Koyeb — MCP backend services (storage + gdrive + mcp-http + proxy)
# Plugin hosted on GitHub (not Pulumi CDN). Install via:
#   pulumi plugin install resource koyeb v0.1.11 \
#     --server https://github.com/koyeb/pulumi-koyeb/releases/download/v0.1.11/
# Or: source infra/env-setup.sh (auto-installs if missing)
# Auth: KOYEB_TOKEN env var only (no Pulumi config key — provider has no schema).
#   pulumi config set --secret koyebToken "<token>"  # stored under app namespace
#   source infra/env-setup.sh  # exports as KOYEB_TOKEN
# =============================================================================

import pulumi_koyeb as koyeb

KOYEB_REGION = "fra"
GIT_REPO = "github.com/valdo404/docx-system"
GIT_BRANCH = "feat/sse-grpc-multi-tenant-20"


def _koyeb_service(
    name: str,
    dockerfile: str,
    port: int,
    envs: list,
    *,
    public: bool = False,
    http_health_path: str | None = None,
    instance_type: str = "nano",
    scale_to_zero: bool = False,
) -> koyeb.ServiceDefinitionArgs:
    """Build a ServiceDefinitionArgs for a Koyeb service.

    Public services: protocol=http, route "/", scale via requests_per_second.
    Internal (mesh-only) services: protocol=tcp, no routes, min=1 (always on).
    Koyeb requires at least one route for scale-to-zero, which is incompatible
    with tcp — so internal services cannot scale to zero.
    """
    if public:
        port_protocol = "http"
        routes = [koyeb.ServiceDefinitionRouteArgs(path="/", port=port)]
        min_instances = 0 if scale_to_zero else 1
        scaling_targets = [koyeb.ServiceDefinitionScalingTargetArgs(
            requests_per_seconds=[
                koyeb.ServiceDefinitionScalingTargetRequestsPerSecondArgs(value=100),
            ],
        )]
    else:
        port_protocol = "tcp"
        routes = []
        min_instances = 1  # tcp services can't scale to zero (no route to intercept)
        scaling_targets = [koyeb.ServiceDefinitionScalingTargetArgs(
            concurrent_requests=[
                koyeb.ServiceDefinitionScalingTargetConcurrentRequestArgs(value=10),
            ],
        )]
    # Health checks: HTTP for services with http_health_path, TCP otherwise
    if http_health_path:
        health_checks = [
            koyeb.ServiceDefinitionHealthCheckArgs(
                grace_period=10,
                interval=30,
                timeout=5,
                restart_limit=3,
                http=koyeb.ServiceDefinitionHealthCheckHttpArgs(
                    port=port, path=http_health_path,
                ),
            )
        ]
    else:
        health_checks = [
            koyeb.ServiceDefinitionHealthCheckArgs(
                grace_period=10,
                interval=30,
                timeout=5,
                restart_limit=3,
                tcp=koyeb.ServiceDefinitionHealthCheckTcpArgs(port=port),
            )
        ]
    return koyeb.ServiceDefinitionArgs(
        name=name,
        type="WEB",
        regions=[KOYEB_REGION],
        instance_types=[koyeb.ServiceDefinitionInstanceTypeArgs(type=instance_type)],
        scalings=[koyeb.ServiceDefinitionScalingArgs(
            min=min_instances,
            max=2,
            targets=scaling_targets,
        )],
        git=koyeb.ServiceDefinitionGitArgs(
            repository=GIT_REPO,
            branch=GIT_BRANCH,
            dockerfile=koyeb.ServiceDefinitionGitDockerfileArgs(
                dockerfile=dockerfile,
            ),
        ),
        ports=[koyeb.ServiceDefinitionPortArgs(port=port, protocol=port_protocol)],
        routes=routes,
        envs=envs,
        health_checks=health_checks,
    )


# --- App ---
koyeb_app = koyeb.App("docx-mcp", name="docx-mcp")

# --- Service 1: storage (gRPC, mesh-only) ---
cloudflare_api_token = pulumi.Config("cloudflare").require_secret("apiToken")

koyeb_storage = koyeb.Service(
    "koyeb-storage",
    app_name=koyeb_app.name,
    definition=_koyeb_service(
        name="storage",
        dockerfile="Dockerfile.storage-cloudflare",
        port=50051,
        instance_type="nano",
        scale_to_zero=True,  # Applied via CLI (mesh services need no route for CLI/API)
        envs=[
            koyeb.ServiceDefinitionEnvArgs(key="RUST_LOG", value="info,docx_storage_cloudflare=debug"),
            koyeb.ServiceDefinitionEnvArgs(key="GRPC_HOST", value="0.0.0.0"),
            koyeb.ServiceDefinitionEnvArgs(key="GRPC_PORT", value="50051"),
            koyeb.ServiceDefinitionEnvArgs(key="CLOUDFLARE_ACCOUNT_ID", value=account_id),
            koyeb.ServiceDefinitionEnvArgs(key="R2_BUCKET_NAME", value=storage_bucket.name),
            koyeb.ServiceDefinitionEnvArgs(key="R2_ACCESS_KEY_ID", value=r2_access_key_id),
            koyeb.ServiceDefinitionEnvArgs(key="R2_SECRET_ACCESS_KEY", value=r2_secret_access_key),
        ],
    ),
)

# --- Service 2: gdrive (gRPC, mesh-only) ---
koyeb_gdrive = koyeb.Service(
    "koyeb-gdrive",
    app_name=koyeb_app.name,
    definition=_koyeb_service(
        name="gdrive",
        dockerfile="Dockerfile.gdrive",
        port=50052,
        instance_type="nano",
        scale_to_zero=True,  # Applied via CLI (mesh services need no route for CLI/API)
        envs=[
            koyeb.ServiceDefinitionEnvArgs(key="RUST_LOG", value="info"),
            koyeb.ServiceDefinitionEnvArgs(key="GRPC_HOST", value="0.0.0.0"),
            koyeb.ServiceDefinitionEnvArgs(key="GRPC_PORT", value="50052"),
            koyeb.ServiceDefinitionEnvArgs(key="CLOUDFLARE_ACCOUNT_ID", value=account_id),
            koyeb.ServiceDefinitionEnvArgs(key="CLOUDFLARE_API_TOKEN", value=cloudflare_api_token),
            koyeb.ServiceDefinitionEnvArgs(key="D1_DATABASE_ID", value=auth_db.id),
            koyeb.ServiceDefinitionEnvArgs(key="GOOGLE_CLIENT_ID", value=oauth_google_client_id),
            koyeb.ServiceDefinitionEnvArgs(key="GOOGLE_CLIENT_SECRET", value=oauth_google_client_secret),
            koyeb.ServiceDefinitionEnvArgs(key="WATCH_POLL_INTERVAL", value="60"),
        ],
    ),
)

# --- Service 3: mcp-http (HTTP, mesh-only) ---
koyeb_mcp = koyeb.Service(
    "koyeb-mcp-http",
    app_name=koyeb_app.name,
    definition=_koyeb_service(
        name="mcp-http",
        dockerfile="Dockerfile",
        port=3000,
        http_health_path="/health",
        instance_type="small",
        envs=[
            koyeb.ServiceDefinitionEnvArgs(key="MCP_TRANSPORT", value="http"),
            koyeb.ServiceDefinitionEnvArgs(key="ASPNETCORE_URLS", value="http://+:3000"),
            koyeb.ServiceDefinitionEnvArgs(key="STORAGE_GRPC_URL", value="http://storage:50051"),
            koyeb.ServiceDefinitionEnvArgs(key="SYNC_GRPC_URL", value="http://gdrive:50052"),
        ],
    ),
)

# --- Service 4: proxy (HTTP, PUBLIC) ---
koyeb_proxy = koyeb.Service(
    "koyeb-proxy",
    app_name=koyeb_app.name,
    definition=_koyeb_service(
        name="proxy",
        dockerfile="Dockerfile.proxy",
        port=8080,
        public=True,
        http_health_path="/health",
        instance_type="nano",
        scale_to_zero=False,  # Always on — front door for all MCP clients
        envs=[
            koyeb.ServiceDefinitionEnvArgs(key="RUST_LOG", value="info"),
            koyeb.ServiceDefinitionEnvArgs(key="MCP_BACKEND_URL", value="http://mcp-http:3000"),
            koyeb.ServiceDefinitionEnvArgs(key="CLOUDFLARE_ACCOUNT_ID", value=account_id),
            koyeb.ServiceDefinitionEnvArgs(key="CLOUDFLARE_API_TOKEN", value=cloudflare_api_token),
            koyeb.ServiceDefinitionEnvArgs(key="D1_DATABASE_ID", value=auth_db.id),
            koyeb.ServiceDefinitionEnvArgs(key="RESOURCE_URL", value="https://mcp.docx.lapoule.dev"),
            koyeb.ServiceDefinitionEnvArgs(key="AUTH_SERVER_URL", value="https://docx.lapoule.dev"),
        ],
    ),
)

# --- Custom Domain: mcp.docx.lapoule.dev ---
koyeb_domain = koyeb.Domain("docx-mcp-domain",
    name="mcp.docx.lapoule.dev",
    app_name=koyeb_app.name,
)

lapoule_zone = cloudflare.get_zone(filter=cloudflare.GetZoneFilterArgs(
    name="lapoule.dev",
    match="all",
))

cloudflare.DnsRecord("mcp-cname",
    zone_id=lapoule_zone.zone_id,
    name="mcp.docx",
    type="CNAME",
    content=koyeb_domain.intended_cname,
    ttl=1,  # 1 = automatic
    proxied=False,  # DNS-only — Koyeb needs direct access for TLS provisioning
)

# =============================================================================
# Outputs
# =============================================================================

pulumi.export("cloudflare_account_id", account_id)
pulumi.export("r2_bucket_name", storage_bucket.name)
pulumi.export("r2_endpoint", pulumi.Output.concat(
    "https://", account_id, ".r2.cloudflarestorage.com",
))
pulumi.export("r2_access_key_id", r2_access_key_id)
pulumi.export("r2_secret_access_key", pulumi.Output.secret(r2_secret_access_key))
pulumi.export("storage_kv_namespace_id", storage_kv.id)
pulumi.export("auth_d1_database_id", auth_db.id)
pulumi.export("session_kv_namespace_id", session_kv.id)
pulumi.export("oauth_google_client_id", pulumi.Output.secret(oauth_google_client_id))
pulumi.export("oauth_google_client_secret", pulumi.Output.secret(oauth_google_client_secret))
pulumi.export("koyeb_app_id", koyeb_app.id)
pulumi.export("koyeb_mcp_domain", koyeb_domain.name)
