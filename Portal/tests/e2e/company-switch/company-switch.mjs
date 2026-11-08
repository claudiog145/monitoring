// Playwright E2E for the header company switch (spec 1000 MC6, contract v6 §2/§6).
// For every company: select it in the header, wait for the switch, check the header colour equals
// theme.primaryColor, then open each tab and check its visible text carries no other company's
// vocabulary or service keys. Prints [PASS]/[FAIL] per check; exit code 0 only when all pass.
// The originally active company is restored at the end (also on failure).
import { chromium } from 'playwright';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const args = process.argv.slice(2);
const opt = (name, fallback) => {
  const i = args.indexOf(name);
  return i >= 0 && args[i + 1] ? args[i + 1] : fallback;
};
const portal = opt('--portal', 'http://localhost:5080').replace(/\/$/, '');
const companies = opt('--companies', 'arrivia,banco,kinectify,mercadolibre').split(',').map((s) => s.trim()).filter(Boolean);
const dumpDir = args.includes('--dump') ? join(dirname(fileURLToPath(import.meta.url)), 'out') : null;
const settleMs = Number(opt('--settle-ms', '2500'));

const TABS = [
  ['overview', 'Overview'],
  ['journeys', 'Customer Journeys'],
  ['health', 'Platform Health'],
  ['analytics', 'Analytics'],
  ['monkeys', 'Simian Army'],
  ['daemons', 'Daemons'],
  ['memory', 'Memory Bank'],
  ['exceptions', 'Exceptions'],
];

// Tourism vocabulary that must never surface for a company whose profile is not the tourism one.
const TOURISM_TERMS = ['Hotels', 'Cruise', 'T-Mobile', 'Club'];

// Service keys that are also plain English words the portal uses in its own prose ("search",
// "checkout", "cases"...). They count as a leak only where the UI prints a service key: "Name/key",
// "· key ·", "(key domain)" or a cell holding only the key. Distinctive keys count anywhere.
const GENERIC_KEYS = new Set(['search', 'checkout', 'payments', 'cases', 'alerts', 'shared', 'core', 'air', 'onboarding']);

// Known artifact: .github/spec1000/e2e.ps1 (gate level api/portal) posts test triage signals
// (kpi E2EManual / E2ELatency, domain "hotels") under whichever company is active when the gate
// runs. Those rows are test fixtures, not company data, so their cards/rows are left out of the scan.
const GATE_ARTIFACT_MARKERS = ['E2EManual', 'E2ELatency'];

let passed = 0;
let failed = 0;
const report = (ok, label, detail) => {
  if (ok) passed++; else failed++;
  console.log(`${ok ? '[PASS]' : '[FAIL]'} ${label}${detail ? ` - ${detail}` : ''}`);
};

const escapeRe = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

function hexToRgb(hex) {
  const n = parseInt(hex.slice(1), 16);
  return `rgb(${(n >> 16) & 255}, ${(n >> 8) & 255}, ${n & 255})`;
}

async function getJson(path) {
  const r = await fetch(`${portal}${path}`);
  if (!r.ok) throw new Error(`GET ${path} -> ${r.status}`);
  return r.json();
}

/** Forbidden terms for `key`: tourism words (non-tourism companies) + other companies' service keys/names. */
function forbiddenFor(key, profiles) {
  const self = profiles.find((p) => p.profileKey === key);
  const ownKeys = new Set((self.services || []).map((s) => s.key.toLowerCase()));
  const ownNames = new Set((self.services || []).map((s) => s.displayName));
  const isTourism = (self.services || []).some((s) => s.key === 'hotels' || s.key === 'cruises');
  const terms = [];
  if (!isTourism) TOURISM_TERMS.forEach((t) => terms.push({ term: t, re: new RegExp(`\\b${escapeRe(t)}`) }));
  for (const other of profiles) {
    if (other.profileKey === key || !companies.includes(other.profileKey)) continue;
    for (const s of other.services || []) {
      if (!ownKeys.has(s.key.toLowerCase())) {
        const k = escapeRe(s.key);
        const re = GENERIC_KEYS.has(s.key)
          ? new RegExp(`(?:/${k}(?![\\w./?=&-])|·\\s*${k}\\s*·|\\(${k} domain\\)|^${k}$)`, 'm')
          : new RegExp(`(?<![\\w-])${k}(?![\\w-])`);
        terms.push({ term: `${other.profileKey}:${s.key}`, re });
      }
      // Single-word display names ("Payments", "Cars") are covered by the key rule above.
      if (s.displayName && /\s/.test(s.displayName.trim()) && !ownNames.has(s.displayName)) {
        terms.push({ term: `${other.profileKey}:"${s.displayName}"`, re: new RegExp(`(?<![\\w-])${escapeRe(s.displayName)}(?![\\w-])`) });
      }
    }
  }
  return terms;
}

async function settle(page) {
  await page.waitForLoadState('networkidle', { timeout: 30000 }).catch(() => {});
  await page.waitForTimeout(settleMs);
}

async function switchTo(page, key) {
  const current = await page.evaluate(() => document.body.dataset.company || '');
  if (current !== key) {
    await page.selectOption('#company-select', key);
  }
  await page.waitForFunction((k) => document.body.dataset.company === k, key, { timeout: 30000 });
  // A switch redraws in place; a fresh load proves the server pointer and the theme survive a reload.
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.waitForFunction((k) => document.body.dataset.company === k, key, { timeout: 30000 });
  await settle(page);
}

