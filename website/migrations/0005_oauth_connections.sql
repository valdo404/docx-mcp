-- OAuth connections for external file providers (Google Drive, OneDrive, etc.)
-- Each tenant can have multiple connections per provider.
-- Tokens are stored in D1 (encrypted at rest by Cloudflare).

CREATE TABLE IF NOT EXISTS "oauth_connection" (
    "id" TEXT PRIMARY KEY NOT NULL,
    "tenantId" TEXT NOT NULL,
    "provider" TEXT NOT NULL,
    "displayName" TEXT NOT NULL,
    "providerAccountId" TEXT,
    "accessToken" TEXT NOT NULL,
    "refreshToken" TEXT NOT NULL,
    "tokenExpiresAt" TEXT,
    "scopes" TEXT NOT NULL,
    "createdAt" TEXT NOT NULL,
    "updatedAt" TEXT NOT NULL,
    FOREIGN KEY ("tenantId") REFERENCES "tenant"("id") ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS "idx_oauth_conn_tenant"
    ON "oauth_connection"("tenantId", "provider");
