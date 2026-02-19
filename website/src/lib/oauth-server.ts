import { Kysely } from 'kysely';
import { D1Dialect } from 'kysely-d1';

// --- Types ---

interface OAuthClientRecord {
  id: string;
  clientName: string;
  redirectUris: string; // JSON array
  grantTypes: string; // JSON array
  tokenEndpointAuthMethod: string;
  clientSecret: string | null;
  clientUri: string | null;
  logoUri: string | null;
  createdAt: string;
  updatedAt: string;
}

interface OAuthAuthorizationCodeRecord {
  code: string;
  clientId: string;
  tenantId: string;
  redirectUri: string;
  scope: string;
  codeChallenge: string;
  resource: string;
  expiresAt: string;
  createdAt: string;
}

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

interface OAuthDB {
  oauth_client: OAuthClientRecord;
  oauth_authorization_code: OAuthAuthorizationCodeRecord;
  oauth_access_token: OAuthAccessTokenRecord;
  oauth_refresh_token: OAuthRefreshTokenRecord;
}

export interface RegisterClientParams {
  client_name: string;
  redirect_uris: string[];
  grant_types?: string[];
  response_types?: string[];
  token_endpoint_auth_method?: string;
  client_uri?: string;
  logo_uri?: string;
}

export interface TokenResponse {
  access_token: string;
  refresh_token: string;
  token_type: string;
  expires_in: number;
  scope: string;
}

// --- Constants ---

const ACCESS_TOKEN_PREFIX = 'oat_';
const REFRESH_TOKEN_PREFIX = 'ort_';
const ACCESS_TOKEN_TTL_SECONDS = 3600; // 1 hour
const REFRESH_TOKEN_TTL_SECONDS = 30 * 24 * 3600; // 30 days
const AUTHORIZATION_CODE_TTL_SECONDS = 300; // 5 minutes

// --- Helpers ---

function getKysely(db: D1Database) {
  return new Kysely<OAuthDB>({
    dialect: new D1Dialect({ database: db }),
  });
}

export function generateOpaqueToken(prefix: string): string {
  const bytes = new Uint8Array(32);
  crypto.getRandomValues(bytes);
  const randomPart = Array.from(bytes)
    .map((b) => b.toString(16).padStart(2, '0'))
    .join('');
  return `${prefix}${randomPart}`;
}

export async function hashToken(token: string): Promise<string> {
  const encoder = new TextEncoder();
  const data = encoder.encode(token);
  const hashBuffer = await crypto.subtle.digest('SHA-256', data);
  const hashArray = Array.from(new Uint8Array(hashBuffer));
  return hashArray.map((b) => b.toString(16).padStart(2, '0')).join('');
}

export async function verifyPkce(
  codeVerifier: string,
  codeChallenge: string,
): Promise<boolean> {
  const encoder = new TextEncoder();
  const data = encoder.encode(codeVerifier);
  const hashBuffer = await crypto.subtle.digest('SHA-256', data);
  // Base64url encode (no padding)
  const hashBase64 = btoa(String.fromCharCode(...new Uint8Array(hashBuffer)))
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
    .replace(/=+$/, '');
  return hashBase64 === codeChallenge;
}

function normalizeRedirectUri(uri: string): string {
  // Treat localhost and 127.0.0.1 as equivalent
  try {
    const parsed = new URL(uri);
    if (parsed.hostname === '127.0.0.1') {
      parsed.hostname = 'localhost';
      return parsed.toString().replace(/\/$/, '');
    }
    return uri.replace(/\/$/, '');
  } catch {
    return uri;
  }
}

function redirectUrisMatch(registered: string, provided: string): boolean {
  return normalizeRedirectUri(registered) === normalizeRedirectUri(provided);
}

// --- Client Registration (RFC 7591) ---

