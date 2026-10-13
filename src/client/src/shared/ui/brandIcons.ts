/**
 * Ссылки на значок вкладки и значок iPhone с меткой версии (issue #1315).
 *
 * Зачем приложению трогать `<link rel="icon">`, если значок отдаёт сервер и адрес в `index.html`
 * уже стоит. Потому что Chromium значок вкладки по HTTP НЕ перепроверяет: он держит его в
 * собственной базе по АДРЕСУ значка до недели и сервер не спрашивает — ни `no-cache`, ни тег до
 * этого пути не доходят. С постоянным адресом смена логотипа доезжала бы до вкладок, закладок и
 * истории с опозданием до семи дней. Единственный рычаг — сам адрес: метка версии в нём.
 *
 * Поэтому ссылка ставится при КАЖДОЙ загрузке, как только пришло оформление, а не только вкладке,
 * открытой в момент смены логотипа. Метка — и у стандартных значков: смена их картинок в новой
 * сборке — такая же смена.
 */

/** Ключ в localStorage — его же читает `public/brand-icons-init.js`, до первой отрисовки. */
export const BRAND_ICONS_KEY = 'crg-brand-icons';

const LINKS = [
  { selector: 'link[rel="icon"]', name: 'tab' },
  { selector: 'link[rel="apple-touch-icon"]', name: 'apple' },
] as const;

/** Адрес значка с меткой версии. Такому адресу сервер даёт длинный кеш. */
export function brandIconUrl(name: 'tab' | 'apple', version: string): string {
  return `/api/branding/icons/${name}?v=${encodeURIComponent(version)}`;
}

/**
 * Поставить ссылкам страницы адреса с меткой. Ссылку с тем же адресом не трогает: присвоение `href`
 * браузер читает как смену значка и перезапрашивает его.
 */
export function applyBrandIcons(doc: Document, version: string): void {
  for (const { selector, name } of LINKS) {
    const link = doc.querySelector<HTMLLinkElement>(selector);
    const href = brandIconUrl(name, version);
    if (link && link.getAttribute('href') !== href) link.setAttribute('href', href);
  }
}
