import { describe, expect, it } from 'vitest';
import { assignmentRefusalOf, periodComplete } from './workAssignments';

/**
 * Разбор отказов адресов назначений (change `work-12-assignments-ui`, задача 2.1): вид — по коду и полям ответа,
 * текст — сервера. От вида зависит, где окно покажет причину и предложит ли перечитать.
 */

const refusal = (status: number, data: unknown) => Object.assign(new Error(String(status)), { response: { status, data } });

describe('assignmentRefusalOf', () => {
  it('422 пачки — отказавшие поимённо и текст сервера', () => {
    const r = assignmentRefusalOf(refusal(422, {
      error: 'Назначение не заведено ни одному сотруднику: Мельник С. И. — уволен с 20.12.2026.',
      refused: [{ employeeId: 'e4', name: 'Мельник С. И.', reason: 'уволен с 20.12.2026' }],
    }));
    expect(r).toEqual({
      kind: 'refused',
      error: 'Назначение не заведено ни одному сотруднику: Мельник С. И. — уволен с 20.12.2026.',
      refused: [{ employeeId: 'e4', name: 'Мельник С. И.', reason: 'уволен с 20.12.2026' }],
    });
  });

  it('409 «справочник не заведён» узнаётся по коду, а не по словам', () => {
    const r = assignmentRefusalOf(refusal(409, { code: 'directory-missing', reason: 'not-created', error: 'Справочник «Сотрудник» не заведён.' }));
    expect(r).toEqual({ kind: 'missing', error: 'Справочник «Сотрудник» не заведён.' });
  });

  it('прочий 409 — конфликт с текстом сервера: устаревшая версия, пересечение, последний менеджер', () => {
    for (const error of [
      'Назначение тем временем изменили, и это действие не выполнено. Перечитайте назначения стройки и повторите.',
      'Период пересекается с назначением Белова А. С. 01.11.2026–30.11.2026.',
      'На стройке не останется менеджера.',
    ]) expect(assignmentRefusalOf(refusal(409, { error }))).toEqual({ kind: 'conflict', error });
  });

  it('403 — «назначать может менеджер»: текст сервера, без него — свой', () => {
    expect(assignmentRefusalOf(refusal(403, { error: 'Вы не менеджер этой стройки.' })))
      .toEqual({ kind: 'forbidden', error: 'Вы не менеджер этой стройки.' });
    expect(assignmentRefusalOf(refusal(403, undefined)).error).toMatch(/может её менеджер/);
  });

  it('404 — назначение сняли: свой вид, текст сервера', () => {
    expect(assignmentRefusalOf(refusal(404, { error: 'Назначение не найдено: его сняли или идентификатор неверен.' })))
      .toEqual({ kind: 'gone', error: 'Назначение не найдено: его сняли или идентификатор неверен.' });
  });

  it('400 — текст сервера как есть', () => {
    expect(assignmentRefusalOf(refusal(400, { error: 'Не задано начало периода (dateFrom).' })))
      .toEqual({ kind: 'other', error: 'Не задано начало периода (dateFrom).' });
  });
});

describe('periodComplete', () => {
  it('монтажнику нужен и конец, менеджеру — нет', () => {
    expect(periodComplete('installer', '2026-12-01', '')).toBe(false);
    expect(periodComplete('manager', '2026-12-01', '')).toBe(true);
    expect(periodComplete('installer', '2026-12-01', '2026-12-31')).toBe(true);
  });

  it('без начала и с концом раньше начала — не задан', () => {
    expect(periodComplete('manager', '', '2026-12-31')).toBe(false);
    expect(periodComplete('installer', '2026-12-31', '2026-12-01')).toBe(false);
  });
});
