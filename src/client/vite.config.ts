import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import path from 'path';
import { deliveryCspOnPreview, pdfjsAssets } from './viteSupport';

export default defineConfig({
  // pdf.js: файлы рядом с библиотекой — в сборку (pdfjs/<версия>/), политика поставки — в preview
  // (issue #1490, viteSupport.ts).
  plugins: [react(), tailwindcss(), pdfjsAssets(), deliveryCspOnPreview()],
  // Worker — модулем, а не классическим скриптом: pdf.js внутри него делает import() запасных
  // декодеров. Собирается он при этом в `.js`, а не `.mjs`: nginx поставки отдал бы `.mjs` как
  // application/octet-stream, и worker не запустился бы (сторож — шаг после сборки в CI).
  worker: { format: 'es' },
  resolve: {
    alias: { '@': path.resolve(__dirname, './src') },
  },
  // Язык форматирования у тестов один и назван явно: «системный» дал бы ответ, зависящий от машины
  // (см. src/shared/testing/formatLocale.ts).
  test: { setupFiles: ['./src/shared/testing/formatLocale.ts', './src/shared/testing/matchMedia.ts'] },
  // Адрес API — из окружения: в CI живые прогоны поднимают свою копию на другом порту, и
  // прибитый гвоздём :5000 увёл бы их в чужое (или несуществующее) приложение. Умолчание —
  // прежнее, ради `npm run dev` без переменных.
  server: {
    proxy: {
      '/api': { target: process.env.API_TARGET || 'http://localhost:5000', changeOrigin: true },
    },
  },
  // Тем же адресом пользуется `vite preview` — в CI прогоны идут по СОБРАННОМУ клиенту, а не по
  // dev-серверу: проверять надо то, что поедет в продакшн.
  preview: {
    proxy: {
      '/api': { target: process.env.API_TARGET || 'http://localhost:5000', changeOrigin: true },
    },
  },
});
