import { Storage } from '@google-cloud/storage';

let _storage: Storage | null = null;

export function getGcsClient(serviceAccountKey: string): Storage {
  if (!_storage) {
    const credentials = JSON.parse(serviceAccountKey);
    _storage = new Storage({
      projectId: credentials.project_id,
      credentials,
    });
  }
  return _storage;
}

export async function createTenantPrefix(
  serviceAccountKey: string,
  bucketName: string,
  prefix: string,
  metadata: Record<string, string>,
): Promise<void> {
  const storage = getGcsClient(serviceAccountKey);
  const bucket = storage.bucket(bucketName);

  const file = bucket.file(`${prefix}.marker`);
  await file.save('', {
    metadata: { metadata },
    contentType: 'application/octet-stream',
  });
}

export async function getTenantStorageUsed(
  serviceAccountKey: string,
  bucketName: string,
  prefix: string,
): Promise<number> {
  const storage = getGcsClient(serviceAccountKey);
  const bucket = storage.bucket(bucketName);
  const [files] = await bucket.getFiles({ prefix });
  return files.reduce((total, file) => total + parseInt(String(file.metadata.size || '0'), 10), 0);
}
