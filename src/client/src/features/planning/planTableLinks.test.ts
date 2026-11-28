import { describe, expect, it } from 'vitest';
import { parseView } from '@/features/tables/tableViewState';
import { offersTableLink, offerTableLink, scheduleTableLink } from './planTableLinks';

function address(link: string) {
  const url = new URL(link, 'http://localhost');
  return { path: url.pathname, filter: parseView(url.hash).filter };
}

describe('Открыть таблицей', () => {
  it('открывает текущую версию выбранного КП по его номеру', () => {
    expect(address(offerTableLink('КП-2026-014'))).toEqual({
      path: '/tables/plan.offers/registry',
      filter: { type: 'condition', column: 'Номер', op: 'eq', value: 'КП-2026-014' },
    });
  });

  it('переносит в реестр КП выбранные стройки', () => {
    expect(address(offersTableLink([])).filter).toBeNull();
    expect(address(offersTableLink(['site-id-1', 'site-id-2'])).filter).toEqual({
      type: 'condition', column: 'КлючСтройки', op: 'in', values: ['site-id-1', 'site-id-2'],
    });
  });

  it('сохраняет график, раздел и код предупреждения', () => {
    const schedule = { type: 'condition', column: 'КлючГрафика', op: 'eq', value: 'schedule-id' };
    expect(address(scheduleTableLink('schedule-id', 'section-id', 'no-dates'))).toEqual({
      path: '/tables/plan.schedule-works/works',
      filter: { type: 'group', logic: 'and', children: [
        schedule,
        { type: 'condition', column: 'КлючРаздела', op: 'eq', value: 'section-id' },
        { type: 'condition', column: 'Предупреждения', op: 'contains', value: 'no-dates' },
      ] },
    });
    expect(address(scheduleTableLink('schedule-id', undefined, null)).filter).toEqual(schedule);
    expect(address(scheduleTableLink('schedule-id', null, null)).filter).toEqual({
      type: 'group', logic: 'and', children: [
        schedule,
        { type: 'condition', column: 'КлючРаздела', op: 'is_empty' },
      ],
    });
  });
});
