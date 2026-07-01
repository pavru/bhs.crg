import { describe, it, expect } from 'vitest';
import {
  parseSchemaFields,
  resolveEffectiveFields,
  groupEffectiveFields,
  isSubtypeOf,
  getDefaultValues,
  isFieldMissing,
  isScalarField,
  type SchemaField,
} from './schema';
import type { DocumentType } from './types';

// ── Test factory ────────────────────────────────────────────────────────────────

let seq = 0;
function dt(schema: Record<string, unknown>, parentId: string | null = null, id?: string): DocumentType {
  return {
    id: id ?? `dt${++seq}`,
    name: 'T', code: 'C', kind: 'Document', isAbstract: false,
    parentId, schema, pluginBindings: {}, group: null,
    createdAt: '', updatedAt: '',
  };
}

function field(key: string, extra: Partial<SchemaField> = {}): SchemaField {
  return { key, title: key, type: 'string', required: false, ...extra };
}

// ── parseSchemaFields ─────────────────────────────────────────────────────────

describe('parseSchemaFields', () => {
  it('returns [] when no fields', () => {
    expect(parseSchemaFields({})).toEqual([]);
    expect(parseSchemaFields({ fields: 'not-array' })).toEqual([]);
  });

  it('applies defaults for missing props', () => {
    const [f] = parseSchemaFields({ fields: [{ key: 'A' }] });
    expect(f).toMatchObject({ key: 'A', title: '', type: 'string', required: false });
  });

  it('preserves provided props', () => {
    const [f] = parseSchemaFields({ fields: [{ key: 'A', title: 'Имя', type: 'number', required: true }] });
    expect(f).toMatchObject({ key: 'A', title: 'Имя', type: 'number', required: true });
  });
});

// ── resolveEffectiveFields (inheritance) ────────────────────────────────────────

describe('resolveEffectiveFields', () => {
  it('returns own fields when no parent', () => {
    const t = dt({ fields: [field('A'), field('B')] });
    expect(resolveEffectiveFields(t, [t]).map(f => f.key)).toEqual(['A', 'B']);
  });

  it('inherits parent fields first, then own', () => {
    const parent = dt({ fields: [field('P1'), field('P2')] }, null, 'p');
    const child = dt({ fields: [field('C1')] }, 'p', 'c');
    expect(resolveEffectiveFields(child, [parent, child]).map(f => f.key))
      .toEqual(['P1', 'P2', 'C1']);
  });

  it('excludes fields listed in excludedFields', () => {
    const parent = dt({ fields: [field('P1'), field('P2')] }, null, 'p');
    const child = dt({ fields: [field('C1')], excludedFields: ['P2'] }, 'p', 'c');
    expect(resolveEffectiveFields(child, [parent, child]).map(f => f.key))
      .toEqual(['P1', 'C1']);
  });

  it('applies fieldOverrides (required) to inherited fields', () => {
    const parent = dt({ fields: [field('P1', { required: false })] }, null, 'p');
    const child = dt({ fields: [], fieldOverrides: { P1: { required: true } } }, 'p', 'c');
    const resolved = resolveEffectiveFields(child, [parent, child]);
    expect(resolved.find(f => f.key === 'P1')?.required).toBe(true);
  });

  it('own field with same key as inherited is not duplicated', () => {
    const parent = dt({ fields: [field('X', { title: 'Parent X' })] }, null, 'p');
    const child = dt({ fields: [field('X', { title: 'Child X' })] }, 'p', 'c');
    const resolved = resolveEffectiveFields(child, [parent, child]);
    expect(resolved.filter(f => f.key === 'X')).toHaveLength(1);
  });

  it('walks a multi-level chain', () => {
    const gp = dt({ fields: [field('G')] }, null, 'gp');
    const p = dt({ fields: [field('P')] }, 'gp', 'p');
    const c = dt({ fields: [field('C')] }, 'p', 'c');
    expect(resolveEffectiveFields(c, [gp, p, c]).map(f => f.key)).toEqual(['G', 'P', 'C']);
  });

  it('falls back to own fields if parent is missing from list', () => {
    const child = dt({ fields: [field('C1')] }, 'missing', 'c');
    expect(resolveEffectiveFields(child, [child]).map(f => f.key)).toEqual(['C1']);
  });
});