export async function registerClient(
  db: D1Database,
  params: RegisterClientParams,
): Promise<OAuthClientRecord> {
  const kysely = getKysely(db);
  const now = new Date().toISOString();
  const id = crypto.randomUUID();

  const grantTypes = params.grant_types ?? ['authorization_code'];
  const authMethod = params.token_endpoint_auth_method ?? 'none';

  let clientSecret: string | null = null;
  if (authMethod === 'client_secret_post') {
    clientSecret = await hashToken(generateOpaqueToken('cs_'));
  }

  const record: OAuthClientRecord = {
    id,
    clientName: params.client_name,
    redirectUris: JSON.stringify(params.redirect_uris),
    grantTypes: JSON.stringify(grantTypes),
    tokenEndpointAuthMethod: authMethod,
    clientSecret,
    clientUri: params.client_uri ?? null,
    logoUri: params.logo_uri ?? null,
    createdAt: now,
    updatedAt: now,
  };

  await kysely.insertInto('oauth_client').values(record).execute();

  return record;
}

export async function getClient(
  db: D1Database,
  clientId: string,
): Promise<OAuthClientRecord | undefined> {
  const kysely = getKysely(db);
  return await kysely
    .selectFrom('oauth_client')
    .selectAll()
    .where('id', '=', clientId)
    .executeTakeFirst();
}

// --- Authorization Code ---

export async function createAuthorizationCode(
  db: D1Database,
  clientId: string,
  tenantId: string,
  redirectUri: string,
  scope: string,
  codeChallenge: string,
  resource: string,
): Promise<string> {
  const kysely = getKysely(db);
  const code = generateOpaqueToken('');
  const now = new Date();
  const expiresAt = new Date(now.getTime() + AUTHORIZATION_CODE_TTL_SECONDS * 1000);

  const record: OAuthAuthorizationCodeRecord = {
    code,
    clientId,
    tenantId,
    redirectUri,
    scope,
    codeChallenge,
    resource,
    expiresAt: expiresAt.toISOString(),
    createdAt: now.toISOString(),
  };

  await kysely.insertInto('oauth_authorization_code').values(record).execute();

  return code;
}

// --- Token Exchange (authorization_code) ---

export async function exchangeCode(
  db: D1Database,
  code: string,
  clientId: string,
  redirectUri: string,
  codeVerifier: string,
): Promise<TokenResponse> {
  const kysely = getKysely(db);

  // 1. Find and validate the authorization code
  const authCode = await kysely
    .selectFrom('oauth_authorization_code')
    .selectAll()
    .where('code', '=', code)
    .executeTakeFirst();

  if (!authCode) {
    throw new OAuthError('invalid_grant', 'Authorization code not found');
  }

  if (new Date(authCode.expiresAt) < new Date()) {
    // Clean up expired code
    await kysely
      .deleteFrom('oauth_authorization_code')
      .where('code', '=', code)
      .execute();
    throw new OAuthError('invalid_grant', 'Authorization code expired');
  }

  if (authCode.clientId !== clientId) {
    throw new OAuthError('invalid_grant', 'Client ID mismatch');
  }

  if (!redirectUrisMatch(authCode.redirectUri, redirectUri)) {
    throw new OAuthError('invalid_grant', 'Redirect URI mismatch');
  }

  // 2. Verify PKCE
  const pkceValid = await verifyPkce(codeVerifier, authCode.codeChallenge);
  if (!pkceValid) {
    throw new OAuthError('invalid_grant', 'PKCE verification failed');
  }

  // 3. Delete the code (one-time use)
  await kysely
    .deleteFrom('oauth_authorization_code')
    .where('code', '=', code)
    .execute();

  // 4. Generate tokens
  const now = new Date();
  const accessToken = generateOpaqueToken(ACCESS_TOKEN_PREFIX);
  const refreshToken = generateOpaqueToken(REFRESH_TOKEN_PREFIX);

  const accessTokenHash = await hashToken(accessToken);
  const refreshTokenHash = await hashToken(refreshToken);

  const accessTokenRecord: OAuthAccessTokenRecord = {
    id: crypto.randomUUID(),
    clientId,
    tenantId: authCode.tenantId,
    tokenHash: accessTokenHash,
    tokenPrefix: accessToken.slice(0, 12),
    scope: authCode.scope,
    resource: authCode.resource,
    expiresAt: new Date(now.getTime() + ACCESS_TOKEN_TTL_SECONDS * 1000).toISOString(),
    createdAt: now.toISOString(),
    lastUsedAt: null,
  };

  const refreshTokenRecord: OAuthRefreshTokenRecord = {
    id: crypto.randomUUID(),
    clientId,
    tenantId: authCode.tenantId,
    tokenHash: refreshTokenHash,
    scope: authCode.scope,
    resource: authCode.resource,
    expiresAt: new Date(now.getTime() + REFRESH_TOKEN_TTL_SECONDS * 1000).toISOString(),
    createdAt: now.toISOString(),
    revoked: 0,
  };

  await kysely.insertInto('oauth_access_token').values(accessTokenRecord).execute();
  await kysely.insertInto('oauth_refresh_token').values(refreshTokenRecord).execute();

  return {
    access_token: accessToken,
    refresh_token: refreshToken,
    token_type: 'Bearer',
    expires_in: ACCESS_TOKEN_TTL_SECONDS,
    scope: authCode.scope,
  };
}

