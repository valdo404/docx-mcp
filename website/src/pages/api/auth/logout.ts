import type { APIRoute } from 'astro';
import { createAuth } from '../../../lib/auth';
import { env } from 'cloudflare:workers';

export const prerender = false;

export const POST: APIRoute = async ({ request, redirect }) => {
  const auth = createAuth(env as unknown as Env);
  await auth.api.signOut({ headers: request.headers });
  return redirect('/');
};
