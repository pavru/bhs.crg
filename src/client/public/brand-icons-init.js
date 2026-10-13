// Значок вкладки с меткой версии — ещё до того, как браузер возьмёт его по адресу из index.html.
//
// Chromium держит значок вкладки в собственной базе по АДРЕСУ значка до недели и сервер не
// спрашивает (issue #1315). Адрес в index.html метки версии не знает — файл статический, — поэтому
// по нему браузер показал бы прежний значок, а приложение, получив оформление, тут же сменило бы
// его действующим: значок мигал бы на каждой загрузке, пока запись в базе не истечёт. Метку
// прошлого захода приложение запоминает (shared/ui/BrandIconLinks), и здесь ссылка ставится по ней.
// Устарела метка — не беда: сервер по любому адресу отдаёт действующий значок, а приложение
// следом поставит верную.
//
// Отдельным файлом, а не инлайном, и синхронно в <head> — по тем же причинам, что theme-init.js.
// ⚠️ Адреса и ключ продублированы из shared/ui/brandIcons.ts: модуль сюда не подключить.
try {
  var v = localStorage.getItem('crg-brand-icons');
  if (v) {
    var q = '?v=' + encodeURIComponent(v);
    var tab = document.querySelector('link[rel="icon"]');
    var apple = document.querySelector('link[rel="apple-touch-icon"]');
    if (tab) tab.setAttribute('href', '/api/branding/icons/tab' + q);
    if (apple) apple.setAttribute('href', '/api/branding/icons/apple' + q);
  }
} catch (e) { /* приватный режим без localStorage — остаются адреса без метки */ }
