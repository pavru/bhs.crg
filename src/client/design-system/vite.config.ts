import { defineConfig, normalizePath } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import { readFileSync, readdirSync, realpathSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { readSourceInventory } from './source-inventory';
import { pdfjsAssets } from '../viteSupport';
import { mockupAssets } from './mockup-assets';

const root = dirname(fileURLToPath(import.meta.url));
const client = resolve(root, '..');
const themeFile = resolve(client, 'src/theme/md3-tokens.css');
const ui = resolve(client, 'src/shared/ui');
const list = (folder: string): string[] => readdirSync(folder, { withFileTypes: true })
  .flatMap(item => item.isDirectory() ? list(resolve(folder, item.name)) : [resolve(folder, item.name)])
  .filter(file => /\.(ts|tsx)$/.test(file) && !/\.(test|spec)\./.test(file));

export default defineConfig({
  root,
  plugins: [react(), tailwindcss(), pdfjsAssets(), mockupAssets(root), {
    name: 'catalog-source-metadata',
    resolveId: id => id === 'virtual:catalog-metadata' ? '\0catalog-metadata' : undefined,
    load(id) {
      if (id !== '\0catalog-metadata') return;
      let commit = 'локальная копия';
      try { commit = execFileSync('git', ['rev-parse', '--short', 'HEAD'], { cwd: client, encoding: 'utf8' }).trim(); } catch { /* Export without Git is supported. */ }
      const roles = [...new Set(readFileSync(themeFile, 'utf8').match(/--md-(?:sys|ext)-color-[\w-]+(?=\s*:)/g))];
      const theme = JSON.parse(readFileSync(resolve(client, 'src/theme/md3-theme.json'), 'utf8'));
      const typography = Object.entries(theme.typescale).filter(([name]) => name !== '//');
      const shape = Object.entries(theme.shape).filter(([name]) => name !== '//');
      const inventory = readSourceInventory(client);
      return `export default ${JSON.stringify({ commit, roles, typography, shape, inventory, state: theme.state, motion: theme.motion, elevation: theme.elevation, sourceFiles: list(ui).length })}`;
    },
    configureServer(server) {
      server.watcher.add([themeFile, resolve(client, 'src/theme/md3-theme.json'), resolve(client, 'src/shared')]);
      let update: ReturnType<typeof setTimeout>;
      server.watcher.on('all', (event, path) => {
        if (!['change', 'add', 'unlink'].includes(event) || /\.(test|spec)\./.test(path)) return;
        const file = normalizePath(path);
        if (!file.includes('/shared/') && file !== normalizePath(themeFile) && !file.endsWith('/theme/md3-theme.json')) return;
        clearTimeout(update);
        update = setTimeout(() => {
          const module = server.moduleGraph.getModuleById('\0catalog-metadata');
          if (module) server.moduleGraph.invalidateModule(module);
          server.ws.send({ type: 'full-reload' });
        }, 100);
      });
    },
  }],
  resolve: { alias: { '@': resolve(client, 'src') } },
  worker: { format: 'es' },
  cacheDir: resolve(client, '../../.design-sync/ui-catalog-vite'),
  server: { host: '127.0.0.1', port: 5200, strictPort: true, fs: { allow: [client, realpathSync(resolve(client, 'node_modules'))] } },
  build: {
    outDir: resolve(client, 'dist/ui-catalog'),
    emptyOutDir: true,
    rolldownOptions: { input: {
      index: resolve(root, 'index.html'), story: resolve(root, 'story.html'),
      mockup: resolve(root, 'mockup.html'), work12: resolve(root, 'work12.html'), work19: resolve(root, 'work19.html'),
      work21: resolve(root, 'work21.html'),
      work22: resolve(root, 'work22.html'),
      work23: resolve(root, 'work23.html'),
      work24: resolve(root, 'work24.html'),
      work26: resolve(root, 'work26.html'),
      work27: resolve(root, 'work27.html'),
      duplicatePayment: resolve(root, 'duplicate-payment.html'),
      paymentRequisites: resolve(root, 'payment-requisites.html'),
      planOffers: resolve(root, 'plan-offers.html'),
      planCalendars: resolve(root, 'plan-calendars.html'),
      planSchedules: resolve(root, 'plan-schedules.html'),
      planProgress: resolve(root, 'plan-progress.html'),
    } },
  },
});
