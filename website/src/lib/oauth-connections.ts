import { Kysely } from 'kysely';
import { D1Dialect } from 'kysely-d1';

interface OAuthConnectionRecord {
  id: string;
  tenantId: string;
  provider: string;
  displayName: string;
  providerAccountId: string | null;
  accessToken: string;
  refreshToken: string;
  tokenExpiresAt: string | null;
  scopes: string;
  createdAt: string;
  updatedAt: string;
}

export interface OAuthConnectionInfo {
  id: string;
  provider: string;
  displayName: string;
  providerAccountId: string | null;
  scopes: string;
  tokenExpiresAt: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface CreateConnectionParams {
  provider: string;
  displayName: string;
  providerAccountId: string | null;
  accessToken: string;
  refreshToken: string;
  tokenExpiresAt: string | null;
  scopes: string;
}

function getKysely(db: D1Database) {
  return new Kysely<{ oauth_connection: OAuthConnectionRecord }>({
    dialect: new D1Dialect({ database: db }),
  });
}

export async function createConnection(
  db: D1Database,
  tenantId: string,
  params: CreateConnectionParams,
): Promise<OAuthConnectionInfo> {
  const kysely = getKysely(db);
  const now = new Date().toISOString();
  const id = crypto.randomUUID();

  const record: OAuthConnectionRecord = {
    id,
    tenantId,
    provider: params.provider,
    displayName: params.displayName,
    providerAccountId: params.providerAccountId,
    accessToken: params.accessToken,
    refreshToken: params.refreshToken,
    tokenExpiresAt: params.tokenExpiresAt,
    scopes: params.scopes,
    createdAt: now,
    updatedAt: now,
  };

  await kysely.insertInto('oauth_connection').values(record).execute();

  return {
    id,
    provider: params.provider,
    displayName: params.displayName,
    providerAccountId: params.providerAccountId,
    scopes: params.scopes,
    tokenExpiresAt: params.tokenExpiresAt,
    createdAt: now,
    updatedAt: now,
  };
}

export async function listConnections(
  db: D1Database,
  tenantId: string,
  provider?: string,
): Promise<OAuthConnectionInfo[]> {
  const kysely = getKysely(db);

  let query = kysely
    .selectFrom('oauth_connection')
    .select([
      'id',
      'provider',
      'displayName',
      'providerAccountId',
      'scopes',
      'tokenExpiresAt',
      'createdAt',
      'updatedAt',
    ])
    .where('tenantId', '=', tenantId);

  if (provider) {
    query = query.where('provider', '=', provider);
  }

  return await query.orderBy('createdAt', 'desc').execute();
}

export async function getConnection(
  db: D1Database,
  connectionId: string,
): Promise<OAuthConnectionRecord | undefined> {
  const kysely = getKysely(db);

  return await kysely
    .selectFrom('oauth_connection')
    .selectAll()
    .where('id', '=', connectionId)
    .executeTakeFirst();
}

export async function deleteConnection(
  db: D1Database,
  tenantId: string,
  connectionId: string,
): Promise<boolean> {
  const kysely = getKysely(db);

  const result = await kysely
    .deleteFrom('oauth_connection')
    .where('id', '=', connectionId)
    .where('tenantId', '=', tenantId)
    .executeTakeFirst();

  return (result.numDeletedRows ?? 0) > 0;
}

export async function updateTokens(
  db: D1Database,
  connectionId: string,
  accessToken: string,
  refreshToken: string,
  expiresAt: string | null,
): Promise<void> {
  const kysely = getKysely(db);

  await kysely
    .updateTable('oauth_connection')
    .set({
      accessToken,
      refreshToken,
      tokenExpiresAt: expiresAt,
      updatedAt: new Date().toISOString(),
    })
    .where('id', '=', connectionId)
    .execute();
}