async function setActiveViaPage(page, key) {
  // Uses the portal's own switch path (antiforgery + Functions) to restore the original pointer.
  await page.goto(portal, { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('#company-select:not([disabled])', { timeout: 30000 });
  await page.selectOption('#company-select', key);
  await page.waitForFunction((k) => document.body.dataset.company === k, key, { timeout: 30000 });
}

const main = async () => {
  const profiles = (await getJson('/api/settings/company-profiles')).profiles || [];
  const original = (await getJson('/api/settings/company-profiles-active')).profileKey || null;
  console.log(`Portal ${portal}; originally active company: ${original || '(none)'}`);

  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
  page.on('dialog', (d) => d.dismiss().catch(() => {}));
  try {
    await page.goto(portal, { waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#company-select:not([disabled])', { timeout: 30000 });

    for (const key of companies) {
      const profile = profiles.find((p) => p.profileKey === key);
      if (!profile) { report(false, `${key}: profile exists`, 'not in /api/settings/company-profiles'); continue; }
      if (!profile.enabled) { report(false, `${key}: profile enabled`); continue; }
      try {
        await switchTo(page, key);
        report(true, `${key}: selected in header`);
      } catch (e) {
        report(false, `${key}: selected in header`, e.message.split('\n')[0]);
        continue;
      }

      const primary = profile.theme && profile.theme.primaryColor;
      const headerBg = await page.$eval('.app-header', (el) => getComputedStyle(el).backgroundColor);
      if (!primary) report(false, `${key}: theme.primaryColor defined`);
      else report(headerBg === hexToRgb(primary), `${key}: header background = ${primary}`, `got ${headerBg}`);

      const shown = (await page.$eval('#company-name', (el) => (el.hidden ? '' : el.textContent.trim())).catch(() => '')) || '';
      const expectedName = (profile.theme && profile.theme.shortName) || profile.displayName;
      report(shown === expectedName, `${key}: company name shown "${expectedName}"`, `got "${shown}"`);

      const terms = forbiddenFor(key, profiles);
      for (const [tab, label] of TABS) {
        try {
          await page.click(`#tab-btn-${tab}`);
          await page.waitForSelector(`#tab-${tab}:not([hidden])`, { timeout: 15000 });
          await settle(page);
          const still = await page.evaluate(() => document.body.dataset.company);
          const { text: visible, artifacts } = await page.evaluate(({ tabId, markers }) => {
            const panel = document.getElementById(tabId);
            const hidden = new Set();
            const walker = document.createTreeWalker(panel, NodeFilter.SHOW_TEXT);
            for (let n = walker.nextNode(); n; n = walker.nextNode()) {
              if (!markers.some((m) => n.nodeValue.includes(m))) continue;
              // The outermost list row / item holding the marker (an investigation card, a table row),
              // else the smallest row, else the text's own element - never a card full of real data.
              let box = null;
              for (let el = n.parentElement; el && el !== panel; el = el.parentElement) {
                if (el.matches('li, tr, [class*="item"]') && el.innerText.length <= 1500) box = el;
              }
              box = box || n.parentElement.closest('[class*="row"]') || n.parentElement;
              if (!panel.contains(box) || box === panel || box.innerText.length > 1500) box = n.parentElement;
              hidden.add(box);
            }
            const saved = [...hidden].map((el) => [el, el.style.display]);
            saved.forEach(([el]) => { el.style.display = 'none'; });
            const company = document.querySelector('.control-group--company');
            const savedCompany = company ? company.style.display : null;
            if (company) company.style.display = 'none';
            const header = document.querySelector('.app-header').innerText;
            const body = panel.innerText;
            saved.forEach(([el, d]) => { el.style.display = d; });
            if (company) company.style.display = savedCompany;
            return { text: `${header}\n${body}`, artifacts: saved.length };
          }, { tabId: `tab-${tab}`, markers: GATE_ARTIFACT_MARKERS });
          if (dumpDir) {
            mkdirSync(dumpDir, { recursive: true });
            writeFileSync(join(dumpDir, `${key}-${tab}.txt`), visible, 'utf8');
          }
          const hits = terms
            .map(({ term, re }) => {
              const m = visible.match(re);
              if (!m) return null;
              const at = m.index;
              return `${term} in "...${visible.slice(Math.max(0, at - 40), at + 40).replace(/\s+/g, ' ')}..."`;
            })
            .filter(Boolean);
          const ok = still === key && hits.length === 0;
          const note = artifacts ? ` (${artifacts} gate E2E artifact(s) E2EManual/E2ELatency ignored)` : '';
          report(ok, `${key}: ${label} shows only ${key} data${note}`,
            still !== key ? `active company changed to ${still}` : hits.slice(0, 5).join('; '));
        } catch (e) {
          report(false, `${key}: ${label} shows only ${key} data`, e.message.split('\n')[0]);
        }
      }
    }
  } finally {
    if (original) {
      try {
        await setActiveViaPage(page, original);
        const now = (await getJson('/api/settings/company-profiles-active')).profileKey;
        report(now === original, `restored originally active company ${original}`, `now ${now}`);
      } catch (e) {
        report(false, `restored originally active company ${original}`, e.message.split('\n')[0]);
      }
    }
    await browser.close();
  }
  console.log(`company-switch: ${passed} passed, ${failed} failed`);
  process.exit(failed === 0 ? 0 : 1);
};

main().catch((e) => {
  console.error(`[FAIL] company-switch crashed - ${e.stack || e.message}`);
  process.exit(1);
});
