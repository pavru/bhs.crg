import { describe, it, expect } from 'vitest';
import { RECORD_ROUTES, recordLink } from './recordRoutes';
import { workNav, settingsNav } from './navConfig';
import type { AccessInfo } from '@/shared/api/access';

const costs = { code: 'costs', title: 'Счета и накладные', available: true };
const access = (permissions: string[], modules = [costs]): AccessInfo => ({ permissions, modules });
const ID = '3fa85f64-5717-4562-b3fc-2c963f66afa6';

/**
 * Переход из строки таблицы в форму записи (задача G4, issue #1097).
 *
 * ⚠️ Главное здесь — отказы: ссылка, которой не положено быть, обязана не появиться. Появившаяся
 * ведёт на «Раздел недоступен» — то есть обещает переход, которого не будет.
 */
describe('адрес записи по типу', () => {
  it('счёт открывается на экране счетов, названный параметром адреса', () => {
    expect(recordLink('СчётНаОплату', ID, access(['costs.invoice.read'])))
      .toEqual({ to: `/invoices?invoice=${ID}`, label: 'Открыть счёт' });
  });

  it('без права на счета ссылки нет — модуль открыт, а экран счетов закрыт', () => {
    expect(recordLink('СчётНаОплату', ID, access(['costs.waybill.read']))).toBeNull();
  });

  it('без модуля ссылки нет, хотя право записано', () => {
    expect(recordLink('СчётНаОплату', ID, access(['costs.invoice.read'], []))).toBeNull();
    expect(recordLink('СчётНаОплату', ID, access(['costs.invoice.read'], [{ ...costs, available: false }]))).toBeNull();
  });

  it('у типа без экрана и у таблицы без типа ссылки нет', () => {
    const all = access(['costs.invoice.read']);
    expect(recordLink('ТипБезЭкрана', ID, all)).toBeNull();
    expect(recordLink(null, ID, all)).toBeNull();
    expect(recordLink(undefined, ID, all)).toBeNull();
  });

  it('без ключа строки ссылки нет', () => {
    expect(recordLink('СчётНаОплату', null, access(['costs.invoice.read']))).toBeNull();
  });

  it('ключ в адрес ложится значением параметра, а не куском адреса', () => {
    const link = recordLink('СчётНаОплату', 'a&b=c#d', access(['costs.invoice.read']));
    expect(new URL(link!.to, 'http://x').searchParams.get('invoice')).toBe('a&b=c#d');
    expect(new URL(link!.to, 'http://x').hash).toBe('');
  });

  // Экран без раздела не закрыт ничем: `RequireAccess` пропускает адрес, которого нет в навигации, и
  // ссылка на него досталась бы всем. Поэтому у каждого экрана записи раздел обязан быть.
  it('у каждого экрана записи есть раздел навигации — им экран и закрыт', () => {
    const sections = [...workNav, ...settingsNav].map(item => item.to);
    for (const [type, route] of Object.entries(RECORD_ROUTES))
      expect(sections, `экран типа «${type}»`).toContain(route.screen);
  });
});
