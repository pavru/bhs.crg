import { describe, expect, it } from 'vitest';
import cases from './siteFilterCases.json';
import { filterOn, filterState, isVisible, parseFavorites, serializeFavorites } from './rule';

describe('правило видимости избранных строек — общий файл случаев', () => {
  const live = new Set(cases.live);

  // Положительный якорь: пустой файл случаев прошёл бы любой цикл ниже, ничего не проверив.
  it('файл случаев прочитан', () => {
    expect(cases.cases.length).toBeGreaterThanOrEqual(10);
  });

  for (const c of cases.cases) {
    it(c.name, () => {
      expect(isVisible(c.sites, filterState(c.favorites, c.on, live))).toBe(c.visible);
    });
  }
});

describe('настройки избранного', () => {
  it('нет ключа — избранного нет и фильтр включён', () => {
    expect(parseFavorites(undefined)).toEqual([]);
    expect(filterOn(undefined)).toBe(true);
    expect(filterOn('off')).toBe(false);
  });

  it('пустой список не хранится — ключ снимается', () => {
    expect(serializeFavorites([])).toBeNull();
    expect(serializeFavorites(['a', 'b'])).toBe('a,b');
  });

  it('одиночная стройка объекта — то же, что список из одной', () => {
    const state = filterState(['A'], true, new Set(['A', 'B']));
    expect(isVisible('B', state)).toBe(false);
    expect(isVisible(null, state)).toBe(true);
  });
});
