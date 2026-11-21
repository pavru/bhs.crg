import fs from 'node:fs/promises';
import { launchBrowser } from '../e2e/harness.mjs';

const manifest = JSON.parse(await fs.readFile(new URL('./mockups/manifest.json', import.meta.url), 'utf8'));
const base = process.env.CATALOG_URL || 'http://127.0.0.1:5200';
const filter = process.env.MOCKUPS?.split(',');
const projects = manifest.projects.filter(p => !filter || filter.some(value => p.id.startsWith(value)));
const cases = projects.flatMap(project => project.pages.map(page => ({ project: project.id, kind: project.kind, title: project.title, page: page.path })));
const browser = await launchBrowser();
const failures = [], requests = [], results = [];
let completed = 0;
try {
  await Promise.all(Array.from({ length: 3 }, async (_, worker) => {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
    let current;
    page.on('pageerror', error => failures.push({ ...current, type: 'js', error: error.message }));
    page.on('response', response => { if (response.status() >= 400) requests.push({ ...current, status: response.status(), url: response.url() }); });
    for (const item of cases.filter((_, index) => index % 3 === worker)) {
      current = item;
      try {
        const params = new URLSearchParams({ project: item.project, page: item.page, theme: 'light' });
        await page.goto(`${base}/mockup.html?${params}`, { waitUntil: 'domcontentloaded', timeout: 60000 });
        await page.waitForFunction(() => window.__mockupState?.ready, undefined, { timeout: 25000 });
        const state = await page.evaluate(() => ({ ...window.__mockupState, textLength: document.body.innerText.length, live: document.querySelectorAll('[data-live-control]').length, unresolved: [...document.querySelectorAll('svg path[d]')].filter(path => path.getAttribute('d').includes('{{')).length }));
        if (state.error || state.textLength < 20 || state.unresolved) failures.push({ ...item, type: 'state', ...state });
        results.push({ ...item, ...state });
      } catch (error) { failures.push({ ...item, type: 'load', error: error.message }); }
      completed++;
      if (completed % 20 === 0) console.log(JSON.stringify({ completed, total: cases.length, failures: failures.length, badRequests: requests.length }));
    }
    await page.close();
  }));
} finally { await browser.close(); }
const output = process.env.MOCKUP_REPORT || '../../.design-sync/mockup-browser.json';
const report = { projects: projects.length, completed, failures, requests, results };
await fs.writeFile(output, JSON.stringify(report, null, 2));
console.log(JSON.stringify({ projects: projects.length, completed, failures: failures.length, badRequests: requests.length, report: output }));
process.exitCode = failures.length || requests.length ? 1 : 0;
