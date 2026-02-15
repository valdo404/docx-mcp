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
  const { listConnections } = await import('../../lib/oauth-connections');

  const url = new URL(context.request.url);
  const provider = url.searchParams.get('provider') ?? undefined;

  const connections = await listConnections(typedEnv.DB, tenant.id, provider);

  return new Response(JSON.stringify(connections), {
    headers: { 'Content-Type': 'application/json' },
  });
};

export const DELETE: APIRoute = async (context) => {
  const tenant = context.locals.tenant;
  if (!tenant) {
    return new Response(JSON.stringify({ error: 'Unauthorized' }), {
      status: 401,
      headers: { 'Content-Type': 'application/json' },
    });
  }

  const { env } = await import('cloudflare:workers');
  const typedEnv = env as unknown as Env;
  const { deleteConnection } = await import('../../lib/oauth-connections');

  const body = (await context.request.json()) as { connectionId?: string };
  if (!body.connectionId) {
    return new Response(
      JSON.stringify({ error: 'connectionId is required' }),
      { status: 400, headers: { 'Content-Type': 'application/json' } },
    );
  }

  const deleted = await deleteConnection(
    typedEnv.DB,
    tenant.id,
    body.connectionId,
  );

  if (!deleted) {
    return new Response(
      JSON.stringify({ error: 'Connection not found' }),
      { status: 404, headers: { 'Content-Type': 'application/json' } },
    );
  }

  return new Response(JSON.stringify({ success: true }), {
    headers: { 'Content-Type': 'application/json' },
  });
};
