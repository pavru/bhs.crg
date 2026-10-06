import { describe, expect, it } from 'vitest';
import { seenStep, type SeenBase } from './commonData';
import type { CommonDataEntry } from './types';

const entry = (over: Partial<CommonDataEntry> = {}): CommonDataEntry => ({
  id: 'e1', displayName: 'Ромашка', aliases: [], compositeTypeId: 't1', data: { Адрес: 'Москва' },
  scope: 'System', scopeId: null, createdAt: '', updatedAt: '', archived: false, version: '10', ...over,
});

/** Основа формы, собранной по записи. */
const baseOf = (e: CommonDataEntry): SeenBase => ({ version: e.version, entry: e });

describe('seenStep — какую версию форма вправе назвать (issue #1214)', () => {
  it('запись та же — основа та же', () => {
    const e = entry();
    const base = baseOf(e);
    expect(seenStep(base, e)).toBe(base);
  });

  it('версия сдвинулась, содержимое то же (вернули из архива) — основа переезжает', () => {
    const base = baseOf(entry({ archived: true }));
    expect(seenStep(base, entry({ version: '11', archived: false })).version).toBe('11');
  });

  it('содержимое изменили под открытой формой — называется ПРЕЖНЯЯ версия, сервер откажет', () => {
    const base = baseOf(entry());
    expect(seenStep(base, entry({ version: '11', data: { Адрес: 'Тверь' } }))).toBe(base);
    expect(seenStep(base, entry({ version: '12', displayName: 'Лютик' }))).toBe(base);
    expect(seenStep(base, entry({ version: '13', aliases: ['ромашка'] }))).toBe(base);
  });

  it('записи в кэше нет — основа остаётся', () => {
    const base = baseOf(entry());
    expect(seenStep(base, undefined)).toBe(base);
    expect(seenStep(base, null)).toBe(base);
  });
});
