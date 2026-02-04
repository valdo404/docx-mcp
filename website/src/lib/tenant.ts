import { Kysely } from 'kysely';
import { D1Dialect } from 'kysely-d1';
import { createTenantPrefix } from './gcs';

interface TenantRecord {
  id: string;
  userId: string;
  displayName: string | null;
  gcsPrefix: string;
  storageQuotaBytes: number;
  storageUsedBytes: number;
  createdAt: string;
  updatedAt: string;
}

export type { TenantRecord };

export async function getOrCreateTenant(
  db: D1Database,
  userId: string,
  userName: string,
  gcsServiceAccountKey: string,
  gcsBucketName: string,
): Promise<TenantRecord> {
  const kysely = new Kysely<{ tenant: TenantRecord }>({
    dialect: new D1Dialect({ database: db }),
  });

  const existing = await kysely
    .selectFrom('tenant')
    .selectAll()
    .where('userId', '=', userId)
    .executeTakeFirst();

  if (existing) return existing;

  const now = new Date().toISOString();
  const tenantId = crypto.randomUUID();
  const gcsPrefix = `tenant-${userId}/`;

  const tenant: TenantRecord = {
    id: tenantId,
    userId,
    displayName: userName,
    gcsPrefix,
    storageQuotaBytes: 104857600, // 100 MB
    storageUsedBytes: 0,
    createdAt: now,
    updatedAt: now,
  };

  await kysely.insertInto('tenant').values(tenant).execute();

  await createTenantPrefix(gcsServiceAccountKey, gcsBucketName, gcsPrefix, {
    tenantId,
    userId,
    createdAt: now,
  });

  return tenant;
}

export async function getTenant(
  db: D1Database,
  userId: string,
): Promise<TenantRecord | undefined> {
  const kysely = new Kysely<{ tenant: TenantRecord }>({
    dialect: new D1Dialect({ database: db }),
  });

  return kysely
    .selectFrom('tenant')
    .selectAll()
    .where('userId', '=', userId)
    .executeTakeFirst();
}
