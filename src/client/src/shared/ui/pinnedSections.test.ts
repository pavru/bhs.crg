import { describe, it, expect } from 'vitest';
import type { AccessInfo } from '@/shared/api/access';
import { visibleNav } from './navAccess';
import { settingsNav, workNav, type NavItem } from './navConfig';
import {
  MAX_PINNED, entryNote, move, parsePinned, pin, pinnedGroup, pinnedRows, rowNote, rowTitle, serializePinned, unpin,
} from './pinnedSections';

const NAV = [...workNav, ...settingsNav];

function access(permissions: string[], modules: { code: string; title?: string; available: boolean }[] = []): AccessInfo {
  return { permissions, modules: modules.map(m => ({ title: m.code, ...m })) };
}

const COSTS = { code: 'costs', title: 'Счета и накладные', available: true };
const PLAN = { code: 'plan', title: 'Планирование', available: true };
const everyone = () => true;

/**
 * Закреплённые страницы (ТЗ AUTH-16.4, work-04). Главное правило здесь — «молча не пропадает ничего»:
 * группа меню показывает только открытое, а профиль — каждое закрепление с причиной. Фильтр меню,
 * удаляющий недоступное из СПИСКА, краснит тесты профиля ниже.
 */
describe('группа «Закреплённые»', () => {
  it('снятое право убирает страницу из группы, остальные на месте в порядке списка', () => {
    const pinned = ['/waybills', '/invoices', '/estimates'];
    const acc = access(['costs.invoice.read', 'plan.estimate.read'], [COSTS, PLAN]);

    const group = pinnedGroup(pinned, visibleNav(NAV, acc), everyone);

    expect(group.map(e => e.item.to)).toEqual(['/invoices', '/estimates']);
  });

  it('адрес вне навигации в группу не попадает', () => {
    const acc = access(['costs.invoice.read'], [COSTS]);
    expect(pinnedGroup(['/reports/brigades', '/invoices'], visibleNav(NAV, acc), everyone).map(e => e.item.to))
      .toEqual(['/invoices']);
  });

  it('страница вне пространства — в группе с пометкой', () => {
    const acc = access(['costs.invoice.read', 'plan.estimate.read'], [COSTS, PLAN]);
    const onlyCosts = (item: NavItem) => item.module === 'costs';

    const group = pinnedGroup(['/estimates', '/invoices'], visibleNav(NAV, acc), onlyCosts);

    expect(group.map(e => [e.item.to, e.outside])).toEqual([['/estimates', true], ['/invoices', false]]);
    expect(entryNote(group[0])).toBe('Планирование · вне пространства');
    expect(entryNote(group[1])).toBe('Учёт затрат');
  });

  it('страница «Администрирования» пометки «вне пространства» не получает: его пространство не сужает', () => {
    const acc = access(['core.users.manage']);
    const group = pinnedGroup(['/users'], visibleNav(NAV, acc), () => false);
    expect(group).toHaveLength(1);
    expect(group[0].outside).toBe(false);
  });
});

describe('профиль «Закреплённые страницы»: шесть видов строки', () => {
  it('недоступная страница остаётся строкой с названным правом', () => {
    const rows = pinnedRows(['/waybills'], NAV, access(['costs.invoice.read'], [COSTS]));
    expect(rows[0].kind).toBe('permission');
    expect(rowTitle(rows[0])).toBe('Накладные');
    expect(rowNote(rows[0])).toBe('Нет доступа: нужно право costs.waybill.read');
  });

  it('модуль не выдан и модуль выключен — разные причины', () => {
    const notGranted = pinnedRows(['/estimates'], NAV, access([], [{ ...PLAN, available: false }]));
    const off = pinnedRows(['/estimates'], NAV, access([], []));

    expect(notGranted[0].kind).toBe('module-not-granted');
    expect(rowNote(notGranted[0])).toBe('Модуль «Планирование» вам не выдан — его выдаёт администратор');
    expect(off[0].kind).toBe('module-off');
    expect(rowNote(off[0])).toBe('Модуль страницы выключен на этом экземпляре');
  });

  it('адрес вне навигации — сам адрес и «страницы больше нет»', () => {
    const [row] = pinnedRows(['/reports/brigades'], NAV, access([]));
    expect(row.kind).toBe('gone');
    expect(rowTitle(row)).toBe('/reports/brigades');
    expect(rowNote(row)).toBe('Страницы больше нет в навигации');
  });

  it('право модуля, которого нет на экземпляре, — причина неизвестна, а не просьба о несуществующем', () => {
    // Пункт без модуля, но с правом модуля, которого нет в ответе: выдать такое право некому.
    const item: NavItem = { to: '/x', label: 'Икс', icon: workNav[0].icon, permission: 'ghost.thing.read', rail: 'data' };
    const [row] = pinnedRows(['/x'], [item], access([]));
    expect(row.kind).toBe('unknown');
    expect(rowNote(row)).toBe('Нет доступа, причина неизвестна');
  });

  it('доступная страница — названием и разделом; право вернули — строка снова «ok» на своём месте', () => {
    const pinned = ['/invoices', '/waybills'];
    const without = pinnedRows(pinned, NAV, access(['costs.invoice.read'], [COSTS]));
    const withBack = pinnedRows(pinned, NAV, access(['costs.invoice.read', 'costs.waybill.read'], [COSTS]));

    expect(without.map(r => r.kind)).toEqual(['ok', 'permission']);
    expect(withBack.map(r => r.kind)).toEqual(['ok', 'ok']);
    expect(rowNote(withBack[1])).toBe('Учёт затрат');
  });

  it('каждое закрепление — строкой: число строк равно длине списка', () => {
    const pinned = ['/invoices', '/waybills', '/gone', '/estimates', '/users'];
    expect(pinnedRows(pinned, NAV, access([])).map(r => r.to)).toEqual(pinned);
  });
});

describe('правка списка', () => {
  it('новое закрепление встаёт последним, повтор и предел список не меняют', () => {
    expect(pin(['/a'], '/b')).toEqual(['/a', '/b']);
    expect(pin(['/a', '/b'], '/a')).toEqual(['/a', '/b']);
    const full = Array.from({ length: MAX_PINNED }, (_, i) => `/p${i}`);
    expect(pin(full, '/new')).toBe(full);
  });

  it('открепить и переставить', () => {
    expect(unpin(['/a', '/b', '/c'], '/b')).toEqual(['/a', '/c']);
    expect(move(['/a', '/b', '/c'], 2, 1)).toEqual(['/a', '/c', '/b']);
    expect(move(['/a', '/b', '/c'], 0, 2)).toEqual(['/b', '/c', '/a']);
    expect(move(['/a'], 0, 1)).toEqual(['/a']);
  });

  it('значение настройки: пустой список снимает ключ; негодное значение — «закреплений нет»', () => {
    expect(serializePinned([])).toBeNull();
    expect(parsePinned(serializePinned(['/a', '/b']))).toEqual(['/a', '/b']);
    expect(parsePinned(undefined)).toEqual([]);
    expect(parsePinned('not json')).toEqual([]);
    expect(parsePinned('{"a":1}')).toEqual([]);
  });
});
