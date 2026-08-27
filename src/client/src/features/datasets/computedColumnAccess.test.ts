import { describe, it, expect } from 'vitest';
import { columnAccessor } from './computedColumnAccess';

/**
 * Чем вставлять обращение к колонке (issue #539). Правило одно: заголовок, годный в имя переменной,
 * пишем как есть, остальные — через get("…"). Именно «остальные» и были недоступны: заголовок «1»
 * превращался в переменную `_1`, о которой узнать было неоткуда, а выражение `1` тихо возвращало
 * число.
 */
describe('columnAccessor', () => {
  it('обычное имя вставляется как есть', () => {
    expect(columnAccessor('Фамилия')).toBe('Фамилия');
    expect(columnAccessor('Total_2')).toBe('Total_2');
  });

  it('числовой заголовок — через get (иначе это просто число)', () => {
    expect(columnAccessor('1')).toBe('get("1")');
  });

  it('пробелы и знаки — через get, а НЕ через просанированное имя', () => {
    // «Полное Имя» доступно и как переменная `Полное_Имя`, но подсказывать надо точный заголовок:
    // просанированные имена сталкиваются между собой («№» и «%» оба дают «_»).
    expect(columnAccessor('Полное Имя')).toBe('get("Полное Имя")');
    expect(columnAccessor('Кол-во')).toBe('get("Кол-во")');
    expect(columnAccessor('№')).toBe('get("№")');
  });

  it('кавычки в заголовке экранируются', () => {
    expect(columnAccessor('Тип "А"')).toBe('get("Тип \\"А\\"")');
  });

  it('пустой заголовок не притворяется идентификатором', () => {
    expect(columnAccessor('')).toBe('get("")');
  });

  it('ключевые слова и литералы — через get, хотя выглядят идентификаторами', () => {
    // «for» ломает разбор выражения (вся колонка молча пустеет), «null» даёт литерал вместо ячейки.
    expect(columnAccessor('for')).toBe('get("for")');
    expect(columnAccessor('new')).toBe('get("new")');
    expect(columnAccessor('class')).toBe('get("class")');
    expect(columnAccessor('null')).toBe('get("null")');
    expect(columnAccessor('true')).toBe('get("true")');
    expect(columnAccessor('undefined')).toBe('get("undefined")');
  });

  it('похожее на ключевое слово, но не оно — как есть', () => {
    expect(columnAccessor('format')).toBe('format');
    expect(columnAccessor('Null')).toBe('Null');
  });
});
