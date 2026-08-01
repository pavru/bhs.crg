import { describe, it, expect } from 'vitest';
import { docState, EXPIRING_SOON_DAYS } from './docState';

const NOW = new Date('2026-08-01T12:00:00Z');
/** Точный момент, а не дата: у даты без времени граница «ровно порог» плавает на полдня. */
const inDays = (n: number) => new Date(NOW.getTime() + n * 86_400_000).toISOString();

describe('docState (issue #555)', () => {
  it('документ без связок — «без связок», даже если срок в порядке', () => {
    expect(docState(inDays(1000), 0, NOW)).toBe('unlinked');
  });

  /**
   * Главное состояние экрана: просроченный документ с ЖИВЫМИ связками. Подсказки просроченные
   * прячут, но уже созданные связки продолжают подмешиваться в PDF — этого не видно нигде.
   */
  it('просрочен и связки живы', () => {
    expect(docState(inDays(-1), 3, NOW)).toBe('expired');
    expect(docState('2026-02-28', 3, NOW)).toBe('expired');
  });

  it('истекает в пределах порога', () => {
    expect(docState(inDays(1), 1, NOW)).toBe('expiring');
    expect(docState(inDays(EXPIRING_SOON_DAYS), 1, NOW)).toBe('expiring');
  });

  it('за порогом — состояния нет', () => {
    expect(docState(inDays(EXPIRING_SOON_DAYS + 1), 1, NOW)).toBeNull();
  });

  /** Система хранит даты без времени — на такой границе допускаем сдвиг в пределах суток. */
  it('дата без времени у порога считается «истекает»', () => {
    const dateOnly = new Date(NOW.getTime() + EXPIRING_SOON_DAYS * 86_400_000).toISOString().slice(0, 10);
    expect(docState(dateOnly, 1, NOW)).toBe('expiring');
  });

  /**
   * Неизвестный срок — не «действует». В живой базе срок лежит в реквизите, которого нет ни в одном
   * поле схемы (#558), поэтому тэг не резолвится; считать такой документ действующим значило бы
   * утверждать факт, которого не знаешь.
   */
  it('срок неизвестен — состояния нет, но и «в порядке» не утверждаем', () => {
    expect(docState(null, 5, NOW)).toBeNull();
    expect(docState('', 5, NOW)).toBeNull();
  });

  it('неразбираемая дата не выдаётся за просрочку', () => {
    expect(docState('не указан', 5, NOW)).toBeNull();
  });
});