// ── groupEffectiveFields ────────────────────────────────────────────────────────

describe('groupEffectiveFields', () => {
  const fields = [field('A'), field('B'), field('C')];

  it('returns a single untitled section when no groups', () => {
    const sections = groupEffectiveFields(fields, {});
    expect(sections).toHaveLength(1);
    expect(sections[0].title).toBeNull();
    expect(sections[0].fields.map(f => f.key)).toEqual(['A', 'B', 'C']);
  });

  it('splits ungrouped fields first, then groups in order', () => {
    const schema = { groups: [{ key: 'g1', title: 'Группа 1', fieldKeys: ['B'] }] };
    const sections = groupEffectiveFields(fields, schema);
    expect(sections.map(s => s.title)).toEqual([null, 'Группа 1']);
    expect(sections[0].fields.map(f => f.key)).toEqual(['A', 'C']);
    expect(sections[1].fields.map(f => f.key)).toEqual(['B']);
  });

  it('ignores group field keys that no longer exist', () => {
    const schema = { groups: [{ key: 'g1', title: 'G', fieldKeys: ['B', 'GONE'] }] };
    const sections = groupEffectiveFields(fields, schema);
    expect(sections.find(s => s.title === 'G')!.fields.map(f => f.key)).toEqual(['B']);
  });
});

// ── isSubtypeOf ─────────────────────────────────────────────────────────────────

describe('isSubtypeOf', () => {
  const gp = dt({}, null, 'gp');
  const p = dt({}, 'gp', 'p');
  const c = dt({}, 'p', 'c');
  const all = [gp, p, c];

  it('is true for identity', () => expect(isSubtypeOf('c', 'c', all)).toBe(true));
  it('is true for direct parent', () => expect(isSubtypeOf('c', 'p', all)).toBe(true));
  it('is true for ancestor', () => expect(isSubtypeOf('c', 'gp', all)).toBe(true));
  it('is false for unrelated', () => expect(isSubtypeOf('p', 'c', all)).toBe(false));
});

// ── getDefaultValues ────────────────────────────────────────────────────────────

describe('getDefaultValues', () => {
  it('collects only fields with a default', () => {
    const fields = [field('A', { defaultValue: 'x' }), field('B'), field('C', { defaultValue: 0 })];
    expect(getDefaultValues(fields)).toEqual({ A: 'x', C: 0 });
  });
});

// ── isFieldMissing ──────────────────────────────────────────────────────────────

describe('isFieldMissing', () => {
  it('optional field is never missing', () => {
    expect(isFieldMissing(field('A', { required: false }), '')).toBe(false);
  });
  it('required string missing when empty/null', () => {
    const f = field('A', { required: true });
    expect(isFieldMissing(f, '')).toBe(true);
    expect(isFieldMissing(f, null)).toBe(true);
    expect(isFieldMissing(f, '  ')).toBe(true);
    expect(isFieldMissing(f, 'x')).toBe(false);
  });
  it('required boolean is never missing', () => {
    expect(isFieldMissing(field('A', { required: true, type: 'boolean' }), undefined)).toBe(false);
  });
  it('required complex missing when empty object', () => {
    const f = field('A', { required: true, type: 'complex' });
    expect(isFieldMissing(f, {})).toBe(true);
    expect(isFieldMissing(f, null)).toBe(true);
    expect(isFieldMissing(f, { x: 1 })).toBe(false);
  });
});

// ── isScalarField ───────────────────────────────────────────────────────────────

describe('isScalarField', () => {
  it.each(['string', 'text', 'number', 'date', 'boolean', 'enum', 'primitive', 'image', 'file'] as const)(
    '%s is scalar', t => expect(isScalarField(field('A', { type: t }))).toBe(true));
  it.each(['array', 'complex', 'doc-ref', 'doc-array'] as const)(
    '%s is not scalar', t => expect(isScalarField(field('A', { type: t }))).toBe(false));
});
