import { afterEach, describe, it, expect } from 'vitest';
import { isMissing, validateConstraint, validateSample } from './fieldValidation';
import { setFormatLocale } from '@/shared/format/format';
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

// Образец даты в настройке типа — в маске языка пользователя (issue #1461).
describe('validateSample: дата образца по языку пользователя', () => {
  afterEach(() => setFormatLocale('ru-RU'));
  const def = { id: 'd', name: 'Дата', baseType: 'date', constraints: { minDate: '2026-01-01' } } as unknown as PrimitiveTypeDef;

  it('en-US: 10/18/2026 — дата и проходит предел; 12/31/2025 — раньше предела', () => {
    setFormatLocale('en-US');
    expect(validateSample('10/18/2026', def)).toBeNull();
    expect(validateSample('12/31/2025', def)).toMatch(/не ранее/);
    expect(validateSample('18.10.2026', def)).toBe('Не дата (ожидается MM/DD/YYYY)');
  });

  it('ru-RU: 18.10.2026 — дата; текст не в маске — отказ с маской языка', () => {
    setFormatLocale('ru-RU');
    expect(validateSample('18.10.2026', def)).toBeNull();
    expect(validateSample('вчера', def)).toBe('Не дата (ожидается ДД.ММ.ГГГГ)');
  });

  // Ревью PR #1544: образец — в точности типа, как подсказывает кнопка «Месяц и год (ММ.ГГГГ)».
  it('точность «месяц и год»: образец ММ.ГГГГ, отказ называет маску точности', () => {
    const month = { ...def, constraints: { minDate: '2026-01-01', datePrecision: 'month' } } as PrimitiveTypeDef;
    expect(validateSample('10.2026', month)).toBeNull();
    expect(validateSample('12.2025', month)).toBe('Дата не ранее 01.2026');
    expect(validateSample('вчера', month)).toBe('Не дата (ожидается ММ.ГГГГ)');
    const year = { ...def, constraints: { datePrecision: 'year' } } as PrimitiveTypeDef;
    expect(validateSample('2026', year)).toBeNull();
  });

  // Ревью PR #1544: предел под полем MM/DD/YYYY — в той же записи, иначе 01.07 читалось бы как 7 января.
  it('предел в сообщении — в маске языка пользователя', () => {
    const limited = { ...def, constraints: { minDate: '2026-07-01', maxDate: '2026-07-31' } } as PrimitiveTypeDef;
    setFormatLocale('en-US');
    expect(validateConstraint('2026-06-30', limited)).toBe('Дата не ранее 07/01/2026');
    expect(validateConstraint('2026-08-01', limited)).toBe('Дата не позднее 07/31/2026');
    setFormatLocale('ru-RU');
    expect(validateConstraint('2026-06-30', limited)).toBe('Дата не ранее 01.07.2026');
  });

  // #1464: предел сравнивается в точности типа — иначе 07.2026 при min 15.07.2026 отклонялся бы
  // сообщением «не ранее 07.2026», которое ему же противоречит.
  it('точность «месяц»: месяц предела допустим, хотя его 01 раньше дня предела', () => {
    const month = { ...def, constraints: { minDate: '2026-07-15', maxDate: '2026-09-10', datePrecision: 'month' } } as PrimitiveTypeDef;
    expect(validateConstraint('2026-07-01', month)).toBeNull();
    expect(validateConstraint('2026-09-01', month)).toBeNull();
    expect(validateConstraint('2026-06-01', month)).toBe('Дата не ранее 07.2026');
    expect(validateConstraint('2026-10-01', month)).toBe('Дата не позднее 09.2026');
  });
});
