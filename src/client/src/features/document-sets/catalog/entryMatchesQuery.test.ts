import { describe, it, expect } from 'vitest';
import { entryMatchesQuery, groupObjectsByType } from './objectsByType';
import type { CommonDataEntry, DocumentType } from '@/shared/api/types';

function entry(over: Partial<CommonDataEntry>): CommonDataEntry {
  return {
    id: 'e1', displayName: 'ООО Ромашка', aliases: [], compositeTypeId: 't1',
    data: {}, scope: 'System', scopeId: null, createdAt: '', updatedAt: '', ...over,
  };
}

describe('entryMatchesQuery (issue #249)', () => {
  it('пустой запрос матчит всё', () => {
    expect(entryMatchesQuery(entry({}), 'Организация', '')).toBe(true);
    expect(entryMatchesQuery(entry({}), 'Организация', '   ')).toBe(true);
  });

  it('находит по имени записи (регистронезависимо)', () => {
    expect(entryMatchesQuery(entry({ displayName: 'ООО Ромашка' }), 'Организация', 'ромашка')).toBe(true);
  });

  it('находит по имени ТИПА — «орга» → «Организация» (основной баг)', () => {
    expect(entryMatchesQuery(entry({ displayName: 'ООО Ромашка' }), 'Организация', 'орга')).toBe(true);
  });

  it('находит по алиасам', () => {
    expect(entryMatchesQuery(entry({ aliases: ['Ромашка ООО', 'RML'] }), 'Организация', 'rml')).toBe(true);
  });

  it('находит по значению скалярного поля', () => {
    expect(entryMatchesQuery(entry({ data: { ИНН: '7701234567' } }), 'Организация', '77012')).toBe(true);
  });

  it('игнорирует служебные ключи (префикс _) и составные значения', () => {
    const e = entry({ displayName: 'X', data: { _baseRef: 'match-uuid', nested: { k: 'match' }, arr: ['match'] } });
    expect(entryMatchesQuery(e, 'Тип', 'match')).toBe(false);
  });

  it('не матчит при отсутствии совпадений', () => {
    expect(entryMatchesQuery(entry({ displayName: 'ООО Ромашка', data: { ИНН: '7701' } }), 'Организация', 'персона')).toBe(false);
  });

  it('typeName undefined не роняет', () => {
    expect(entryMatchesQuery(entry({ displayName: 'Ромашка' }), undefined, 'ромашка')).toBe(true);
    expect(entryMatchesQuery(entry({ displayName: 'Ромашка' }), undefined, 'орга')).toBe(false);
  });
});

describe('группировка объектов по составному типу', () => {
  const orgs = { id: 't1', name: 'Организация' } as unknown as DocumentType;
  const persons = { id: 't2', name: 'Персона' } as unknown as DocumentType;

  it('раскладывает по типам, сохраняя порядок входа', () => {
    const a = entry({ id: 'a', compositeTypeId: 't1' });
    const b = entry({ id: 'b', compositeTypeId: 't2' });
    const c = entry({ id: 'c', compositeTypeId: 't1' });
    const { groups, noType } = groupObjectsByType([a, b, c], [orgs, persons]);
    expect(groups.map(g => [g.type.id, g.items.map(i => i.id)])).toEqual([['t1', ['a', 'c']], ['t2', ['b']]]);
    expect(noType).toEqual([]);
  });

  it('пустые группы не показываются', () => {
    const { groups } = groupObjectsByType([entry({ compositeTypeId: 't1' })], [orgs, persons]);
    expect(groups.map(g => g.type.id)).toEqual(['t1']);
  });

  // Запись, чей тип удалён или не пришёл в списке, обязана остаться видимой: иначе объект исчезает
  // из каталога, хотя он есть.
  it('запись без известного типа уходит в отдельный список, а не пропадает', () => {
    const orphan = entry({ id: 'x', compositeTypeId: 'снесённый' });
    const { groups, noType } = groupObjectsByType([orphan], [orgs]);
    expect(groups).toEqual([]);
    expect(noType.map(e => e.id)).toEqual(['x']);
  });
});
