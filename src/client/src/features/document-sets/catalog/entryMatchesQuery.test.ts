import { describe, it, expect } from 'vitest';
import { entryMatchesQuery } from './ObjectsByTypeList';
import type { CommonDataEntry } from '@/shared/api/types';

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
