import type { APIRoute } from 'astro';
import { exchangeCode, refreshAccessToken, OAuthError } from '../../../lib/oauth-server';

export const prerender = false;

// POST /api/oauth/token — Token endpoint
// No session auth required (clients send client_id in body)
export const POST: APIRoute = async (context) => {
  console.log('[OAuth Token] POST /api/oauth/token');
  console.log('[OAuth Token] Origin:', context.request.headers.get('origin'), 'Content-Type:', context.request.headers.get('content-type'));
  const { env } = await import('cloudflare:workers');
  const db = (env as unknown as Env).DB;

  let params: Record<string, string>;
  const contentType = context.request.headers.get('content-type') ?? '';
  if (contentType.includes('application/x-www-form-urlencoded')) {
    const formData = await context.request.formData();
    params = Object.fromEntries(formData.entries()) as Record<string, string>;
  } else {
    try {
      params = await context.request.json();
    } catch {
      return new Response(
        JSON.stringify({
          error: 'invalid_request',
          error_description: 'Invalid body',
        }),
        { status: 400, headers: { 'Content-Type': 'application/json' } },
      );
    }
  }

  const grantType = params.grant_type;

  try {
    if (grantType === 'authorization_code') {
      const { code, client_id, redirect_uri, code_verifier } = params;
      console.log('[OAuth Token] authorization_code grant — client_id:', client_id, 'redirect_uri:', redirect_uri, 'code:', code?.substring(0, 12) + '...', 'code_verifier present:', !!code_verifier);

      if (!code || !client_id || !redirect_uri || !code_verifier) {
        return new Response(
          JSON.stringify({
            error: 'invalid_request',
            error_description: 'Missing required parameters: code, client_id, redirect_uri, code_verifier',
          }),
          {
            status: 400,
            headers: {
              'Content-Type': 'application/json',
              'Cache-Control': 'no-store',
            },
          },
        );
      }

      const result = await exchangeCode(db, code, client_id, redirect_uri, code_verifier);

      console.log('[OAuth Token] Token issued successfully, access_token prefix:', result.access_token?.substring(0, 12));
      return new Response(JSON.stringify(result), {
        status: 200,
        headers: {
          'Content-Type': 'application/json',
          'Cache-Control': 'no-store',
        },
      });
    }

    if (grantType === 'refresh_token') {
      const { refresh_token, client_id } = params;

      if (!refresh_token || !client_id) {
        return new Response(
          JSON.stringify({
            error: 'invalid_request',
            error_description: 'Missing required parameters: refresh_token, client_id',
          }),
          {
            status: 400,
            headers: {
              'Content-Type': 'application/json',
              'Cache-Control': 'no-store',
            },
          },
        );
      }

      const result = await refreshAccessToken(db, refresh_token, client_id);

      return new Response(JSON.stringify(result), {
        status: 200,
        headers: {
          'Content-Type': 'application/json',
          'Cache-Control': 'no-store',
        },
      });
    }

    return new Response(
      JSON.stringify({
        error: 'unsupported_grant_type',
        error_description: `Unsupported grant_type: ${grantType}`,
      }),
      {
        status: 400,
        headers: {
          'Content-Type': 'application/json',
          'Cache-Control': 'no-store',
        },
      },
    );
  } catch (e) {
    if (e instanceof OAuthError) {
      console.error('[OAuth Token] OAuthError:', e.code, e.message);
      return new Response(JSON.stringify(e.toJSON()), {
        status: 400,
        headers: {
          'Content-Type': 'application/json',
          'Cache-Control': 'no-store',
        },
      });
    }
    console.error('Token endpoint error:', e);
    return new Response(
      JSON.stringify({
        error: 'server_error',
        error_description: 'Internal server error',
      }),
      {
        status: 500,
        headers: {
          'Content-Type': 'application/json',
          'Cache-Control': 'no-store',
        },
      },
    );
  }
};
