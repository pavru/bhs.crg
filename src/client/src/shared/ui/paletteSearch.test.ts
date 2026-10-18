import { describe, it, expect } from 'vitest';
import { highlight, matches, queryWords, ranked } from './paletteSearch';

/** Поиск командной палитры по словам (issue #1433). */
describe('поиск палитры', () => {
  it('слова в любом порядке, без регистра и без различия «ё»', () => {
    expect(matches('Тема: тёмная', '', queryWords('тёмная тема'))).toBe(true);
    expect(matches('Новый счёт', '', queryWords('НОВЫЙ счет'))).toBe(true);
    expect(matches('Новый счёт', '', queryWords('новый накладная'))).toBe(false);
  });

  it('пункт находится и словами поиска, а не только подписью', () => {
    expect(matches('Счёт из файла', 'загрузить скан pdf', queryWords('загрузить скан'))).toBe(true);
  });

  it('пустой запрос подходит всем', () => {
    expect(matches('Выйти', '', queryWords('   '))).toBe(true);
  });

  it('подсвечиваются совпадения в подписи, сливаясь в один кусок', () => {
    expect(highlight('Тема: тёмная', queryWords('тем ёмн'))).toEqual([
      { text: 'Тем', hit: true }, { text: 'а: ', hit: false }, { text: 'тёмн', hit: true }, { text: 'ая', hit: false },
    ]);
    expect(highlight('Выйти', [])).toEqual([{ text: 'Выйти', hit: false }]);
  });

  /** Enter исполняет первый пункт: «счета» ведёт в раздел, а не заводит черновик (у него «счета» в словах). */
  it('совпадение подписью выше совпадения словами; группа целиком идёт за лучшим пунктом', () => {
    const items = [
      { label: 'Новый счёт', group: 'Действия' },
      { label: 'Тема: светлая', group: 'Действия' },
      { label: 'Стройки', group: 'Стройки' },
      { label: 'Накладные', group: 'Учёт' },
      { label: 'Счета', group: 'Учёт' },
    ];
    expect(ranked(items, queryWords('счета')).map(i => i.label))
      .toEqual(['Счета', 'Накладные', 'Новый счёт', 'Тема: светлая', 'Стройки']);
    expect(ranked(items, []).map(i => i.label)).toEqual(items.map(i => i.label));
  });
});
