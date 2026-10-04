import { describe, it, expect } from 'vitest';
import { RECORD_ROUTES, recordLink, recordScreen } from './recordRoutes';
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
  });
});
