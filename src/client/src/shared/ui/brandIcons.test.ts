import { describe, expect, it } from 'vitest';
// Текстом, как он лежит в сборке: скрипт не модуль, и проверяется он исполнением.
import script from '../../../public/brand-icons-init.js?raw';
import { BRAND_ICONS_KEY, applyBrandIcons, brandIconUrl } from './brandIcons';

const TAB = 'link[rel="icon"]';
const APPLE = 'link[rel="apple-touch-icon"]';

/** Страница с двумя ссылками из index.html — ровно столько документа, сколько читает код. */
function page(present: string[] = [TAB, APPLE]) {
  const hrefs: Record<string, string> = {
    [TAB]: '/api/branding/icons/tab',
    [APPLE]: '/api/branding/icons/apple',
  };
  const writes: string[] = [];
  const doc = {
    querySelector: (selector: string) => !present.includes(selector) ? null : {
      getAttribute: () => hrefs[selector],
      setAttribute: (_: string, value: string) => { hrefs[selector] = value; writes.push(selector); },
    },
  } as unknown as Document;
  return { doc, hrefs, writes };
}

describe('ссылки на значки с меткой версии', () => {
  it('обе ссылки получают адрес с меткой', () => {
    const { doc, hrefs } = page();

    applyBrandIcons(doc, '639000000000000000-ab12cd34');

    expect(hrefs[TAB]).toBe('/api/branding/icons/tab?v=639000000000000000-ab12cd34');
    expect(hrefs[APPLE]).toBe('/api/branding/icons/apple?v=639000000000000000-ab12cd34');
  });

  // Присвоение адреса браузер читает как смену значка и перезапрашивает его.
  it('ссылку с тем же адресом не трогает', () => {
    const { doc, writes } = page();
    applyBrandIcons(doc, 'std-1');
    writes.length = 0;

    applyBrandIcons(doc, 'std-1');

    expect(writes).toEqual([]);
  });

  it('метка уходит в адрес закодированной', () => {
    expect(brandIconUrl('tab', 'a b&c')).toBe('/api/branding/icons/tab?v=a%20b%26c');
  });

  it('страница без ссылки значка iPhone — не ошибка', () => {
    const { doc, hrefs } = page([TAB]);

    applyBrandIcons(doc, 'std-1');

    expect(hrefs[TAB]).toBe('/api/branding/icons/tab?v=std-1');
  });
});

// Ранний скрипт подключается до модулей и общий код взять не может — ключ и адреса в нём
// продублированы. Копия, разошедшаяся с оригиналом, не падает: значок просто мигал бы прежним на
// каждой загрузке, и заметить это можно только глазами.
describe('ранний скрипт значков', () => {
  function run(stored: string | null, present?: string[]) {
    const target = page(present);
    const storage = { getItem: (key: string) => (key === BRAND_ICONS_KEY ? stored : null) };
    new Function('localStorage', 'document', script)(storage, target.doc);
    return target;
  }

  it('ставит те же адреса, что приложение, — по запомненной метке', () => {
    const expected = page();
    applyBrandIcons(expected.doc, 'a b&c');

    expect(run('a b&c').hrefs).toEqual(expected.hrefs);
  });

  it('метки нет — ссылки остаются как в index.html', () => {
    expect(run(null).writes).toEqual([]);
  });

  it('без localStorage и без ссылок не падает', () => {
    expect(() => run('std-1', [])).not.toThrow();
    expect(() => new Function('localStorage', 'document', script)(
      { getItem: () => { throw new Error('доступ запрещён'); } }, page().doc)).not.toThrow();
  });
});
