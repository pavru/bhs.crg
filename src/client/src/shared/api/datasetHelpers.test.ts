import { describe, it, expect } from 'vitest';
import {
  parseSourceColumns, parseSourceColumnNames, countFilterConditions, cleanFilterNode,
  mergeBindingPreviewsIntoValues, computeBoundFieldKeys,
  isFileMappingValue, parseFileMapping, buildFileMapping,
} from './datasetHelpers';
import type { FilterGroup, DataSetBindingPreviewResult } from './types';

describe('parseSourceColumns', () => {
  it('returns [] for null/undefined/blank', () => {
    expect(parseSourceColumns(null)).toEqual([]);
    expect(parseSourceColumns(undefined)).toEqual([]);
    expect(parseSourceColumns('')).toEqual([]);
  });

  it('returns [] for malformed JSON', () => {
    expect(parseSourceColumns('{ broken')).toEqual([]);
  });

  it('returns [] when JSON is not an array', () => {
    expect(parseSourceColumns('{"name":"x"}')).toEqual([]);
  });

  it('parses column descriptors', () => {
    const json = JSON.stringify([{ name: 'A', sampleValues: ['1'] }, { name: 'B' }]);
    expect(parseSourceColumns(json)).toEqual([{ name: 'A', sampleValues: ['1'] }, { name: 'B' }]);
  });
});

describe('parseSourceColumnNames', () => {
  it('extracts names only', () => {
    const json = JSON.stringify([{ name: 'A' }, { name: 'B' }]);
    expect(parseSourceColumnNames(json)).toEqual(['A', 'B']);
  });
  it('returns [] on bad input', () => {
    expect(parseSourceColumnNames('nope')).toEqual([]);
  });
});

describe('countFilterConditions', () => {
  it('returns 0 for null/undefined', () => {
    expect(countFilterConditions(null)).toBe(0);
    expect(countFilterConditions(undefined)).toBe(0);
  });

  it('counts a single condition', () => {
    expect(countFilterConditions({ type: 'condition', column: 'A', op: 'eq', value: '1' })).toBe(1);
  });

  it('does not count a condition with empty column', () => {
    expect(countFilterConditions({ type: 'condition', column: '', op: 'eq' })).toBe(0);
  });

  it('counts conditions across nested groups', () => {
    const tree: FilterGroup = {
      type: 'group', logic: 'and',
      children: [
        { type: 'condition', column: 'A', op: 'eq', value: '1' },
        {
          type: 'group', logic: 'or',
          children: [
            { type: 'condition', column: 'B', op: 'eq', value: '2' },
            { type: 'condition', column: 'C', op: 'eq', value: '3' },
            { type: 'condition', column: '', op: 'eq' }, // not counted
          ],
        },
      ],
    };
    expect(countFilterConditions(tree)).toBe(3);
  });

  it('returns 0 for an empty group', () => {
    expect(countFilterConditions({ type: 'group', logic: 'and', children: [] })).toBe(0);
  });
});

describe('cleanFilterNode', () => {
  it('keeps a valid condition', () => {
    const c = { type: 'condition', column: 'A', op: 'eq', value: '1' } as const;
    expect(cleanFilterNode(c)).toEqual(c);
  });

  it('drops a condition with a blank column', () => {
    expect(cleanFilterNode({ type: 'condition', column: '  ', op: 'eq' })).toBeNull();
  });

  it('returns null for a group with only empty conditions', () => {
    const tree: FilterGroup = {
      type: 'group', logic: 'and',
      children: [{ type: 'condition', column: '', op: 'eq' }],
    };
    expect(cleanFilterNode(tree)).toBeNull();
  });

  it('prunes empty children but keeps valid ones', () => {
    const tree: FilterGroup = {
      type: 'group', logic: 'and',
      children: [
        { type: 'condition', column: 'A', op: 'eq', value: '1' },
        { type: 'condition', column: '', op: 'eq' },
        { type: 'group', logic: 'or', children: [] },
      ],
    };
    const cleaned = cleanFilterNode(tree) as FilterGroup;
    expect(cleaned.children).toHaveLength(1);
    expect((cleaned.children[0] as { column: string }).column).toBe('A');
  });

  it('collapses a group whose only sub-group is empty', () => {
    const tree: FilterGroup = {
      type: 'group', logic: 'and',
      children: [{ type: 'group', logic: 'or', children: [{ type: 'condition', column: '', op: 'eq' }] }],
    };
    expect(cleanFilterNode(tree)).toBeNull();
  });
});

