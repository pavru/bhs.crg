import { describe, expect, it } from 'vitest';
import { costHidden, planRefusal } from './planningEstimates';

const refused = (status: number, data?: unknown) => ({ response: { status, data }, message: `Request failed with status code ${status}` });

/** Разбор отказов сервера смет (plan-09, задача 2.1, ТЗ PLAN-5.2). */
describe('planRefusal', () => {
  it('текст сервера — первым: он написан для человека', () => {
    expect(planRefusal(refused(409, { error: 'Смету тем временем изменили, и это действие не выполнено.' })))
      .toEqual({ kind: 'conflict', text: 'Смету тем временем изменили, и это действие не выполнено.' });
    expect(planRefusal(refused(400, { error: 'Не названа версия' }))).toEqual({ kind: 'invalid', text: 'Не названа версия' });
  });

  it('403 от ворот права приходит без тела — своя формулировка, а не «status code 403»', () => {
    const r = planRefusal(refused(403, ''));
    expect(r.kind).toBe('forbidden');
    expect(r.text).not.toMatch(/status code/);
    expect(r.text).toMatch(/права/);
  });

  it('409 без текста — «тем временем изменили», 404 — «удалили»', () => {
    expect(planRefusal(refused(409)).text).toMatch(/тем временем изменили/);
    expect(planRefusal(refused(404))).toMatchObject({ kind: 'gone' });
  });

  it('сырая строка в теле — тоже текст сервера', () => {
    expect(planRefusal(refused(409, 'Редакцию удалили'))).toEqual({ kind: 'conflict', text: 'Редакцию удалили' });
  });

  it('сеть без ответа — «не получилось», с сообщением ошибки', () => {
    expect(planRefusal({ message: 'Network Error' })).toEqual({ kind: 'failed', text: 'Network Error' });
  });
});

describe('costHidden', () => {
  it('находит носитель себестоимости и молчит без него', () => {
    const part = { code: 'cost', permission: 'plan.estimate.cost', name: 'видеть себестоимость сметы' };
    expect(costHidden([part])).toBe(part);
    expect(costHidden(undefined)).toBeNull();
    expect(costHidden([{ ...part, code: 'other' }])).toBeNull();
  });
});
