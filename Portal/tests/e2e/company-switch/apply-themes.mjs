// Copies `theme` from each demo pack profile.json onto the live profile stored in platformDB.
// Reads the live profile first and only replaces its theme; every other member is kept.
// Usage: node apply-themes.mjs [--functions http://localhost:8081] [--secret <X-Settings-Write-Secret>]
import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const args = process.argv.slice(2);
const opt = (name, fallback) => {
  const i = args.indexOf(name);
  return i >= 0 && args[i + 1] ? args[i + 1] : fallback;
};
const functionsUrl = opt('--functions', process.env.JM_FUNCTIONS_URL || 'http://localhost:8081').replace(/\/$/, '');
const secret = opt('--secret', process.env.JM_SETTINGS_WRITE_SECRET || '');
if (!secret) {
  console.error('Missing --secret (or JM_SETTINGS_WRITE_SECRET).');
  process.exit(2);
}

const packsDir = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..', 'demo', 'packs');
let failed = 0;
for (const pack of readdirSync(packsDir)) {
  const file = join(packsDir, pack, 'profile.json');
  if (!existsSync(file)) continue;
  const local = JSON.parse(readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
  if (!local.theme) continue;
  const key = local.profileKey;
  const url = `${functionsUrl}/api/settings/company-profiles/${encodeURIComponent(key)}`;
  const current = await fetch(url);
  if (!current.ok) {
    console.log(`[FAIL] ${key}: GET ${current.status}`);
    failed++;
    continue;
  }
  const live = await current.json();
  const body = { ...live, theme: local.theme, updatedBy: 'mc6-themes' };
  const put = await fetch(url, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json', 'X-Settings-Write-Secret': secret },
    body: JSON.stringify(body),
  });
  const text = await put.text();
  if (!put.ok) {
    console.log(`[FAIL] ${key}: PUT ${put.status} ${text.slice(0, 300)}`);
    failed++;
    continue;
  }
  console.log(`[PASS] ${key}: theme ${JSON.stringify(local.theme)} applied`);
}
process.exit(failed ? 1 : 0);
