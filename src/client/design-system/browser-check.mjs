import fs from 'node:fs/promises';
import { launchBrowser } from '../e2e/harness.mjs';

// Visits every live specimen in both themes, reusing a React root between cases.
const base = process.env.CATALOG_URL || 'http://127.0.0.1:5200';
const browser = await launchBrowser();
try {
const index = await browser.newPage();
await index.goto(base + '/?section=coverage');
await index.waitForFunction(() => window.__catalogManifest, undefined, { timeout: 90000 });
const manifest = await index.evaluate(() => window.__catalogManifest);
if (manifest.missing.length) throw Error('Uncovered exports: ' + manifest.missing.join(', '));
const filter = process.env.COMPONENTS?.split(',');
const stories = manifest.stories.filter(s => !filter || filter.includes(s.component));
const failures = [], warnings = [], traffic = [];
const themes = process.env.THEME ? [process.env.THEME] : ['light', 'dark'];
const width = Number(process.env.CATALOG_WIDTH || 1200);
const workers = Math.min(4, Math.max(1, stories.length));
let completed = 0;
  await Promise.all(Array.from({ length: workers }, async (_, worker) => {
    const page = await browser.newPage({ viewport: { width, height: 900 } });
    let current;
    page.on('pageerror', error => failures.push({ story: current, pageError: error.message }));
    page.on('console', message => { if (['error', 'warning'].includes(message.type()) && current?.component !== 'ErrorBoundary') warnings.push({ story: current, message: message.text().slice(0, 800) }); });
    page.on('request', request => { if (new URL(request.url()).pathname.startsWith('/api/')) traffic.push(request.url()); });
    const cases = stories.filter((_, i) => i % workers === worker);
    await page.goto(`${base}/story.html?story=${encodeURIComponent(cases[0].id)}`);
    await page.waitForFunction(() => window.__renderCatalogStory, undefined, { timeout: 90000 });
    for (const story of cases) for (const theme of themes) {
      current = { ...story, theme };
      await page.evaluate(({ id, theme }) => window.__renderCatalogStory(id, {}, theme), { id: story.id, theme });
      try {
        await page.waitForFunction(id => window.__catalogStory?.id === id && window.__catalogStory.ready, story.id, { timeout: 12000 });
        const result = await page.evaluate(() => ({ ...window.__catalogStory, text: document.body.innerText.slice(0, 100), count: document.querySelector('#specimen')?.childElementCount || document.querySelector('#specimen')?.textContent?.trim().length || document.querySelectorAll('[role="dialog"], [role="alertdialog"], [role="menu"], [role="listbox"], [role="tooltip"], [data-snackbar-viewport] > li').length }));
        if (result.error) failures.push({ ...current, error: result.error });
        else if (!result.count) failures.push({ ...current, error: 'No specimen rendered' });
      } catch (error) { failures.push({ ...current, error: error.message.slice(0, 250), lastState: await page.evaluate(() => window.__catalogStory).catch(() => null) }); }
      completed++;
      if (completed % 100 === 0) console.log(JSON.stringify({ completed, total: stories.length * themes.length, failures: failures.length }));
    }
    await page.close();
  }));
  const report = { components: new Set(stories.map(s => s.component)).size, cases: stories.length, themes, width, completed, failures, warnings, actualApiRequests: traffic };
  const output = process.env.CATALOG_REPORT;
  if (output) await fs.writeFile(output, JSON.stringify(report, null, 2));
  console.log(JSON.stringify({ ...report, warnings: warnings.length }, null, 2));
  if (failures.length || traffic.length) process.exitCode = 1;
} finally { await browser.close(); }
