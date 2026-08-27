import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import path from 'path';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { '@': path.resolve(__dirname, './src') },
  },
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
