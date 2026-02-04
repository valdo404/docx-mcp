CREATE TABLE IF NOT EXISTS "tenant" (
    "id" TEXT PRIMARY KEY NOT NULL,
    "userId" TEXT NOT NULL UNIQUE,
    "displayName" TEXT,
    "gcsPrefix" TEXT NOT NULL,
    "storageQuotaBytes" INTEGER NOT NULL DEFAULT 104857600,
    "storageUsedBytes" INTEGER NOT NULL DEFAULT 0,
    "createdAt" TEXT NOT NULL,
    "updatedAt" TEXT NOT NULL,
    FOREIGN KEY ("userId") REFERENCES "user"("id") ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS "idx_tenant_userId" ON "tenant"("userId");
