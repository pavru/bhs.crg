import { describe, it, expect } from 'vitest';
import { getValidUntil, isExpired } from './qualityValidity';
import { FUNCTIONAL_TAG } from '@/shared/api/tags';
import type { QualityDocument } from '@/shared/api/qualityDocs';
import type { DocumentType } from '@/shared/api/types';

/**
 * Срок действия документа качества (issue #1032).
 *
 * До выноса этих функций из `QualityLinksTab` проверить их было нечем: файл экспортировал
 * компоненты, достать функции тестам было неоткуда. А ошибка здесь тиха — просроченный сертификат
 * считается годным, попадает в подсказки и уходит в PDF. Поэтому проверяем границы, а не счастливый
 * путь: отсутствие тэга, мусор вместо даты, вложенное поле и сегодняшний день.
 */

function type(fields: unknown[], id = 'dt1', extra: Partial<DocumentType> = {}): DocumentType {
  return {
    id, name: 'Сертификат', code: 'CERT', kind: 'Document', isAbstract: false, allowsProxy: false,
    parentId: null, schema: { fields }, pluginBindings: {}, group: null,
    module: 'core', storage: 'SharedObject', visibility: 'Shared', readChannels: [], editLevel: 'Open',
    createdAt: '', updatedAt: '', ...extra,
  };
}

function doc(requisites: Record<string, unknown>, documentTypeId = 'dt1'): QualityDocument {
  return {
    id: 'q1', documentTypeId, displayName: 'Сертификат', requisites,
    source: 'Manual', scope: 'System', createdAt: '', updatedAt: '',
  };
}

const withTag = [type([
  { key: 'Номер', title: 'Номер', type: 'string' },
  { key: 'Окончание', title: 'Действует до', type: 'date', tags: [FUNCTIONAL_TAG.qualityValidUntil] },
])];

describe('getValidUntil', () => {
  it('берёт значение поля, помеченного тэгом', () => {
    expect(getValidUntil(doc({ Номер: '77', Окончание: '2020-01-01' }), withTag)).toBe('2020-01-01');
  });

  it('достаёт дату из вложенного составного поля', () => {
    const inner = type([{ key: 'Окончание', type: 'date', tags: [FUNCTIONAL_TAG.qualityValidUntil] }], 'period');
    const outer = type([{ key: 'ПериодДействия', type: 'complex', typeId: 'period' }], 'dt1');
    expect(getValidUntil(doc({ ПериодДействия: { Окончание: '2030-05-05' } }), [outer, inner]))
      .toBe('2030-05-05');
  });

  it('null, когда тэга в схеме нет: без тэга срока не существует, а не «истёк»', () => {
    expect(getValidUntil(doc({ Окончание: '2020-01-01' }), [type([{ key: 'Окончание', type: 'date' }])]))
      .toBeNull();
  });

  it('null, когда тип документа неизвестен', () => {
    expect(getValidUntil(doc({ Окончание: '2020-01-01' }, 'чужой'), withTag)).toBeNull();
  });

  it('null на пустом значении и на значении не-строке', () => {
    expect(getValidUntil(doc({ Окончание: '   ' }), withTag)).toBeNull();
    expect(getValidUntil(doc({ Окончание: 12345 }), withTag)).toBeNull();
  });
});

describe('isExpired', () => {
  it('прошедшая дата — просрочен, будущая — нет', () => {
    expect(isExpired(doc({ Окончание: '2000-01-01' }), withTag)).toBe(true);
    expect(isExpired(doc({ Окончание: '2999-12-31' }), withTag)).toBe(false);
  });

  /**
   * Граница — календарный день, а не момент: вчерашний просрочен, завтрашний нет. Считаем от
   * полуночи СЕГОДНЯШНЕГО дня, иначе документ «до 24 сентября» пропал бы из подсказок утром
   * 24-го — в последний свой рабочий день.
   *
   * Даты считаем арифметикой от `Date`, а не пишем строками: прибитая к тексту дата сделала бы
   * тест зелёным до неё и красным после, и красным он стал бы не из-за поломки.
   */
  it('вчера — просрочен, сегодня и завтра — нет', () => {
    const iso = (shiftDays: number) => {
      const d = new Date();
      d.setDate(d.getDate() + shiftDays);
      return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
    };
    expect(isExpired(doc({ Окончание: iso(-1) }), withTag)).toBe(true);
    expect(isExpired(doc({ Окончание: iso(0) }), withTag)).toBe(false);
    expect(isExpired(doc({ Окончание: iso(1) }), withTag)).toBe(false);
  });

  /**
   * Нераспознанная дата и отсутствие даты — НЕ просрочен. Решение осознанное: распознавание скана
   * кладёт в поле что угодно, и «не смог прочитать» не значит «документ недействителен» — иначе
   * экран вычеркнул бы годный сертификат из-за опечатки в дате. Проверяем поведение, а не
   * конкретную строку кода: у нечитаемой даты сравнение ложно само по себе.
   */
  it('мусор вместо даты и отсутствие даты — не просрочен', () => {
    expect(isExpired(doc({ Окончание: 'не позднее декабря' }), withTag)).toBe(false);
    expect(isExpired(doc({}), withTag)).toBe(false);
  });
});
