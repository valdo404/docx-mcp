-- OAuth 2.1 Authorization Server tables
-- Supports Dynamic Client Registration (RFC 7591), Authorization Code + PKCE, Refresh Token rotation

-- Registered clients (DCR or pre-registered)
CREATE TABLE IF NOT EXISTS "oauth_client" (
    "id" TEXT PRIMARY KEY NOT NULL,
    "clientName" TEXT NOT NULL,
    "redirectUris" TEXT NOT NULL,
    "grantTypes" TEXT NOT NULL,
    "tokenEndpointAuthMethod" TEXT NOT NULL DEFAULT 'none',
    "clientSecret" TEXT,
    "clientUri" TEXT,
    "logoUri" TEXT,
    "createdAt" TEXT NOT NULL,
    "updatedAt" TEXT NOT NULL
);

-- Authorization codes (short-lived, PKCE)
CREATE TABLE IF NOT EXISTS "oauth_authorization_code" (
    "code" TEXT PRIMARY KEY NOT NULL,
    "clientId" TEXT NOT NULL,
    "tenantId" TEXT NOT NULL,
    "redirectUri" TEXT NOT NULL,
    "scope" TEXT NOT NULL,
    "codeChallenge" TEXT NOT NULL,
    "resource" TEXT NOT NULL,
    "expiresAt" TEXT NOT NULL,
    "createdAt" TEXT NOT NULL,
    FOREIGN KEY ("clientId") REFERENCES "oauth_client"("id") ON DELETE CASCADE,
    FOREIGN KEY ("tenantId") REFERENCES "tenant"("id") ON DELETE CASCADE
);

-- Access tokens (opaque, like PATs)
CREATE TABLE IF NOT EXISTS "oauth_access_token" (
    "id" TEXT PRIMARY KEY NOT NULL,
    "clientId" TEXT NOT NULL,
    "tenantId" TEXT NOT NULL,
    "tokenHash" TEXT NOT NULL UNIQUE,
    "tokenPrefix" TEXT NOT NULL,
    "scope" TEXT NOT NULL,
    "resource" TEXT NOT NULL,
    "expiresAt" TEXT NOT NULL,
    "createdAt" TEXT NOT NULL,
    "lastUsedAt" TEXT,
    FOREIGN KEY ("clientId") REFERENCES "oauth_client"("id") ON DELETE CASCADE,
    FOREIGN KEY ("tenantId") REFERENCES "tenant"("id") ON DELETE CASCADE
);

-- Refresh tokens (opaque, rotation obligatoire)
CREATE TABLE IF NOT EXISTS "oauth_refresh_token" (
    "id" TEXT PRIMARY KEY NOT NULL,
    "clientId" TEXT NOT NULL,
    "tenantId" TEXT NOT NULL,
    "tokenHash" TEXT NOT NULL UNIQUE,
    "scope" TEXT NOT NULL,
    "resource" TEXT NOT NULL,
    "expiresAt" TEXT NOT NULL,
    "createdAt" TEXT NOT NULL,
    "revoked" INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY ("clientId") REFERENCES "oauth_client"("id") ON DELETE CASCADE,
    FOREIGN KEY ("tenantId") REFERENCES "tenant"("id") ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS "idx_oauth_access_token_hash" ON "oauth_access_token"("tokenHash");
CREATE INDEX IF NOT EXISTS "idx_oauth_access_token_tenant" ON "oauth_access_token"("tenantId");
CREATE INDEX IF NOT EXISTS "idx_oauth_refresh_token_hash" ON "oauth_refresh_token"("tokenHash");
CREATE INDEX IF NOT EXISTS "idx_oauth_authorization_code_expires" ON "oauth_authorization_code"("expiresAt");
