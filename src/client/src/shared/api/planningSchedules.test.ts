import { describe, expect, it } from 'vitest';
import { scheduleRefusal } from './planningSchedules';

const error = (status: number, data: object = {}) => ({ response: { status, data } });

describe('отказы графика', () => {
  it.each([[400, 'validation'], [403, 'forbidden'], [409, 'conflict'], [422, 'validation'], [500, 'other']])(
    'разбирает ответ %s', (status, kind) => {
      expect(scheduleRefusal(error(status), 'Отказ').kind).toBe(kind);
    },
  );

  it('различает новый предпросмотр и неподходящий ответ 409', () => {
    const preview = { version: '31', fingerprint: 'next', skipped: { unmapped: 2 },
      added: [], removed: [], changed: [], removedLinks: [] };
    expect(scheduleRefusal(error(409, { error: 'Проверьте разницу', preview }), 'Отказ')).toEqual({
      kind: 'preview-changed', message: 'Проверьте разницу', preview,
    });
    expect(scheduleRefusal(error(409, { preview: { version: '31' } }), 'Отказ').kind).toBe('conflict');
  });

  it('сохраняет имена работ и графиков в отказе при цикле', () => {
    const cycle = [{ id: 'w1', name: 'Монтаж', scheduleId: 's1', scheduleName: 'Электрика' }];
    const result = scheduleRefusal(error(422, { code: 'schedule-cycle', error: 'Цикл', cycle }), 'Отказ');
    expect(result).toEqual({ kind: 'cycle', message: 'Цикл', cycle });
  });
});
