import type { APIRoute } from 'astro';
import { createAuth } from '../../../lib/auth';
import { env } from 'cloudflare:workers';

export const prerender = false;

export const ALL: APIRoute = async ({ request }) => {
  const auth = createAuth(env as unknown as Env);
  return auth.handler(request);
};
