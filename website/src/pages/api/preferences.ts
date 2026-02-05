import type { APIRoute } from 'astro';
import { Kysely } from 'kysely';
import { D1Dialect } from 'kysely-d1';

export const prerender = false;

interface TenantRecord {
  id: string;
  preferences: string | null;
}

function getKysely(db: D1Database) {
  return new Kysely<{ tenant: TenantRecord }>({
    dialect: new D1Dialect({ database: db }),
  });
}

// GET /api/preferences - Get user preferences
export const GET: APIRoute = async (context) => {
  const tenant = context.locals.tenant;
  if (!tenant) {
    return new Response(JSON.stringify({ error: 'Unauthorized' }), {
      status: 401,
      headers: { 'Content-Type': 'application/json' },
    });
  }

  const preferences = tenant.preferences ? JSON.parse(tenant.preferences) : {};

  return new Response(JSON.stringify({ preferences }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
};

// PATCH /api/preferences - Update user preferences
export const PATCH: APIRoute = async (context) => {
  const tenant = context.locals.tenant;
  if (!tenant) {
    return new Response(JSON.stringify({ error: 'Unauthorized' }), {
      status: 401,
      headers: { 'Content-Type': 'application/json' },
    });
  }

  let body: Record<string, unknown>;
  try {
    body = await context.request.json();
  } catch {
    return new Response(JSON.stringify({ error: 'Invalid JSON' }), {
      status: 400,
      headers: { 'Content-Type': 'application/json' },
    });
  }

  const { env } = await import('cloudflare:workers');
  const kysely = getKysely((env as unknown as Env).DB);

  // Merge with existing preferences
  const existingPrefs = tenant.preferences ? JSON.parse(tenant.preferences) : {};
  const newPrefs = { ...existingPrefs, ...body };

  await kysely
    .updateTable('tenant')
    .set({ preferences: JSON.stringify(newPrefs) })
    .where('id', '=', tenant.id)
    .execute();

  return new Response(JSON.stringify({ preferences: newPrefs }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
};
