import { describe, it, expect } from 'vitest';
import { isMissing, validateConstraint } from './fieldValidation';
import type { SchemaField } from '@/shared/api/schema';
import type { PrimitiveTypeDef, FieldConstraints } from '@/shared/api/types';

function field(over: Partial<SchemaField>): SchemaField {
  return { key: 'k', title: 'Поле', type: 'string', required: true, ...over } as SchemaField;
}

function def(baseType: PrimitiveTypeDef['baseType'], constraints: FieldConstraints): PrimitiveTypeDef {
  return {
    id: 'p1', name: 'Тип', code: 'T', baseType, constraints,
    allowedTags: [], group: null, createdAt: '', updatedAt: '',
  };
}

describe('незаполненное обязательное поле', () => {
  it('необязательное пустым быть вправе', () => {
    expect(isMissing(field({ required: false }), '')).toBe(false);
  });

  it('пустая строка и пробелы — это «не заполнено»', () => {
    expect(isMissing(field({}), '')).toBe(true);
    expect(isMissing(field({}), '   ')).toBe(true);
    expect(isMissing(field({}), null)).toBe(true);
    expect(isMissing(field({}), undefined)).toBe(true);
  });

  // Флаг всегда имеет значение (снят — тоже значение), а составное поле проверяется по своим полям,
  // а не по себе целиком: иначе форма требовала бы «заполнить» контейнер.
  it('флаг и составное поле незаполненными не считаются', () => {
    expect(isMissing(field({ type: 'boolean' }), undefined)).toBe(false);
    expect(isMissing(field({ type: 'complex' }), undefined)).toBe(false);
  });

  it('ссылка на объект — заполненное значение', () => {
    expect(isMissing(field({}), { $ref: 'catalog', id: 'x' })).toBe(false);
  });
});

describe('ограничения типа поля', () => {
  it('пустое значение не проверяется', () => {
    expect(validateConstraint('', def('string', { minLength: 5 }))).toBeNull();
    expect(validateConstraint(null, def('number', { min: 1 }))).toBeNull();
  });

  it('строка: шаблон, минимум и максимум длины', () => {
    expect(validateConstraint('7712345678', def('string', { pattern: '^\\d{10}$' }))).toBeNull();
    expect(validateConstraint('абв', def('string', { pattern: '^\\d{10}$' })))
      .toBe('Не соответствует формату: ^\\d{10}$');
    expect(validateConstraint('абв', def('string', { pattern: '^\\d+$', patternMessage: 'Только цифры' })))
      .toBe('Только цифры');
    expect(validateConstraint('аб', def('string', { minLength: 3 }))).toBe('Мин. длина: 3 симв.');
    expect(validateConstraint('абвг', def('string', { maxLength: 3 }))).toBe('Макс. длина: 3 симв.');
  });

  // Битый шаблон приходит из редактора типов: сломать им ввод значения нельзя.
  it('невалидное регулярное выражение молча пропускает значение', () => {
    expect(validateConstraint('что угодно', def('string', { pattern: '([' }))).toBeNull();
  });

  it('число: не-число, целое, границы', () => {
    expect(validateConstraint('абв', def('number', {}))).toBe('Введите число');
    expect(validateConstraint('1.5', def('number', { integer: true }))).toBe('Введите целое число');
    expect(validateConstraint('2', def('number', { integer: true }))).toBeNull();
    expect(validateConstraint('0', def('number', { min: 1 }))).toBe('Мин. значение: 1');
    expect(validateConstraint('9', def('number', { max: 5 }))).toBe('Макс. значение: 5');
  });
});
