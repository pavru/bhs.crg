import { describe, expect, it } from 'vitest';
import { formatTime } from '@/shared/format/format';
import { isSeries, seriesLabel, type ActivityRow } from './activity';

/** Ряд журнала (ТЗ CORE-28.2): одиночный ряд не раскрывается и подписи «×N» не несёт. */

const ROW: ActivityRow = {
  id: 'r1', occurredAt: '2026-10-28T11:02:00Z', action: 'costs.invoice.changed', actionTitle: 'Счёт изменён',
  actorId: 'a1', actorName: 'Иван', target: 'Счёт № 41', before: null, after: 'номер, дата',
  count: 1, lastAt: '2026-10-28T11:02:00Z', firstTarget: null, sessionId: null,
};

describe('ряд журнала', () => {
  it('из одной записи — без ключа раскрытия и без подписи', () => {
    expect(isSeries(ROW)).toBe(false);
    expect(seriesLabel(ROW)).toBeNull();
  });

  it('серия — число записей и интервал', () => {
    const row = { ...ROW, count: 9, lastAt: '2026-10-28T11:19:00Z', sessionId: 's1' };

    expect(isSeries(row)).toBe(true);
    expect(seriesLabel(row)).toBe(`×9 · ${formatTime(row.occurredAt)}–${formatTime(row.lastAt)}`);
  });

  it('серия в пределах минуты — время одно', () => {
    const row = { ...ROW, count: 2, lastAt: '2026-10-28T11:02:30Z', sessionId: 's1' };

    expect(seriesLabel(row)).toBe(`×2 · ${formatTime(row.occurredAt)}`);
  });
});
