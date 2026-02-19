import type { APIRoute } from 'astro';
import { listAuthorizedApps, revokeAppAccess } from '../../../lib/oauth-apps';

export const prerender = false;

// GET /api/oauth/apps — List authorized OAuth apps for the current tenant
export const GET: APIRoute = async (context) => {
  const tenant = context.locals.tenant;
  if (!tenant) {
    return new Response(JSON.stringify({ error: 'Tenant not found' }), {
      status: 404,
      headers: { 'Content-Type': 'application/json' },
    });
  }

  const { env } = await import('cloudflare:workers');
  const apps = await listAuthorizedApps((env as unknown as Env).DB, tenant.id);

  return new Response(JSON.stringify({ apps }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
};

// DELETE /api/oauth/apps — Revoke all tokens for an OAuth app
export const DELETE: APIRoute = async (context) => {
  const tenant = context.locals.tenant;
  if (!tenant) {
    return new Response(JSON.stringify({ error: 'Tenant not found' }), {
      status: 404,
      headers: { 'Content-Type': 'application/json' },
    });
  }

  let body: { clientId?: string };
  try {
    body = await context.request.json();
  } catch {
    return new Response(JSON.stringify({ error: 'Invalid JSON' }), {
      status: 400,
      headers: { 'Content-Type': 'application/json' },
    });
  }

  if (!body.clientId) {
    return new Response(JSON.stringify({ error: 'clientId is required' }), {
      status: 400,
      headers: { 'Content-Type': 'application/json' },
    });
  }

  const { env } = await import('cloudflare:workers');
  await revokeAppAccess((env as unknown as Env).DB, tenant.id, body.clientId);

  return new Response(JSON.stringify({ ok: true }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
};
