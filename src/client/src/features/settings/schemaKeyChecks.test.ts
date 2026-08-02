import { describe, it, expect } from 'vitest';
import { boundedEditDistance, similarKeyOf, danglingKeyRefs, danglingRefPlaces } from './schemaKeyChecks';

/**
 * Проверки ключей схемы (issue #639). Случаи взяты с живых данных: в типе АОСР годами жил ключ
 * `ДатаДокумнета` рядом с настоящим `ДатаДокумента`, а поле `ДополнительнеОрганизации` — с опечаткой
 * в самом ключе.
 */
describe('расстояние с ограничением', () => {
  it('перестановка соседних букв — ОДНО различие', () => {
    // Ровно тот случай: по обычному Левенштейну было бы два, и порог в одно различие пропустил бы
    // опечатку, ради которой проверка и заводилась.
    expect(boundedEditDistance('документа', 'докумнета', 1)).toBe(1);
  });

  it('пропуск и замена буквы — одно различие', () => {
    expect(boundedEditDistance('дополнительные', 'дополнительне', 1)).toBe(1);
    expect(boundedEditDistance('материалы', 'материллы', 1)).toBe(1);
  });

  it('различий больше предела — отказ, а не число', () => {
    expect(boundedEditDistance('датадокумента', 'номердокумента', 1)).toBe(-1);
    expect(boundedEditDistance('работы', 'схемы', 1)).toBe(-1);
  });

  it('одинаковые строки — ноль', () => {
    expect(boundedEditDistance('ключ', 'ключ', 1)).toBe(0);
  });
});

describe('похожий ключ', () => {
  const keys = ['ДатаДокумента', 'НомерДокумента', 'ВыпустившаяОрганизация', 'Кол'];

  it('находит двойника с переставленными буквами', () => {
    expect(similarKeyOf('ДатаДокумнета', keys)).toBe('ДатаДокумента');
  });

  it('разный регистр — разные ключи, и различить их глазами нельзя', () => {
    expect(similarKeyOf('датадокумента', keys)).toBe('ДатаДокумента');
  });

  it('сам себя похожим не считает', () => {
    expect(similarKeyOf('ДатаДокумента', keys)).toBeNull();
  });

  it('законно разные ключи молчат', () => {
    expect(similarKeyOf('ДатаНачалаРабот', keys)).toBeNull();
    expect(similarKeyOf('Материалы', keys)).toBeNull();
  });

  it('короткие ключи не сравниваем: «Код» и «Кол» оба законны', () => {
    expect(similarKeyOf('Код', keys)).toBeNull();
  });

  it('пустой ключ — не повод для догадок', () => {
    expect(similarKeyOf('   ', keys)).toBeNull();
  });
});

describe('висячие ссылки на поля', () => {
  const groups = [
    { key: 'g1', title: 'Реквизиты', fieldKeys: ['ДатаДокумента', 'ДатаДокумнета'] },
    { key: 'g2', title: 'Работы', fieldKeys: ['Работы'] },
  ];

  it('ключ без поля виден вместе с местом, где встретился', () => {
    const refs = danglingKeyRefs(groups, ['ДатаДокумнета', 'ПериодДействия'],
      ['ДатаДокумента', 'Работы', 'ПериодДействия']);

    const dangling = refs.find(r => r.key === 'ДатаДокумнета');
    expect(refs).toHaveLength(1);
    expect(dangling!.groups).toEqual(['Реквизиты']);
    expect(dangling!.excluded).toBe(true);
    expect(danglingRefPlaces(dangling!)).toBe('в группе «Реквизиты», в исключениях');
  });

  it('исключённое родительское поле висячим НЕ является — в этом смысл исключения', () => {
    // knownKeys — полный набор родительских ключей ДО исключений, иначе каждое исключение
    // объявляло бы себя же ошибкой.
    expect(danglingKeyRefs([], ['ПериодДействия'], ['ПериодДействия'])).toEqual([]);
  });

  it('здоровая схема молчит', () => {
    expect(danglingKeyRefs(groups.slice(1), [], ['Работы'])).toEqual([]);
  });
});
