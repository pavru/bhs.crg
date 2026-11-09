import { describe, expect, it } from 'vitest';
import { distributionRefusal } from './planningDistribution';

const refused = (status: number, data?: unknown) => ({ response: { status, data }, message: `Request failed with status code ${status}` });

/** Разбор отказов сервера распределения (plan-10, задача 2.2, ТЗ PLAN-5.1, COST-13). */
describe('distributionRefusal', () => {
  it('400 balance — строки с обеими единицами, текст сервера первым', () => {
    const error = {
      rowId: 'r1', number: '13', name: 'Кабель', code: 'over', sum: { row: 13, plan: 1300 }, volume: { row: 12, plan: 1200 },
      unit: 'row', text: 'строка 13: 13 · 100 м = 1 300 м при 1 200 м',
    };
    const r = distributionRefusal(refused(400, { error: 'Распределение не записано — ни одна строка: …', code: 'balance', errors: [error] }));
    expect(r).toEqual({ kind: 'balance', text: 'Распределение не записано — ни одна строка: …', errors: [error] });
  });

  it('400 estimate-section — название раздела', () => {
    const r = distributionRefusal(refused(400, { error: 'Смета составлена на раздел «Корпус 1»', code: 'estimate-section', sectionId: 's', sectionName: 'Корпус 1' }));
    expect(r).toEqual({ kind: 'estimate-section', text: 'Смета составлена на раздел «Корпус 1»', sectionName: 'Корпус 1' });
  });

  it('прочие 400 — как отказ запроса', () => {
    expect(distributionRefusal(refused(400, { error: 'Набор строк пуст' }))).toEqual({ kind: 'invalid', text: 'Набор строк пуст' });
  });

  it('409 — кто и когда изменил, для «перечитайте»', () => {
    const r = distributionRefusal(refused(409, {
      error: 'Распределение редакции тем временем изменено — Петрова О.', code: 'distribution-changed', version: '812',
      changedBy: 'Петрова О.', changedAt: '2026-11-09T10:42:00Z',
    }));
    expect(r).toEqual({
      kind: 'conflict', text: 'Распределение редакции тем временем изменено — Петрова О.', changedBy: 'Петрова О.',
      changedAt: '2026-11-09T10:42:00Z',
    });
  });

  it('409 без тела — своя фраза «тем временем изменили», без «кто»', () => {
    const r = distributionRefusal(refused(409));
    expect(r).toMatchObject({ kind: 'conflict', changedBy: null, changedAt: null });
    expect(r.text).toMatch(/тем временем изменили/);
  });

  it('403 от ворот права без тела — про право, а не «status code 403»', () => {
    const r = distributionRefusal(refused(403, ''));
    expect(r.kind).toBe('forbidden');
    expect(r.text).not.toMatch(/status code/);
    expect(r.text).toMatch(/права/);
  });

  it('404 — «удалили», сеть без ответа — сообщение ошибки', () => {
    expect(distributionRefusal(refused(404))).toMatchObject({ kind: 'gone' });
    expect(distributionRefusal({ message: 'Network Error' })).toEqual({ kind: 'failed', text: 'Network Error' });
  });
});
