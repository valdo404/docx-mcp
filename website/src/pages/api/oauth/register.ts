import type { APIRoute } from 'astro';
import {
  registerClient,
  OAuthError,
  type RegisterClientParams,
} from '../../../lib/oauth-server';

export const prerender = false;

// POST /api/oauth/register — Dynamic Client Registration (RFC 7591)
// No auth required
export const POST: APIRoute = async (context) => {
  console.log('[OAuth DCR] POST /api/oauth/register');
  let body: RegisterClientParams;
  try {
    body = await context.request.json();
  } catch {
    return new Response(
      JSON.stringify({
        error: 'invalid_client_metadata',
        error_description: 'Invalid JSON body',
      }),
      { status: 400, headers: { 'Content-Type': 'application/json' } },
    );
  }

  // Validate required fields
  if (!body.client_name?.trim()) {
    return new Response(
      JSON.stringify({
        error: 'invalid_client_metadata',
        error_description: 'client_name is required',
      }),
      { status: 400, headers: { 'Content-Type': 'application/json' } },
    );
  }

  if (!Array.isArray(body.redirect_uris) || body.redirect_uris.length === 0) {
    return new Response(
      JSON.stringify({
        error: 'invalid_client_metadata',
        error_description: 'redirect_uris must be a non-empty array',
      }),
      { status: 400, headers: { 'Content-Type': 'application/json' } },
    );
  }

  // Validate each redirect URI
  for (const uri of body.redirect_uris) {
    try {
      const parsed = new URL(uri);
      // Allow http only for localhost
      if (
        parsed.protocol === 'http:' &&
        parsed.hostname !== 'localhost' &&
        parsed.hostname !== '127.0.0.1'
      ) {
        return new Response(
          JSON.stringify({
            error: 'invalid_redirect_uri',
            error_description: `Non-localhost redirect_uri must use HTTPS: ${uri}`,
          }),
          { status: 400, headers: { 'Content-Type': 'application/json' } },
        );
      }
    } catch {
      return new Response(
        JSON.stringify({
          error: 'invalid_redirect_uri',
          error_description: `Invalid redirect_uri: ${uri}`,
        }),
        { status: 400, headers: { 'Content-Type': 'application/json' } },
      );
    }
  }

  // Validate grant_types if provided
  const allowedGrants = ['authorization_code', 'refresh_token'];
  if (body.grant_types) {
    for (const gt of body.grant_types) {
      if (!allowedGrants.includes(gt)) {
        return new Response(
          JSON.stringify({
            error: 'invalid_client_metadata',
            error_description: `Unsupported grant_type: ${gt}`,
          }),
          { status: 400, headers: { 'Content-Type': 'application/json' } },
        );
      }
    }
  }

  // Validate token_endpoint_auth_method if provided
  const allowedAuthMethods = ['none', 'client_secret_post'];
  if (
    body.token_endpoint_auth_method &&
    !allowedAuthMethods.includes(body.token_endpoint_auth_method)
  ) {
    return new Response(
      JSON.stringify({
        error: 'invalid_client_metadata',
        error_description: `Unsupported token_endpoint_auth_method: ${body.token_endpoint_auth_method}`,
      }),
      { status: 400, headers: { 'Content-Type': 'application/json' } },
    );
  }

  console.log('[OAuth DCR] Registering client:', body.client_name, 'redirect_uris:', body.redirect_uris);

  try {
    const { env } = await import('cloudflare:workers');
    const client = await registerClient((env as unknown as Env).DB, body);

    console.log('[OAuth DCR] Client registered:', client.id);
    return new Response(
      JSON.stringify({
        client_id: client.id,
        client_name: client.clientName,
        redirect_uris: JSON.parse(client.redirectUris),
        grant_types: JSON.parse(client.grantTypes),
        token_endpoint_auth_method: client.tokenEndpointAuthMethod,
        client_uri: client.clientUri,
        logo_uri: client.logoUri,
      }),
      {
        status: 201,
        headers: { 'Content-Type': 'application/json' },
      },
    );
  } catch (e) {
    if (e instanceof OAuthError) {
      return new Response(JSON.stringify(e.toJSON()), {
        status: 400,
        headers: { 'Content-Type': 'application/json' },
      });
    }
    console.error('DCR error:', e);
    return new Response(
      JSON.stringify({
        error: 'server_error',
        error_description: 'Internal server error',
      }),
      { status: 500, headers: { 'Content-Type': 'application/json' } },
    );
  }
};