// --- Token Refresh ---

export async function refreshAccessToken(
  db: D1Database,
  refreshTokenStr: string,
  clientId: string,
): Promise<TokenResponse> {
  const kysely = getKysely(db);
  const tokenHash = await hashToken(refreshTokenStr);

  // 1. Find and validate the refresh token
  const refreshRecord = await kysely
    .selectFrom('oauth_refresh_token')
    .selectAll()
    .where('tokenHash', '=', tokenHash)
    .executeTakeFirst();

  if (!refreshRecord) {
    throw new OAuthError('invalid_grant', 'Refresh token not found');
  }

  if (refreshRecord.revoked) {
    throw new OAuthError('invalid_grant', 'Refresh token has been revoked');
  }

  if (new Date(refreshRecord.expiresAt) < new Date()) {
    throw new OAuthError('invalid_grant', 'Refresh token expired');
  }

  if (refreshRecord.clientId !== clientId) {
    throw new OAuthError('invalid_grant', 'Client ID mismatch');
  }

  // 2. Revoke the old refresh token (rotation)
  await kysely
    .updateTable('oauth_refresh_token')
    .set({ revoked: 1 })
    .where('id', '=', refreshRecord.id)
    .execute();

  // 3. Generate new tokens
  const now = new Date();
  const newAccessToken = generateOpaqueToken(ACCESS_TOKEN_PREFIX);
  const newRefreshToken = generateOpaqueToken(REFRESH_TOKEN_PREFIX);

  const accessTokenHash = await hashToken(newAccessToken);
  const refreshTokenHash = await hashToken(newRefreshToken);

  const accessTokenRecord: OAuthAccessTokenRecord = {
    id: crypto.randomUUID(),
    clientId,
    tenantId: refreshRecord.tenantId,
    tokenHash: accessTokenHash,
    tokenPrefix: newAccessToken.slice(0, 12),
    scope: refreshRecord.scope,
    resource: refreshRecord.resource,
    expiresAt: new Date(now.getTime() + ACCESS_TOKEN_TTL_SECONDS * 1000).toISOString(),
    createdAt: now.toISOString(),
    lastUsedAt: null,
  };

  const refreshTokenRecord: OAuthRefreshTokenRecord = {
    id: crypto.randomUUID(),
    clientId,
    tenantId: refreshRecord.tenantId,
    tokenHash: refreshTokenHash,
    scope: refreshRecord.scope,
    resource: refreshRecord.resource,
    expiresAt: new Date(now.getTime() + REFRESH_TOKEN_TTL_SECONDS * 1000).toISOString(),
    createdAt: now.toISOString(),
    revoked: 0,
  };

  await kysely.insertInto('oauth_access_token').values(accessTokenRecord).execute();
  await kysely.insertInto('oauth_refresh_token').values(refreshTokenRecord).execute();

  return {
    access_token: newAccessToken,
    refresh_token: newRefreshToken,
    token_type: 'Bearer',
    expires_in: ACCESS_TOKEN_TTL_SECONDS,
    scope: refreshRecord.scope,
  };
}

// --- Validation helpers for authorize endpoint ---

export function validateRedirectUri(
  client: OAuthClientRecord,
  redirectUri: string,
): boolean {
  const registeredUris: string[] = JSON.parse(client.redirectUris);
  return registeredUris.some((uri) => redirectUrisMatch(uri, redirectUri));
}

// --- Error ---

export class OAuthError extends Error {
  constructor(
    public code: string,
    message: string,
  ) {
    super(message);
    this.name = 'OAuthError';
  }

  toJSON() {
    return {
      error: this.code,
      error_description: this.message,
    };
  }
}
