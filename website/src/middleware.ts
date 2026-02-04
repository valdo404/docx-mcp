import { defineMiddleware } from 'astro:middleware';

export const onRequest = defineMiddleware(async (context, next) => {
  const url = new URL(context.request.url);

  const isProtectedRoute =
    url.pathname.startsWith('/tableau-de-bord') ||
    url.pathname.startsWith('/en/dashboard');
  const isAuthRoute = url.pathname.startsWith('/api/auth');

  // Skip for static pages (landing, etc.)
  if (!isProtectedRoute && !isAuthRoute) {
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

  // Provision tenant on protected routes
  if (isProtectedRoute && session) {
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
