import { describe, it, expect } from 'vitest';
import { toggleInSet } from './toggleInSet';

describe('toggleInSet', () => {
  it('добавляет отсутствующее', () => {
    expect([...toggleInSet(new Set(['a']), 'b')]).toEqual(['a', 'b']);
  });

  it('убирает присутствующее', () => {
    expect([...toggleInSet(new Set(['a', 'b']), 'a')]).toEqual(['b']);
  });

  it('исходное множество не трогает — иначе React не увидел бы смены состояния', () => {
    const before = new Set(['a']);
    const after = toggleInSet(before, 'b');
    expect([...before]).toEqual(['a']);
    expect(after).not.toBe(before);
  });

  it('два переключения подряд возвращают исходный состав', () => {
    const once = toggleInSet(new Set(['a']), 'b');
    expect([...toggleInSet(once, 'b')]).toEqual(['a']);
  });
});
