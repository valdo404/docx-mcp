/**
 * Post-build fix for @astrojs/cloudflare Pages compatibility.
 *
 * `astro build` with @astrojs/cloudflare generates dist/_worker.js/wrangler.json
 * containing Worker-specific keys that break `wrangler pages deploy`:
 *
 *   - `rules`  — ESModule rules, not valid for Pages
 *   - `assets` — uses reserved "ASSETS" binding name
 *
 * We patch the file to remove only these problematic keys while preserving
 * essential config like `main: "entry.mjs"` (the worker entry point).
 */
import { readFileSync, writeFileSync } from 'node:fs';

const configPath = 'dist/_worker.js/wrangler.json';
const config = JSON.parse(readFileSync(configPath, 'utf8'));

delete config.rules;
delete config.assets;

writeFileSync(configPath, JSON.stringify(config));
