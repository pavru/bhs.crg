import { describe, expect, it } from 'vitest';
import { canPreviewClosing, inverted } from './periods';

/**
 * Перечень закрытия за период «наоборот» не запрашивается (ревью PR #1212). Гонка, из-за которой такой
 * запрос уходил после удавшегося закрытия, на стенде разработчика не воспроизводится — живой прогон
 * ловил её только на раннере. Поэтому условие вынесено в функцию и проверено здесь.
 */
describe('inverted', () => {
  it('период «наоборот» — конец раньше начала; неназванная дата таким не считается', () => {
    expect(inverted('2026-10-01', '2026-09-30')).toBe(true);
    expect(inverted('2026-09-01', '2026-09-30')).toBe(false);
    expect(inverted('2026-09-30', '2026-09-30')).toBe(false);
    expect(inverted('', '2026-09-30')).toBe(false);
    expect(inverted('2026-10-01', '')).toBe(false);
  });
});

describe('canPreviewClosing', () => {
  it('спрашивает перечень, когда обе даты названы и начало не позже конца', () => {
    expect(canPreviewClosing('2026-09-01', '2026-09-30')).toBe(true);
    expect(canPreviewClosing('2026-09-30', '2026-09-30')).toBe(true);
  });

  it('не спрашивает, пока дата не названа', () => {
    expect(canPreviewClosing('', '2026-09-30')).toBe(false);
    expect(canPreviewClosing('2026-09-01', '')).toBe(false);
  });

  it('не спрашивает период «наоборот»: начало сдвинулось за выбранный конец', () => {
    expect(canPreviewClosing('2026-10-01', '2026-09-30')).toBe(false);
  });

  // Диалог объясняет отсутствие перечня по `inverted`. Разойдись условия — перечень был бы выключен
  // молча: ни запроса, ни объяснения.
  it('когда даты названы, перечень выключен ровно тогда, когда диалог говорит «наоборот»', () => {
    const days = ['2026-09-01', '2026-09-30', '2026-10-01'];
    for (const from of days)
      for (const through of days)
        expect(canPreviewClosing(from, through)).toBe(!inverted(from, through));
  });
});
