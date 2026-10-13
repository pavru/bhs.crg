import { useEffect } from 'react';
import { useBranding } from '@/shared/api/branding';
import { BRAND_ICONS_KEY, applyBrandIcons } from './brandIcons';

/**
 * Держит значок вкладки на адресе с действующей меткой версии (issue #1315) — почему это дело
 * приложения, а не одного сервера, см. `brandIcons.ts`.
 *
 * Пока оформление не получено, ссылки не трогаем: там стоит то, что поставил ранний скрипт по
 * метке прошлого захода, либо адрес без метки из `index.html`.
 */
export function BrandIconLinks() {
  const version = useBranding().data?.icons.version;

  useEffect(() => {
    if (!version) return;
    applyBrandIcons(document, version);
    // Метку запоминаем: при следующей загрузке ранний скрипт поставит ссылку ещё до того, как
    // браузер возьмёт значок по адресу без метки — иначе прежний значок мигал бы на каждой загрузке.
    try { localStorage.setItem(BRAND_ICONS_KEY, version); } catch { /* приватный режим — без запоминания */ }
  }, [version]);

  return null;
}
