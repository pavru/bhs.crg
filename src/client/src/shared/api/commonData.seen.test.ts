import { describe, expect, it } from 'vitest';
import { recordContent, seenStep } from './commonData';
import type { CommonDataEntry } from './types';

const entry = (over: Partial<CommonDataEntry> = {}): CommonDataEntry => ({
  id: 'e1', displayName: 'Ромашка', aliases: [], compositeTypeId: 't1', data: { Адрес: 'Москва' },
  scope: 'System', scopeId: null, createdAt: '', updatedAt: '', archived: false, version: '10', ...over,
});

/** Основа формы, собранной по записи. */
const baseOf = (e: CommonDataEntry) => ({ version: e.version, content: recordContent(e) });

describe('seenStep — какую версию форма вправе назвать (issue #1214)', () => {
  it('запись та же — основа та же', () => {
    const e = entry();
    const base = baseOf(e);
    expect(seenStep(base, e, recordContent(e))).toBe(base);
  });

  it('версия сдвинулась, содержимое то же (вернули из архива) — основа переезжает', () => {
    const base = baseOf(entry());
    const returned = entry({ version: '11', archived: false });
    expect(seenStep(base, returned, recordContent(returned)).version).toBe('11');
  });

  it('содержимое изменили под открытой формой — называется ПРЕЖНЯЯ версия, сервер откажет', () => {
    const base = baseOf(entry());
    const edited = entry({ version: '11', data: { Адрес: 'Тверь' } });
    expect(seenStep(base, edited, recordContent(edited))).toBe(base);
    const renamed = entry({ version: '12', displayName: 'Лютик' });
    expect(seenStep(base, renamed, recordContent(renamed))).toBe(base);
  });

  it('новая запись — основы нет, шаг её не выдумывает', () => {
    const base = { version: '', content: '' };
    expect(seenStep(base, null, '')).toBe(base);
  });
});
