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

  it('устаревшее остаётся устаревшим, пока человек не перечитает', () => {
    const stale = stepBase(base, '11', 'строки-Б', true);
    expect(stepBase(stale.base, '12', 'строки-В', true).kind).toBe('stale');
    // Перечитал — правок нет: основа свежая.
    expect(stepBase(stale.base, '12', 'строки-В', false).kind).toBe('rebuild');
  });
});
