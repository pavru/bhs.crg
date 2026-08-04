// Тема до первой отрисовки: иначе светлое оформление успевает мигнуть перед тёмным.
// Файл вынесен из index.html, чтобы политика безопасности могла запретить инлайновые скрипты
// целиком (script-src 'self' в deploy/nginx.conf) — при инлайне пришлось бы разрешать их все.
// Подключается синхронно в <head>: выполниться он должен ДО отрисовки.
try {
  var t = localStorage.getItem('crg-theme') || 'system';
  var dark = t === 'dark' || (t === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches);
  document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light');
} catch (e) { /* приватный режим без localStorage — остаётся светлая тема по умолчанию */ }
