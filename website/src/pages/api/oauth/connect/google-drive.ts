import type { APIRoute } from 'astro';

export const prerender = false;

export const GET: APIRoute = async (context) => {
  const tenant = context.locals.tenant;
  if (!tenant) {
    return new Response(JSON.stringify({ error: 'Unauthorized' }), {
      status: 401,
      headers: { 'Content-Type': 'application/json' },
    });
  }

  const { env } = await import('cloudflare:workers');
  const typedEnv = env as unknown as Env;

  const clientId = typedEnv.OAUTH_GOOGLE_CLIENT_ID;
  if (!clientId) {
    return new Response(
      JSON.stringify({ error: 'Google OAuth not configured' }),
      { status: 500, headers: { 'Content-Type': 'application/json' } },
    );
  }

  const state = crypto.randomUUID();

  const redirectUri = new URL(
    '/api/oauth/callback/google-drive',
    typedEnv.BETTER_AUTH_URL,
  ).toString();

  const params = new URLSearchParams({
    client_id: clientId,
    redirect_uri: redirectUri,
    response_type: 'code',
    scope: 'https://www.googleapis.com/auth/drive.file https://www.googleapis.com/auth/userinfo.email',
    access_type: 'offline',
    prompt: 'consent',
    state,
    include_granted_scopes: 'true',
  });

  const url = context.request.url;
  const lang = new URL(url).pathname.startsWith('/en/') ? 'en' : 'fr';

  // Store state in KV for CSRF validation (5 min TTL)
  const kvKey = `oauth_state:${state}`;
  await typedEnv.SESSION.put(
    kvKey,
    JSON.stringify({ tenantId: tenant.id, lang }),
    { expirationTtl: 300 },
  );

  return context.redirect(
    `https://accounts.google.com/o/oauth2/v2/auth?${params.toString()}`,
  );
};