describe('mergeBindingPreviewsIntoValues', () => {
  function scalar(data: Record<string, string | null>): DataSetBindingPreviewResult {
    return { bindingId: '1', sourceName: 's', fileName: 'f', mode: 'scalar', targetFieldKey: null, totalRows: 1, data, error: null };
  }
  function tabular(targetFieldKey: string, data: Record<string, string | null>[]): DataSetBindingPreviewResult {
    return { bindingId: '2', sourceName: 's', fileName: 'f', mode: 'tabular', targetFieldKey, totalRows: data.length, data, error: null };
  }

  it('overwrites matching scalar key', () => {
    const result = mergeBindingPreviewsIntoValues({ inn: 'старое', name: 'не трогать' }, [scalar({ inn: 'новое' })]);
    expect(result.inn).toBe('новое');
    expect(result.name).toBe('не трогать');
  });

  it('does not overwrite existing value with empty scalar', () => {
    const result = mergeBindingPreviewsIntoValues({ inn: 'ручное' }, [scalar({ inn: '' })]);
    expect(result.inn).toBe('ручное');
  });

  it('writes tabular array into targetFieldKey, even empty', () => {
    const result = mergeBindingPreviewsIntoValues({ Чертежи: [{ old: true }] }, [tabular('Чертежи', [])]);
    expect(result['Чертежи']).toEqual([]);
  });

  it('skips error bindings', () => {
    const errored: DataSetBindingPreviewResult = { bindingId: '3', sourceName: 's', fileName: 'f', mode: 'error', targetFieldKey: null, totalRows: 0, data: {}, error: 'нет источника' };
    const result = mergeBindingPreviewsIntoValues({ inn: 'прежнее' }, [errored]);
    expect(result.inn).toBe('прежнее');
  });
});

describe('computeBoundFieldKeys', () => {
  it('collects scalar mapping keys and array targetFieldKeys separately', () => {
    const { scalarKeys, arrayKeys } = computeBoundFieldKeys([
      { targetFieldKey: null, mapping: { inn: 'ИНН', name: 'Название' } },
      { targetFieldKey: 'Чертежи', mapping: { НомерЛиста: 'НомерЛиста' } },
    ]);
    expect([...scalarKeys]).toEqual(['inn', 'name']);
    expect([...arrayKeys]).toEqual(['Чертежи']);
  });

  it('returns empty sets for no bindings', () => {
    const { scalarKeys, arrayKeys } = computeBoundFieldKeys([]);
    expect(scalarKeys.size).toBe(0);
    expect(arrayKeys.size).toBe(0);
  });
});

describe('file mapping (@@file:)', () => {
  it('round-trips column and sizeColumn through build/parse', () => {
    const encoded = buildFileMapping({ column: 'ФайлПуть', sizeColumn: 'РазмерБайт' });
    expect(isFileMappingValue(encoded)).toBe(true);
    expect(parseFileMapping(encoded)).toEqual({ column: 'ФайлПуть', sizeColumn: 'РазмерБайт' });
  });

  it('round-trips without a sizeColumn', () => {
    const encoded = buildFileMapping({ column: 'ФайлПуть', sizeColumn: '' });
    expect(parseFileMapping(encoded)).toEqual({ column: 'ФайлПуть', sizeColumn: '' });
  });

  it('does not treat a plain column name as a file mapping', () => {
    expect(isFileMappingValue('ФайлПуть')).toBe(false);
    expect(parseFileMapping('ФайлПуть')).toBeNull();
  });

  it('does not confuse file mapping with ref mapping prefix', () => {
    expect(isFileMappingValue('@@ref:{"column":"X","match":"","typeId":"1"}')).toBe(false);
  });

  it('returns null for malformed JSON', () => {
    expect(parseFileMapping('@@file:not-json')).toBeNull();
  });

  it('returns null when column is missing', () => {
    expect(parseFileMapping('@@file:{"sizeColumn":"РазмерБайт"}')).toBeNull();
  });
});
