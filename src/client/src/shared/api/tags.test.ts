import { describe, it, expect } from 'vitest';
import { tagCode, tagOrder, hasTag, findTagEntry, withTagOrder } from './tags';

/**
 * Разбор записи тэга «код» / «код:параметр» (issue #583). Правила совпадают с серверным TagCode —
 * те же случаи проверены в TagCodeTests: разойдись разбор, поле с номером считалось бы
 * отмеченным на одной стороне и неотмеченным на другой.
 */
describe('разбор записи тэга', () => {
  it('запись без параметра остаётся собой', () => {
    expect(tagCode('identity')).toBe('identity');
    expect(tagOrder('identity')).toBeNull();
  });

  it('«код:номер» разбирается на код и номер', () => {
    expect(tagCode('identity:2')).toBe('identity');
    expect(tagOrder('identity:2')).toBe(2);
  });

  it('точка — часть кода тэга, а не разделитель параметра', () => {
    expect(tagCode('material.qualityDocLink')).toBe('material.qualityDocLink');
  });

  it('пробелы вокруг обеих частей срезаются', () => {
    expect(tagCode(' identity : 3 ')).toBe('identity');
    expect(tagOrder(' identity : 3 ')).toBe(3);
  });

  // Опечатка в номере не должна молча отключать поле от сопоставления: тэг работает, просто без номера.
  it.each(['identity:', 'identity:первый', 'identity:-1', 'identity:1.5'])('негодный параметр «%s» = без номера', raw => {
    expect(tagCode(raw)).toBe('identity');
    expect(tagOrder(raw)).toBeNull();
  });
});

describe('поиск тэга по коду', () => {
  it('находит тэг и с параметром, и без', () => {
    expect(hasTag(['identity'], 'identity')).toBe(true);
    expect(hasTag(['identity:2'], 'identity')).toBe(true);
    expect(hasTag(['doc.number'], 'identity')).toBe(false);
    expect(hasTag(undefined, 'identity')).toBe(false);
  });

  it('отдаёт запись как она лежит в схеме — из неё читается номер', () => {
    expect(findTagEntry(['doc.number', 'identity:2'], 'identity')).toBe('identity:2');
    expect(findTagEntry(['doc.number'], 'identity')).toBeUndefined();
  });
});

describe('правка номера', () => {
  it('меняет номер, не трогая остальные тэги и их порядок', () => {
    expect(withTagOrder(['doc.number', 'identity:2'], 'identity', 1)).toEqual(['doc.number', 'identity:1']);
  });

  it('пустой номер убирает параметр, а сам тэг оставляет', () => {
    expect(withTagOrder(['identity:2'], 'identity', null)).toEqual(['identity']);
  });

  it('непроставленный тэг не появляется', () => {
    expect(withTagOrder(['doc.number'], 'identity', 1)).toEqual(['doc.number']);
  });
});
