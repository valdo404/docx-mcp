/**
 * Post-build fix for @astrojs/cloudflare Pages compatibility.
 *
 * The adapter merges the user wrangler.jsonc into dist/_worker.js/wrangler.json
 * and adds Worker-specific keys (main, rules) plus a reserved ASSETS binding.
 * These are invalid for `wrangler pages deploy`, so we strip them.
 */
import { readFileSync, writeFileSync } from 'node:fs';

const file = 'dist/_worker.js/wrangler.json';
const config = JSON.parse(readFileSync(file, 'utf8'));

// Keys incompatible with Cloudflare Pages
const keysToRemove = [
  'main',               // Worker entrypoint — Pages determines this
  'rules',              // Worker module rules — not supported in Pages
  'assets',             // "ASSETS" is reserved by Pages (provided natively)
  'pages_build_output_dir', // Pages-level config, not valid inside worker config
  'configPath',         // Metadata from adapter merge
  'userConfigPath',     // Metadata from adapter merge
];

for (const key of keysToRemove) {
  delete config[key];
}

writeFileSync(file, JSON.stringify(config));
