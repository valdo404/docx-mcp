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
