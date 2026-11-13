import { describe, it, expect } from 'vitest';
import { CATALOG_RECORD, DATASET_RECORD, RECORD_ROUTES, SET_RECORD, TEMPLATE_RECORD, hitRoute, openTo, recordHref, recordLink, recordScreen } from './recordRoutes';
import { workNav, settingsNav } from './navConfig';
import type { AccessInfo } from '@/shared/api/access';

const costs = { code: 'costs', title: 'Счета и накладные', available: true };
const access = (permissions: string[], modules = [costs]): AccessInfo => ({ permissions, modules });
const ID = '3fa85f64-5717-4562-b3fc-2c963f66afa6';
const reader = access(['costs.invoice.read']);
/** Ссылка так, как её строит экран таблицы: экран — раз на таблицу, адрес — на строку. */
const link = (type: string | null | undefined, id: string | null, who: AccessInfo, name?: string) =>
  recordLink(recordScreen(type, who), id, name);

/**
 * Переход из строки таблицы в форму записи (задача G4, issue #1097).
 *
 * ⚠️ Главное здесь — отказы: ссылка, которой не положено быть, обязана не появиться. Появившаяся
 * ведёт на «Раздел недоступен» — то есть обещает переход, которого не будет.
 */
describe('адрес записи по типу', () => {
  it('счёт открывается на экране счетов, названный параметром адреса', () => {
    expect(link('СчётНаОплату', ID, reader)).toEqual({ to: `/invoices?invoice=${ID}`, label: 'Открыть счёт' });
  });

  it('без права на счета ссылки нет — модуль открыт, а экран счетов закрыт', () => {
    expect(link('СчётНаОплату', ID, access(['costs.waybill.read']))).toBeNull();
  });

  it('без модуля ссылки нет, хотя право записано', () => {
    expect(link('СчётНаОплату', ID, access(['costs.invoice.read'], []))).toBeNull();
    expect(link('СчётНаОплату', ID, access(['costs.invoice.read'], [{ ...costs, available: false }]))).toBeNull();
  });

  it('у типа без экрана и у таблицы без типа ссылки нет', () => {
    expect(link('ТипБезЭкрана', ID, reader)).toBeNull();
    expect(link(null, ID, reader)).toBeNull();
    expect(link(undefined, ID, reader)).toBeNull();
  });

  // Код типа приходит с сервера. Поиск по унаследованным ключам объекта нашёл бы под «constructor»
  // функцию — и «экран» без адреса.
  it('код типа, совпавший с унаследованным ключом объекта, экраном не становится', () => {
    for (const type of ['constructor', 'toString', '__proto__'])
      expect(recordScreen(type, reader), type).toBeNull();
  });

  it('без ключа строки ссылки нет', () => {
    expect(link('СчётНаОплату', null, reader)).toBeNull();
  });

  it('ключ в адрес ложится значением параметра, а не куском адреса', () => {
    const { to } = link('СчётНаОплату', 'a&b=c#d', reader)!;
    expect(new URL(to, 'http://x').searchParams.get('invoice')).toBe('a&b=c#d');
    expect(new URL(to, 'http://x').hash).toBe('');
  });

  // Стройку открывает её страница, а не выбор в списке (shell-05): ключ — шаг пути, и он кодируется.
  it('стройка открывается своей страницей, ключ — закодированным шагом пути', () => {
    const builder = access(['core.constructions.read']);
    expect(link('Стройка', ID, builder)).toEqual({ to: `/document-sets/${ID}`, label: 'Открыть стройку' });
    expect(link('Стройка', 'a/b?c#d', builder)!.to).toBe('/document-sets/a%2Fb%3Fc%23d');
    expect(link('Стройка', ID, access([]))).toBeNull();
  });

  // Набор данных — на экране своего уровня (shell-05, решение «А»): адрес из уровня карточки поиска.
  it('набор данных открывается на панели своего уровня, правом её раздела', () => {
    const both = access(['core.datasets.read', 'core.constructions.read']);
    const at = (level: Parameters<typeof hitRoute>[1], who = both) => {
      const route = hitRoute(DATASET_RECORD, level, who);
      return route && recordHref(route, 'd/1');
    };
    expect(at({ scope: 'System' })).toBe('/datasets?file=d%2F1');
    expect(at({ scope: 'Construction', scopeId: 'c-1', constructionId: 'c-1' })).toBe('/document-sets/c-1/datasets?file=d%2F1');
    expect(at({ scope: 'Section', scopeId: 's-1', constructionId: 'c-1' })).toBe('/document-sets/c-1/sections/s-1/datasets?file=d%2F1');
    expect(at({ scope: 'Set', scopeId: 'k/1', constructionId: 'c-1' })).toBe('/document-sets/c-1/sets/k%2F1/datasets?file=d%2F1');
    // Уровня нет или он неполон (стройку удалили) — адреса нет, а не адрес в никуда.
    expect(at(null)).toBeNull();
    expect(at({ scope: 'Section', scopeId: 's-1' })).toBeNull();
    // Экран уровня закрыт своим правом: набор стройки — правом строек, системный — правом наборов.
    expect(at({ scope: 'Section', scopeId: 's-1', constructionId: 'c-1' }, access(['core.datasets.read']))).toBeNull();
    expect(at({ scope: 'System' }, access(['core.constructions.read']))).toBeNull();
    // У других типов экран — экран группы.
    expect(hitRoute(RECORD_ROUTES['Стройка'], null, both)).toBe(RECORD_ROUTES['Стройка']);
    // Строка таблицы уровня не знает — ссылки на набор из неё нет, а не ссылка на системную страницу.
    expect(link('НаборДанных', ID, both)).toBeNull();
  });

  // Комплект и шаблон открываются под другой записью (shell-09, Р-4): адрес знает только карточка «Недавних».
  it('у комплекта и шаблона ссылки из строки таблицы и из палитры нет', () => {
    const all = access(['core.constructions.read', 'id.document.read', 'id.config.edit']);
    // Не `/document-sets/<id комплекта>` — страница стройки, которой нет.
    expect(link('КомплектДокументов', ID, all)).toBeNull();
    expect(link('ШаблонДокумента', ID, all)).toBeNull();
    expect(hitRoute(SET_RECORD, null, all)).toBeNull();
    expect(hitRoute(TEMPLATE_RECORD, null, all)).toBeNull();
  });

  // Двести ссылок страницы с одним именем в списке ссылок читалки экрана неразличимы.
  it('подпись называет запись, когда её есть чем назвать', () => {
    expect(link('СчётНаОплату', ID, reader, 'ООО «Кабель»')!.label).toBe('Открыть счёт: ООО «Кабель»');
    expect(link('СчётНаОплату', ID, reader, '')!.label).toBe('Открыть счёт');
  });

  // Экран без раздела не закрыт ничем: `RequireAccess` пропускает адрес, которого нет в навигации, и
  // ссылка на него досталась бы всем. Поэтому у каждого экрана записи раздел обязан быть.
  it('у каждого экрана записи есть раздел навигации — им экран и закрыт', () => {
    const sections = [...workNav, ...settingsNav].map(item => item.to);
    for (const [type, route] of Object.entries(RECORD_ROUTES))
      expect(sections, `экран типа «${type}»`).toContain(route.screen);
    expect(sections, 'справочник').toContain(CATALOG_RECORD.screen);
  });

  // Карточка позиции в выборе (issue #1517): «Открыть в справочнике» — только тому, кому открыт раздел.
  it('запись справочника открывается на странице общих данных — только с правом её читать', () => {
    expect(recordLink(openTo(CATALOG_RECORD, access(['core.catalog.read'])), ID))
      .toEqual({ to: `/common-data?entry=${ID}`, label: 'Открыть в справочнике' });
    expect(openTo(CATALOG_RECORD, reader)).toBeNull();
  });
});
