import { describe, it, expect } from 'vitest';
import { parseSourceColumns, parseSourceColumnNames, countFilterConditions, cleanFilterNode } from './datasetHelpers';
import type { FilterGroup } from './types';

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
