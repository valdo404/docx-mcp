import type { APIRoute } from 'astro';

export const prerender = false;

export const GET: APIRoute = async (context) => {
  const url = new URL(context.request.url);
  const code = url.searchParams.get('code');
  const state = url.searchParams.get('state');
  const error = url.searchParams.get('error');

  const { env } = await import('cloudflare:workers');
  const typedEnv = env as unknown as Env;

  // Handle OAuth errors
  if (error) {
    const lang = url.pathname.startsWith('/en/') ? 'en' : 'fr';
    const dashboardPath =
      lang === 'fr' ? '/tableau-de-bord' : '/en/dashboard';
    return context.redirect(
      `${dashboardPath}?oauth_error=${encodeURIComponent(error)}`,
    );
  }

  if (!code || !state) {
    return new Response(
      JSON.stringify({ error: 'Missing code or state parameter' }),
      { status: 400, headers: { 'Content-Type': 'application/json' } },
    );
  }

  // Validate state (CSRF protection)
  const kvKey = `oauth_state:${state}`;
  const stateData = await typedEnv.SESSION.get(kvKey);
  if (!stateData) {
    return new Response(
      JSON.stringify({ error: 'Invalid or expired state' }),
      { status: 400, headers: { 'Content-Type': 'application/json' } },
    );
  }

  // Delete the state to prevent replay
  await typedEnv.SESSION.delete(kvKey);

  const { tenantId, lang } = JSON.parse(stateData) as {
    tenantId: string;
    lang: string;
  };

  const dashboardPath =
    lang === 'fr' ? '/tableau-de-bord' : '/en/dashboard';

  // Exchange code for tokens
  const redirectUri = new URL(
    '/api/oauth/callback/google-drive',
    typedEnv.BETTER_AUTH_URL,
  ).toString();

  const tokenResponse = await fetch('https://oauth2.googleapis.com/token', {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({
      code,
      client_id: typedEnv.OAUTH_GOOGLE_CLIENT_ID,
      client_secret: typedEnv.OAUTH_GOOGLE_CLIENT_SECRET,
      redirect_uri: redirectUri,
      grant_type: 'authorization_code',
    }),
  });

  if (!tokenResponse.ok) {
    const errorBody = await tokenResponse.text();
    console.error('Token exchange failed:', errorBody);
    return context.redirect(
      `${dashboardPath}?oauth_error=token_exchange_failed`,
    );
  }

  const tokens = (await tokenResponse.json()) as {
    access_token: string;
    refresh_token?: string;
    expires_in: number;
    scope: string;
  };

  if (!tokens.refresh_token) {
    return context.redirect(
      `${dashboardPath}?oauth_error=no_refresh_token`,
    );
  }

  // Get the Google account email for display
  const userinfoResponse = await fetch(
    'https://www.googleapis.com/oauth2/v2/userinfo',
    { headers: { Authorization: `Bearer ${tokens.access_token}` } },
  );

  let email = 'Google Drive';
  if (userinfoResponse.ok) {
    const userinfo = (await userinfoResponse.json()) as { email?: string };
    if (userinfo.email) {
      email = userinfo.email;
    }
  }

  // Store the connection in D1
  const { createConnection } = await import(
    '@/lib/oauth-connections'
  );

  const expiresAt = new Date(
    Date.now() + tokens.expires_in * 1000,
  ).toISOString();

  await createConnection(typedEnv.DB, tenantId, {
    provider: 'google_drive',
    displayName: email,
    providerAccountId: email,
    accessToken: tokens.access_token,
    refreshToken: tokens.refresh_token,
    tokenExpiresAt: expiresAt,
    scopes: tokens.scope,
  });

  return context.redirect(`${dashboardPath}?oauth_success=google_drive`);
};
