import { describe, it, expect } from 'vitest';
import { DATA_GRID_STATES, cellKind, columnUnavailable, type DataGridStateKind } from './dataGridStates';

const KINDS: DataGridStateKind[] = ['no-data', 'filtered-out', 'module-off', 'no-right', 'removed'];

describe('пять состояний сетки (G1a, #1088)', () => {
  // Состояния сравниваются МЕЖДУ СОБОЙ, а не с ожидаемыми строками: сравнение с образцом прошло бы
  // и на сетке, где остальные четыре показывают тот же текст.
  it.each(['title', 'hint'] as const)('%s попарно различны', part => {
    const texts = KINDS.map(k => DATA_GRID_STATES[k][part]);
    expect(new Set(texts).size).toBe(KINDS.length);
    for (const text of texts) expect(text.trim()).not.toBe('');
  });

  it('объявлены ровно пять', () => {
    expect(Object.keys(DATA_GRID_STATES).sort()).toEqual([...KINDS].sort());
  });

  it('код причины с сервера: известный — принят, неизвестный — колонка обычная', () => {
    expect(columnUnavailable('removed')).toBe('removed');
    expect(columnUnavailable('no-right')).toBe('no-right');
    expect(columnUnavailable('что-то новое')).toBeNull();
    expect(columnUnavailable(null)).toBeNull();
  });
});

describe('клетка: «поля нет» отличимо от «пусто»', () => {
  it('ключа нет в строке — absent', () => {
    expect(cellKind({ a: 1 }, 'b')).toBe('absent');
  });

  it.each([null, undefined, '', '   '])('значение %j — empty', v => {
    expect(cellKind({ a: v }, 'a')).toBe('empty');
  });

  it.each([0, false, 'текст'])('значение %j — value', v => {
    expect(cellKind({ a: v }, 'a')).toBe('value');
  });

  it('поле из прототипа не считается полем строки', () => {
    expect(cellKind({}, 'toString')).toBe('absent');
  });
});
