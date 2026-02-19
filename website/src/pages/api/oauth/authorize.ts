import type { APIRoute } from 'astro';
import {
  getClient,
  validateRedirectUri,
  createAuthorizationCode,
} from '../../../lib/oauth-server';

export const prerender = false;

// GET /api/oauth/authorize — Authorization endpoint
// Requires Better Auth session (user must be logged in)
// On GET without consent: redirect to consent page
// On POST (consent granted): generate code and redirect
export const GET: APIRoute = async (context) => {
  const url = new URL(context.request.url);
  const { env } = await import('cloudflare:workers');
  const db = (env as unknown as Env).DB;

  // Extract OAuth params
  const clientId = url.searchParams.get('client_id');
  const redirectUri = url.searchParams.get('redirect_uri');
  const responseType = url.searchParams.get('response_type');
  const codeChallenge = url.searchParams.get('code_challenge');
  const codeChallengeMethod = url.searchParams.get('code_challenge_method');
  const scope = url.searchParams.get('scope') ?? 'mcp:tools';
  const state = url.searchParams.get('state');
  const resource = url.searchParams.get('resource');

  // Validate required params
  if (!clientId || !redirectUri || !responseType || !codeChallenge) {
    return new Response(
      JSON.stringify({
        error: 'invalid_request',
        error_description: 'Missing required parameters: client_id, redirect_uri, response_type, code_challenge',
      }),
      { status: 400, headers: { 'Content-Type': 'application/json' } },
    );
  }

  if (responseType !== 'code') {
    return new Response(
      JSON.stringify({
        error: 'unsupported_response_type',
        error_description: 'Only response_type=code is supported',
      }),
      { status: 400, headers: { 'Content-Type': 'application/json' } },
    );
  }

  if (codeChallengeMethod && codeChallengeMethod !== 'S256') {
    return new Response(
      JSON.stringify({
        error: 'invalid_request',
        error_description: 'Only code_challenge_method=S256 is supported',
      }),
      { status: 400, headers: { 'Content-Type': 'application/json' } },
    );
  }

  // Validate client
  const client = await getClient(db, clientId);
  if (!client) {
    return new Response(
      JSON.stringify({
        error: 'invalid_client',
        error_description: 'Unknown client_id',
      }),
      { status: 400, headers: { 'Content-Type': 'application/json' } },
    );
  }

  // Validate redirect_uri
  if (!validateRedirectUri(client, redirectUri)) {
    return new Response(
      JSON.stringify({
        error: 'invalid_redirect_uri',
        error_description: 'redirect_uri not registered for this client',
      }),
      { status: 400, headers: { 'Content-Type': 'application/json' } },
    );
  }

  // Check if user is logged in (middleware ensures session for /api/oauth/* routes)
  if (!context.locals.user || !context.locals.tenant) {
    // Redirect to login, preserving the full authorize URL as return_to
    const lang = url.pathname.startsWith('/en/') ? 'en' : 'fr';
    const loginPath = lang === 'fr' ? '/connexion' : '/en/login';
    const returnTo = encodeURIComponent(url.pathname + url.search);
    return context.redirect(`${loginPath}?return_to=${returnTo}`);
  }

  // User is logged in — redirect to consent page with all params
  const consentParams = new URLSearchParams({
    client_id: clientId,
    redirect_uri: redirectUri,
    scope,
    code_challenge: codeChallenge,
    code_challenge_method: codeChallengeMethod ?? 'S256',
    resource: resource ?? '',
    ...(state ? { state } : {}),
    client_name: client.clientName,
  });

  return context.redirect(`/consent?${consentParams.toString()}`);
};

// POST /api/oauth/authorize — Consent granted, generate code and redirect
export const POST: APIRoute = async (context) => {
  const { env } = await import('cloudflare:workers');
  const db = (env as unknown as Env).DB;

  // Must be logged in
  if (!context.locals.user || !context.locals.tenant) {
    return new Response(
      JSON.stringify({ error: 'unauthorized' }),
      { status: 401, headers: { 'Content-Type': 'application/json' } },
    );
  }

  let body: Record<string, string>;
  const contentType = context.request.headers.get('content-type') ?? '';
  if (contentType.includes('application/x-www-form-urlencoded')) {
    const formData = await context.request.formData();
    body = Object.fromEntries(formData.entries()) as Record<string, string>;
  } else {
    try {
      body = await context.request.json();
    } catch {
      return new Response(
        JSON.stringify({ error: 'invalid_request', error_description: 'Invalid body' }),
        { status: 400, headers: { 'Content-Type': 'application/json' } },
      );
    }
  }

  const { client_id, redirect_uri, scope, code_challenge, resource, state, action } = body;

  // Handle deny
  if (action === 'deny') {
    const params = new URLSearchParams({
      error: 'access_denied',
      error_description: 'The user denied the authorization request',
      ...(state ? { state } : {}),
    });
    return context.redirect(`${redirect_uri}?${params.toString()}`);
  }

  // Validate client
  const client = await getClient(db, client_id);
  if (!client) {
    return new Response(
      JSON.stringify({ error: 'invalid_client' }),
      { status: 400, headers: { 'Content-Type': 'application/json' } },
    );
  }

  if (!validateRedirectUri(client, redirect_uri)) {
    return new Response(
      JSON.stringify({ error: 'invalid_redirect_uri' }),
      { status: 400, headers: { 'Content-Type': 'application/json' } },
    );
  }

  // Generate authorization code
  const code = await createAuthorizationCode(
    db,
    client_id,
    context.locals.tenant.id,
    redirect_uri,
    scope ?? 'mcp:tools',
    code_challenge,
    resource ?? '',
  );

  // Redirect back to client with code
  const params = new URLSearchParams({
    code,
    ...(state ? { state } : {}),
  });

  return context.redirect(`${redirect_uri}?${params.toString()}`);
};
