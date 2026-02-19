/// <reference types="astro/client" />

type D1Database = import('@cloudflare/workers-types').D1Database;
type KVNamespace = import('@cloudflare/workers-types').KVNamespace;

interface Env {
  DB: D1Database;
  SESSION: KVNamespace;
  BETTER_AUTH_SECRET: string;
  BETTER_AUTH_URL: string;
  OAUTH_GITHUB_CLIENT_ID: string;
  OAUTH_GITHUB_CLIENT_SECRET: string;
  OAUTH_GOOGLE_CLIENT_ID: string;
  OAUTH_GOOGLE_CLIENT_SECRET: string;
  OAUTH_MICROSOFT_CLIENT_ID: string;
  OAUTH_MICROSOFT_CLIENT_SECRET: string;
  OAUTH_MICROSOFT_TENANT_ID: string;
  GCS_SERVICE_ACCOUNT_KEY: string;
  GCS_BUCKET_NAME: string;
}

declare namespace App {
  interface Locals {
    user?: {
      id: string;
      name: string;
      email: string;
      image?: string | null;
    };
    session?: {
      id: string;
      userId: string;
      expiresAt: Date;
    };
    tenant?: {
      id: string;
      userId: string;
      displayName: string | null;
      gcsPrefix: string;
      storageQuotaBytes: number;
      storageUsedBytes: number;
      preferences: string | null;
      createdAt: string;
      updatedAt: string;
    };
  }
}
