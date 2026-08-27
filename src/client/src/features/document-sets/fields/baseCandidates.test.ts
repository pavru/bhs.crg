import { describe, it, expect } from 'vitest';
import { SCOPE_TIER, ancestorTypeIds, parseBaseRef } from './baseCandidates';
import type { DocumentType } from '@/shared/api/types';

function type(id: string, parentId: string | null): DocumentType {
  return {
    id, name: id, code: id, kind: 'Composite', isAbstract: false, allowsProxy: false,
    parentId, schema: {}, pluginBindings: {}, group: null, createdAt: '', updatedAt: '',
  };
}

describe('уровни каталога (issue #71/#73)', () => {
  // Порядок — это приоритет разрешения: ближний уровень бьёт дальний. Перестановка чисел молча
  // меняет, какой кандидат считается ближе, а внешне всё остаётся на месте.
  it('идут от комплекта к системе', () => {
    expect(SCOPE_TIER).toEqual({ Set: 0, Section: 1, Construction: 2, System: 3 });
  });
});

describe('цепочка типов-предков', () => {
  const all = [type('дед', null), type('отец', 'дед'), type('сын', 'отец')];

  it('по возрастанию дистанции: родитель, дед, …', () => {
    expect(ancestorTypeIds(type('сын', 'отец'), all)).toEqual(['отец', 'дед']);
  });

  it('у корня предков нет', () => {
    expect(ancestorTypeIds(type('дед', null), all)).toEqual([]);
    expect(ancestorTypeIds(undefined, all)).toEqual([]);
  });

  // Цикл в данных возможен (родителя задаёт человек) и обязан кончиться, а не завесить экран.
  it('замкнутая цепочка не зацикливает', () => {
    const cyclic = [type('a', 'b'), type('b', 'a')];
    expect(ancestorTypeIds(type('a', 'b'), cyclic)).toEqual(['b', 'a']);
  });
});

describe('разбор ссылки на базу', () => {
  it('объект {kind,id}', () => {
    expect(parseBaseRef({ kind: 'instance', id: 'x' })).toEqual({ kind: 'instance', id: 'x' });
    expect(parseBaseRef({ kind: 'catalog', id: 'x' })).toEqual({ kind: 'catalog', id: 'x' });
  });

  // Голая строка — форма до issue #71. Читать её не перестанем: восстановление бэкапа впрыскивает
  // её заново, а архивы восстановимы неограниченно долго.
  it('голая строка — это запись каталога', () => {
    expect(parseBaseRef('x')).toEqual({ kind: 'catalog', id: 'x' });
  });

  it('незнакомый kind считается каталогом, а мусор — отсутствием ссылки', () => {
    expect(parseBaseRef({ kind: 'что-то', id: 'x' })).toEqual({ kind: 'catalog', id: 'x' });
    expect(parseBaseRef('')).toBeUndefined();
    expect(parseBaseRef(null)).toBeUndefined();
    expect(parseBaseRef({})).toBeUndefined();
    expect(parseBaseRef({ kind: 'instance' })).toBeUndefined();
  });
});
