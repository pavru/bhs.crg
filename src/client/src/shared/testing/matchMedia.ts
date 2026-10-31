/**
 * `matchMedia` для jsdom — там его нет вовсе (ревью PR #1709). Без заглушки каждый экран с общим компонентом,
 * у которого своё правило ширины (`Tabs` на телефоне — `underline`), ронял бы свои тесты, а обходить это
 * проверкой в коде приложения — значит писать код под тесты.
 *
 * <p>Отвечает как рабочий стол шириной окна jsdom (1024 px): `min-width` и `max-width` сверяются с
 * `innerWidth`, остальное (`prefers-color-scheme`, `pointer`) не совпадает. «Не совпадает ничего» не годится:
 * `WIDE` тогда не совпал бы, и слои истории (`historyLayers`) во всех тестах решили бы, что они на телефоне.
 * Тест, которому нужна другая ширина, ставит свой `window.matchMedia` — заглушка ему не мешает.</p>
 *
 * Подключён в `vite.config.ts` (`test.setupFiles`); в окружении node окна нет, и файл ничего не делает.
 */
const WIDTH = /\((min|max)-width:\s*([\d.]+)px\)/g;

function matches(query: string): boolean {
  const found = [...query.matchAll(WIDTH)];
  return found.length > 0 && found.every(([, side, px]) =>
    side === 'min' ? window.innerWidth >= Number(px) : window.innerWidth <= Number(px));
}

if (typeof window !== 'undefined' && typeof window.matchMedia !== 'function') {
  window.matchMedia = (query: string) => ({
    media: query,
    matches: matches(query),
    onchange: null,
    addEventListener: () => {},
    removeEventListener: () => {},
    addListener: () => {},
    removeListener: () => {},
    dispatchEvent: () => false,
  });
}
