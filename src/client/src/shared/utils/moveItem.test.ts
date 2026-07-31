import { describe, it, expect } from 'vitest';
import { moveItem } from './moveItem';

describe('moveItem', () => {
  const abc = ['a', 'b', 'c', 'd'];

  it('двигает вверх', () => expect(moveItem(abc, 2, 1)).toEqual(['a', 'c', 'b', 'd']));
  it('двигает вниз', () => expect(moveItem(abc, 1, 2)).toEqual(['a', 'c', 'b', 'd']));

  /** Перетаскивание через несколько строк — не только на соседнюю. */
  it('переносит через несколько позиций', () =>
    expect(moveItem(abc, 0, 3)).toEqual(['b', 'c', 'd', 'a']));

  /**
   * Клик по «выше» у первой строки и «ниже» у последней — обычное дело, а не ошибка. Возвращаем ТУ
   * ЖЕ ссылку: вызывающий сравнивает по ней и не отправляет сохранение впустую.
   */
  it.each([[0, -1], [3, 4], [1, 1]])('выход за границы и на место: тот же массив (%i → %i)', (from, to) =>
    expect(moveItem(abc, from, to)).toBe(abc));

  it('исходный массив не меняется', () => {
    const src = [...abc];
    moveItem(src, 0, 2);
    expect(src).toEqual(abc);
  });
});
