import { describe, expect, it } from 'vitest';
import type { WaybillVersionView, WaybillView } from '@/shared/api/waybills';
import {
  contentOfView, correctionEvents, correctionMarkText, mayCorrectWaybill, versionChanges,
} from './waybillCorrection';

const view: WaybillView = {
  id: 'w1', version: '9', number: 'РН-7', issuedOn: '2026-06-09', warehouse: 'Основной', constructionId: 'c1',
  constructionName: 'ЖК «Север»', constructionLost: false, receivedBy: null, note: null,
  state: 'Posted', postedAt: '2026-06-09T10:00:00Z', accountingOn: '2026-06-09',
  locked: 'июнь закрыт по стройке', currentVersion: 3,
  lines: [
    { id: 'l1', ordinal: 1, nomenclatureId: 'n1', nomenclatureName: 'Кабель', nomenclatureLost: false,
      sourceText: 'Кабель ВВГ', unit: 'м', quantity: 7, note: null },
  ],
  totals: { count: 1, unmatched: 0 },
};

const version = (number: number, quantity: number, reason: string, extra?: Partial<WaybillVersionView['content']>): WaybillVersionView => ({
  number, reason, authorId: 'u1', authorName: 'Иванов', at: `2026-07-0${number}T09:00:00Z`,
  content: {
    ...contentOfView(view), ...extra,
    lines: [{ id: 'l1', ordinal: 1, nomenclatureId: 'n1', sourceText: 'Кабель ВВГ', unit: 'м', quantity, note: null }],
  },
});

describe('исправление накладной', () => {
  it('кнопка — только у запертой проведённой и только с правкой и правом исправления', () => {
    const all = () => true;
    expect(mayCorrectWaybill(view, all, false)).toBe(true);
    expect(mayCorrectWaybill(view, code => code !== 'costs.waybill.correct', false)).toBe(false);
    expect(mayCorrectWaybill(view, code => code !== 'costs.waybill.edit', false)).toBe(false);
    expect(mayCorrectWaybill({ ...view, locked: null }, all, false)).toBe(false);
    expect(mayCorrectWaybill({ ...view, state: 'Draft' }, all, false)).toBe(false);
    // Накладной больше нет — исправлять нечего.
    expect(mayCorrectWaybill(view, all, true)).toBe(false);
  });

  it('пометка списка называет день и причину', () => {
    expect(correctionMarkText({ at: '2026-10-21T08:00:00Z', reason: 'Пересчёт по факту' }))
      .toBe('исправлена 21.10.2026 · Пересчёт по факту');
  });

  it('называет, что исправление поменяло, — строки по идентификатору', () => {
    const prior = contentOfView(view);
    const next = {
      ...prior, warehouse: 'Второй', constructionId: 'c2',
      lines: [
        { ...prior.lines[0], quantity: 5, note: 'по факту' },
        { id: 'l9', ordinal: 2, nomenclatureId: null, sourceText: 'Хомут', unit: 'шт', quantity: 2, note: null },
      ],
    };
    expect(versionChanges(prior, next)).toEqual([
      'склад: Основной → Второй', 'стройка сменена',
      'строка 1: количество 7 → 5', 'строка 1: примечание изменено', 'добавлена строка',
    ]);
    expect(versionChanges(next, prior)).toContain('строка 2 удалена');
    expect(versionChanges(prior, prior)).toEqual([]);
  });

  it('событие сверяет версию со следующей, а последнюю — с действующим содержимым', () => {
    // Версии свежими сверху: 2 (было 8, стало 7 — действующее), 1 (было 10, стало 8 — версия 2).
    const events = correctionEvents([version(2, 8, 'Ещё раз'), version(1, 10, 'Пересчёт')], view);
    expect(events.map(e => [e.version, e.reason, e.changes])).toEqual([
      [2, 'Ещё раз', ['строка 1: количество 8 → 7']],
      [1, 'Пересчёт', ['строка 1: количество 10 → 8']],
    ]);
    expect(events[0].author).toBe('Иванов');
  });
});
