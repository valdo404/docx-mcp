import { defineMiddleware } from 'astro:middleware';

export const onRequest = defineMiddleware(async (context, next) => {
  const url = new URL(context.request.url);

  const isProtectedRoute =
    url.pathname.startsWith('/tableau-de-bord') ||
    url.pathname.startsWith('/en/dashboard');
  const isPatRoute = url.pathname.startsWith('/api/pat');
  const isPreferencesRoute = url.pathname.startsWith('/api/preferences');
  const isAuthRoute = url.pathname.startsWith('/api/auth');
  const isConsentRoute =
    url.pathname === '/consent' || url.pathname === '/en/consent';

  // OAuth server routes that do NOT require session auth
  const isOAuthServerPublicRoute =
    url.pathname === '/api/oauth/register' ||
    url.pathname === '/api/oauth/token' ||
    url.pathname.startsWith('/.well-known/');
  // OAuth connection management routes (require auth)
  const isOAuthConnectionRoute =
    url.pathname.startsWith('/api/oauth') && !isOAuthServerPublicRoute;
  // OAuth authorize requires session but handles redirect to login itself
  const isOAuthAuthorizeRoute = url.pathname === '/api/oauth/authorize';

  // Skip for static pages, Better Auth routes, and public OAuth server endpoints
  if (
    !isProtectedRoute &&
    !isAuthRoute &&
    !isPatRoute &&
    !isPreferencesRoute &&
    !isOAuthConnectionRoute &&
    !isConsentRoute &&
    !isOAuthServerPublicRoute
  ) {
    return next();
  }

  // Public OAuth server endpoints — pass through without auth
  if (isOAuthServerPublicRoute) {
    return next();
  }

  // Dynamic import to avoid cloudflare:workers during prerender
  const { env } = await import('cloudflare:workers');
  const { createAuth } = await import('./lib/auth');

  const auth = createAuth(env as unknown as Env);
  const session = await auth.api.getSession({
    headers: context.request.headers,
  });

  if (session) {
    context.locals.user = session.user;
    context.locals.session = session.session;
  }

  // Redirect to login if not authenticated on protected route
  if (isProtectedRoute && !session) {
    const lang = url.pathname.startsWith('/en/') ? 'en' : 'fr';
    const loginPath = lang === 'fr' ? '/connexion' : '/en/login';
    return context.redirect(loginPath);
  }

  // OAuth authorize: if not logged in, redirect to login with return_to
  if ((isOAuthAuthorizeRoute || isConsentRoute) && !session) {
    const lang = url.pathname.startsWith('/en/') ? 'en' : 'fr';
    const loginPath = lang === 'fr' ? '/connexion' : '/en/login';
    const returnTo = encodeURIComponent(url.pathname + url.search);
    return context.redirect(`${loginPath}?return_to=${returnTo}`);
  }

  // Return 401 for API routes without auth (PAT, preferences, OAuth connections)
  if ((isPatRoute || isPreferencesRoute || (isOAuthConnectionRoute && !isOAuthAuthorizeRoute)) && !session) {
    return new Response(JSON.stringify({ error: 'Unauthorized' }), {
      status: 401,
      headers: { 'Content-Type': 'application/json' },
    });
  }

  // Provision tenant on protected routes and API routes
  if (
    (isProtectedRoute ||
      isPatRoute ||
      isPreferencesRoute ||
      isOAuthConnectionRoute ||
      isConsentRoute) &&
    session
  ) {
    const { getOrCreateTenant } = await import('./lib/tenant');
    const typedEnv = env as unknown as Env;
    const tenant = await getOrCreateTenant(
      typedEnv.DB,
      session.user.id,
      session.user.name,
      typedEnv.GCS_SERVICE_ACCOUNT_KEY,
      typedEnv.GCS_BUCKET_NAME,
    );
    context.locals.tenant = tenant;
  }

  return next();
});
