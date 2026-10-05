import { describe, expect, it } from 'vitest';
import { stepBase } from './draftBase';

const base = { version: '10', signature: 'строки-А' };

describe('основа черновика', () => {
  it('тот же вид — та же основа', () => {
    expect(stepBase(base, '10', 'строки-А', true)).toEqual({ kind: 'same', base });
  });

  // Сосед поправил шапку, я правлю строки: версия у счёта одна, и она ушла. Строки при этом те же —
  // мой черновик собран по тому, что лежит в базе, и назвать новую версию честно.
  it('версия ушла, часть та же — основа переезжает, правки целы', () => {
    expect(stepBase(base, '11', 'строки-А', true))
      .toEqual({ kind: 'same', base: { version: '11', signature: 'строки-А' } });
  });

  it('часть изменилась, правок нет — черновик пересобирается', () => {
    expect(stepBase(base, '11', 'строки-Б', false))
      .toEqual({ kind: 'rebuild', base: { version: '11', signature: 'строки-Б' } });
  });

  // Ради этого случая всё и заведено: назови форма свежую версию — сервер принял бы набор строк,
  // собранный по прежним, и затёр бы чужие.
  it('часть изменилась под правками — основа прежняя, и версию называют прежнюю', () => {
    expect(stepBase(base, '11', 'строки-Б', true)).toEqual({ kind: 'stale', base });
  });

  // Шапка: подпись — реквизиты целиком, а «моя часть» — только правленые поля (ревью PR #1208).
  // Подпись из одних правленых полей менялась с первой же правкой, без смены версии: основа помнила
  // «правок нет», и чужая правка строк читалась как правка шапки.
  describe('своё сравнение подписей', () => {
    const fields = { version: '10', signature: { 'Номер': 'А-1', 'Назначение': 'кабель' } as Record<string, unknown> };
    const mine = (keys: string[]) => (a: Record<string, unknown>, b: Record<string, unknown>) =>
      keys.every(key => a[key] === b[key]);

    it('версия ушла, моих полей не трогали — основа переезжает', () => {
      const next = { 'Номер': 'А-1', 'Назначение': 'труба' };
      expect(stepBase(fields, '11', next, true, mine(['Номер'])))
        .toEqual({ kind: 'same', base: { version: '11', signature: next } });
    });

    it('изменили поле, которое я правлю, — устарело', () => {
      const next = { 'Номер': 'Б-2', 'Назначение': 'кабель' };
      expect(stepBase(fields, '11', next, true, mine(['Номер'])).kind).toBe('stale');
    });
  });

  it('устаревшее остаётся устаревшим, пока человек не перечитает', () => {
    const stale = stepBase(base, '11', 'строки-Б', true);
    expect(stepBase(stale.base, '12', 'строки-В', true).kind).toBe('stale');
    // Перечитал — правок нет: основа свежая.
    expect(stepBase(stale.base, '12', 'строки-В', false).kind).toBe('rebuild');
  });
});
