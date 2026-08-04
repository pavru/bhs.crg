import { describe, it, expect } from 'vitest';
import {
  suggestEntryName, scalarFieldsFor, findBlockingRefs, maxAllowedScope, scopesUpTo,
} from './extractToCommonData';
import type { SchemaField } from '@/shared/api/schema';
import type { CatalogScope, FieldRef, PrimitiveTypeDef } from '@/shared/api/types';

/**
 * Вынос inline-объекта в общие данные (issue #663) — правила, решаемые без интерфейса.
 *
 * Каждый из четырёх случаев отвечает за свой класс тихой ошибки: имя, не совпавшее с подписью;
 * потерянный компонент ключа (дубликат не найден и объект заведён второй раз); ссылка на документ,
 * уехавшая в чужой комплект сырым `$ref`; запись уровня выше своей вложенной ссылки.
 */

const field = (key: string, type = 'string', extra: Partial<SchemaField> = {}): SchemaField =>
  ({ key, type, title: key, ...extra } as unknown as SchemaField);

const catalogRef = (displayName: string, scope?: CatalogScope, entryId = 'e1'): FieldRef =>
  ({ $ref: 'catalog', entryId, displayName, scope });

const documentRef = (displayName: string): FieldRef =>
  ({ $ref: 'document', instanceId: 'i1', fieldKey: 'Номер', displayName });

describe('suggestEntryName', () => {
  const fields = [field('Наименование'), field('Артикул'), field('Цвет')];

  it('собирает имя из полей идентичности В ПОРЯДКЕ КЛЮЧА, а не схемы', () => {
    const name = suggestEntryName(
      { Наименование: 'Кабель ВВГнг', Артикул: 'LS-3x2.5', Цвет: 'чёрный' },
      fields,
      ['Артикул', 'Наименование'], // порядок задаёт параметр тэга, не порядок полей
    );
    expect(name).toBe('LS-3x2.5 Кабель ВВГнг');
  });

  it('пустой компонент ключа пропускает, а не оставляет дыру в имени', () => {
    const name = suggestEntryName(
      { Наименование: 'Кабель', Артикул: '' }, fields, ['Артикул', 'Наименование']);
    expect(name).toBe('Кабель');
  });

  it('без полей идентичности берёт сводку — ту же, которой объект подписан свёрнутым', () => {
    const name = suggestEntryName({ Наименование: 'Кабель', Артикул: 'LS' }, fields, []);
    expect(name).toBe('Кабель · LS');
  });

  it('пустой объект даёт пустое имя, а не служебное «(пусто)»', () => {
    expect(suggestEntryName({}, fields, [])).toBe('');
    expect(suggestEntryName({}, fields, ['Артикул'])).toBe('');
  });

  it('значение форматирует как на экране: дата не уезжает в имя в ISO', () => {
    const defs = {
      primitiveTypes: [
        { id: 'd', name: 'Дата', code: 'Date', baseType: 'date', constraints: {} },
      ] as unknown as PrimitiveTypeDef[],
    };
    const name = suggestEntryName(
      { Выдан: '2026-08-04' },
      [field('Выдан', 'primitive', { typeId: 'd' })],
      ['Выдан'],
      defs);
    expect(name).toBe('04.08.2026');
  });

  it('ссылку в поле идентичности подставляет её подписью', () => {
    const name = suggestEntryName(
      { Наименование: catalogRef('ООО «ЭнергоСтрой»') }, fields, ['Наименование']);
    expect(name).toBe('ООО «ЭнергоСтрой»');
  });
});

describe('scalarFieldsFor', () => {
  it('отдаёт только скаляры верхнего уровня — как их читает сервер', () => {
    expect(scalarFieldsFor({
      Наименование: 'Кабель',
      Длина: 305,
      Давальческий: true,
      Пусто: null,
      Вложенный: { Ключ: 'значение' },
      Строки: [{ a: 1 }],
      Ссылка: catalogRef('ООО'),
    })).toEqual({
      Наименование: 'Кабель',
      Длина: '305',
      Давальческий: 'true',
    });
  });

  it('ноль и пустая строка — значения, а не пропуски: компонент ключа терять нельзя', () => {
    expect(scalarFieldsFor({ Количество: 0, Примечание: '' }))
      .toEqual({ Количество: '0', Примечание: '' });
  });
});

describe('findBlockingRefs', () => {
  it('находит ссылку на документ на любой глубине, включая массивы', () => {
    const found = findBlockingRefs({
      Наименование: 'Работа',
      Основание: documentRef('АОСР → Номер'),
      Строки: [{ ok: 'да' }, { Ссылка: documentRef('Акт → Дата') }],
    });
    expect(found.map(r => r.path)).toEqual(['Основание', 'Строки[1].Ссылка']);
    expect(found.every(r => r.kind === 'document')).toBe(true);
    expect(found[0].displayName).toBe('АОСР → Номер');
  });

  it('ссылка на экземпляр блокирует так же — резолвится тем же правилом «свой комплект»', () => {
    const found = findBlockingRefs({ Акт: { $ref: 'instance', instanceId: 'i', displayName: 'Акт' } });
    expect(found).toHaveLength(1);
    expect(found[0].kind).toBe('instance');
  });

  it('ссылка на каталог не блокирует — она разворачивается откуда угодно', () => {
    expect(findBlockingRefs({ Организация: catalogRef('ООО'), Вложено: [catalogRef('ЗАО')] }))
      .toEqual([]);
  });

  it('внутрь ссылки не заходит: её содержимое — чужая запись со своими правилами', () => {
    const ref = { ...catalogRef('ООО'), Основание: documentRef('чужое') } as unknown;
    expect(findBlockingRefs({ Организация: ref })).toEqual([]);
  });
});

describe('maxAllowedScope', () => {
  it('без вложенных ссылок разрешает самый широкий уровень', () => {
    expect(maxAllowedScope({ Наименование: 'Кабель' })).toBe('System');
  });

  it('ссылка уровня «Комплект» запрещает всё, что шире', () => {
    expect(maxAllowedScope({ Организация: catalogRef('ООО', 'Set') })).toBe('Set');
  });

  it('из нескольких ссылок побеждает самая узкая', () => {
    expect(maxAllowedScope({
      А: catalogRef('a', 'System'),
      Б: catalogRef('b', 'Section'),
      В: catalogRef('c', 'Construction'),
    })).toBe('Section');
  });

  it('ссылки на документ уровень не ограничивают — они его вообще запрещают (см. findBlockingRefs)', () => {
    expect(maxAllowedScope({ Основание: documentRef('АОСР → Номер') })).toBe('System');
  });

  it('ссылка без уровня доопределяется по каталогу', () => {
    const value = { Организация: { $ref: 'catalog', entryId: 'e7', displayName: 'ООО' } };
    expect(maxAllowedScope(value, id => (id === 'e7' ? 'Section' : undefined))).toBe('Section');
  });

  it('уровень не известен ни ссылке, ни каталогу — считаем «Комплект», а не «Система»', () => {
    const value = { Организация: { $ref: 'catalog', entryId: 'e9', displayName: 'ООО' } };
    expect(maxAllowedScope(value)).toBe('Set');
  });
});

describe('scopesUpTo', () => {
  it('предлагает уровни от комплекта до разрешённого включительно', () => {
    expect(scopesUpTo('Section')).toEqual(['Set', 'Section']);
    expect(scopesUpTo('System')).toEqual(['Set', 'Section', 'Construction', 'System']);
    expect(scopesUpTo('Set')).toEqual(['Set']);
  });
});
