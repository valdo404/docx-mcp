import { Kysely } from 'kysely';
import { D1Dialect } from 'kysely-d1';

interface OAuthAccessTokenRecord {
  id: string;
  clientId: string;
  tenantId: string;
  tokenHash: string;
  tokenPrefix: string;
  scope: string;
  resource: string;
  expiresAt: string;
  createdAt: string;
  lastUsedAt: string | null;
}

interface OAuthRefreshTokenRecord {
  id: string;
  clientId: string;
  tenantId: string;
  tokenHash: string;
  scope: string;
  resource: string;
  expiresAt: string;
  createdAt: string;
  revoked: number;
}

interface OAuthClientRecord {
  id: string;
  clientName: string;
  redirectUris: string;
  grantTypes: string;
  tokenEndpointAuthMethod: string;
  clientSecret: string | null;
  clientUri: string | null;
  logoUri: string | null;
  createdAt: string;
  updatedAt: string;
}

interface OAuthDB {
  oauth_access_token: OAuthAccessTokenRecord;
  oauth_refresh_token: OAuthRefreshTokenRecord;
  oauth_client: OAuthClientRecord;
}

export interface OAuthAppInfo {
  clientId: string;
  clientName: string;
  scope: string;
  createdAt: string;
  lastUsedAt: string | null;
  activeTokens: number;
}

function getKysely(db: D1Database) {
  return new Kysely<OAuthDB>({
    dialect: new D1Dialect({ database: db }),
  });
}

export async function listAuthorizedApps(
  db: D1Database,
  tenantId: string,
): Promise<OAuthAppInfo[]> {
  const kysely = getKysely(db);

  // Get all active (non-expired) access tokens for this tenant, grouped by client
  const tokens = await kysely
    .selectFrom('oauth_access_token')
    .innerJoin('oauth_client', 'oauth_client.id', 'oauth_access_token.clientId')
    .select([
      'oauth_access_token.clientId',
      'oauth_client.clientName',
      'oauth_access_token.scope',
      'oauth_access_token.createdAt',
      'oauth_access_token.lastUsedAt',
      'oauth_access_token.expiresAt',
    ])
    .where('oauth_access_token.tenantId', '=', tenantId)
    .orderBy('oauth_access_token.createdAt', 'desc')
    .execute();

  // Group by clientId
  const appMap = new Map<string, OAuthAppInfo>();
  for (const token of tokens) {
    const existing = appMap.get(token.clientId);
    if (existing) {
      existing.activeTokens++;
      // Keep the most recent lastUsedAt
      if (
        token.lastUsedAt &&
        (!existing.lastUsedAt || token.lastUsedAt > existing.lastUsedAt)
      ) {
        existing.lastUsedAt = token.lastUsedAt;
      }
    } else {
      appMap.set(token.clientId, {
        clientId: token.clientId,
        clientName: token.clientName,
        scope: token.scope,
        createdAt: token.createdAt,
        lastUsedAt: token.lastUsedAt,
        activeTokens: 1,
      });
    }
  }

  return Array.from(appMap.values());
}

export async function revokeAppAccess(
  db: D1Database,
  tenantId: string,
  clientId: string,
): Promise<boolean> {
  const kysely = getKysely(db);

  // Delete all access tokens for this client + tenant
  await kysely
    .deleteFrom('oauth_access_token')
    .where('tenantId', '=', tenantId)
    .where('clientId', '=', clientId)
    .execute();

  // Revoke all refresh tokens for this client + tenant
  await kysely
    .updateTable('oauth_refresh_token')
    .set({ revoked: 1 })
    .where('tenantId', '=', tenantId)
    .where('clientId', '=', clientId)
    .execute();

  return true;
}
