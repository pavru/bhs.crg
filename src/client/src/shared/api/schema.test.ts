import { describe, it, expect } from 'vitest';
import {
  parseSchemaFields,
  resolveEffectiveFields,
  chainFieldKeys,
  groupEffectiveFields,
  isSubtypeOf,
  getDefaultValues,
  isFieldMissing,
  isScalarField,
  isMaterialType,
  materialIdentityKeys,
  type SchemaField,
} from './schema';
import type { DocumentType } from './types';

// ── Test factory ────────────────────────────────────────────────────────────────

let seq = 0;
function dt(schema: Record<string, unknown>, parentId: string | null = null, id?: string): DocumentType {
  return {
    id: id ?? `dt${++seq}`,
    name: 'T', code: 'C', kind: 'Document', isAbstract: false, allowsProxy: false,
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

// ── chainFieldKeys (issue #639) ─────────────────────────────────────────────────

describe('chainFieldKeys', () => {
  it('собирает ключи всей цепочки ДО исключений', () => {
    // Дед объявил «Примечание», отец его исключил. Для отца это поле ВСЁ ЕЩЁ существует —
    // иначе собственное исключение потомка выглядело бы ссылкой в пустоту, и «Убрать ссылки»
    // молча снесло бы намерение «мне это поле не нужно».
    const grand = dt({ fields: [field('Примечание'), field('Общее')] }, null, 'g');
    const parent = dt({ fields: [field('Своё')], excludedFields: ['Примечание'] }, 'g', 'p');

    expect(resolveEffectiveFields(parent, [grand, parent]).map(f => f.key)).toEqual(['Общее', 'Своё']);
    expect(chainFieldKeys(parent, [grand, parent]).sort())
      .toEqual(['Общее', 'Примечание', 'Своё'].sort());
  });

  it('оборванная и зациклённая цепочка не роняют обход', () => {
    const orphan = dt({ fields: [field('A')] }, 'нет-такого', 'o');
    expect(chainFieldKeys(orphan, [orphan])).toEqual(['A']);

    const a = dt({ fields: [field('A')] }, 'b', 'a');
    const b = dt({ fields: [field('B')] }, 'a', 'b');
    expect(chainFieldKeys(a, [a, b]).sort()).toEqual(['A', 'B']);
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

  it('applies ungroupedOrder to the ungrouped section (no groups)', () => {
    const schema = { ungroupedOrder: ['C', 'A'] };
    const sections = groupEffectiveFields(fields, schema);
    // C, A позиционированы; B вне порядка — стабильно в конце
    expect(sections[0].fields.map(f => f.key)).toEqual(['C', 'A', 'B']);
  });

  it('applies ungroupedOrder only to ungrouped fields, groups untouched', () => {
    const schema = {
      groups: [{ key: 'g1', title: 'G', fieldKeys: ['B'] }],
      ungroupedOrder: ['C', 'A'],
    };
    const sections = groupEffectiveFields(fields, schema);
    expect(sections[0].fields.map(f => f.key)).toEqual(['C', 'A']); // ungrouped упорядочен
    expect(sections[1].fields.map(f => f.key)).toEqual(['B']);      // группа как есть
  });

  it('keys outside ungroupedOrder keep their relative order (stable)', () => {
    const many = [field('A'), field('B'), field('C'), field('D')];
    const sections = groupEffectiveFields(many, { ungroupedOrder: ['D'] });
    expect(sections[0].fields.map(f => f.key)).toEqual(['D', 'A', 'B', 'C']);
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

// ── materialIdentityKeys (issue #569) ─────────────────────────────────────────

describe('materialIdentityKeys', () => {
  /**
   * Разбор живого случая: в реестре 151 материал, а вкладка показывала 4 — «шт», «упак», «м»,
   * «компл». Тэг identity носит и «Единица измерения» (законно — она опознаётся наименованием), а
   * набор данных приносит колонку с тем же ключом. Пока ключи собирались по ВСЕМ составным типам,
   * ключом каждой строки становилась единица, и 147 строк отбрасывались как дубли.
   */
  const material = { ...dt({ fields: [
    field('Наименование', { tags: ['identity'] }),
    field('Артикул', { tags: ['identity'] }),
    field('ДокументПодтверждающийКачество', { type: 'complex', tags: ['material.qualityDocLink'] }),
  ] }), kind: 'Composite' as const };
  const unit = { ...dt({ fields: [field('ЕдиницаИзмерения', { tags: ['identity'] })] }), kind: 'Composite' as const };
  const org = { ...dt({ fields: [field('Сокращённое', { tags: ['identity'] })] }), kind: 'Composite' as const };

  it('берёт поля только у типов, способных нести документ качества', () => {
    expect(materialIdentityKeys([unit, org, material])).toEqual(['Наименование', 'Артикул']);
  });

  it('единица измерения и организация материалом не считаются', () => {
    const all = [unit, org, material];
    expect(isMaterialType(material, all)).toBe(true);
    expect(isMaterialType(unit, all)).toBe(false);
    expect(isMaterialType(org, all)).toBe(false);
  });

  it('порядок типов не влияет на состав ключей', () => {
    expect(materialIdentityKeys([material, unit, org])).toEqual(materialIdentityKeys([unit, material, org]));
  });

  // ── порядок компонентов составного ключа (issue #583) ───────────────────────
  //
  // Порядок здесь не косметика: из него складывается ключ связки «материал → сертификат», а ищет
  // связку сервер. Правила ОБЯЗАНЫ совпадать с `MaterialIdentity.KeysOf` — те же случаи проверены
  // и там (MaterialIdentityTests).

  it('номер у тэга задаёт порядок компонентов, а не порядок полей в схеме', () => {
    const m = { ...dt({ fields: [
      field('Наименование', { tags: ['identity:2'] }),
      field('Артикул', { tags: ['identity:1'] }),
      field('Кач', { type: 'complex', tags: ['material.qualityDocLink'] }),
    ] }), kind: 'Composite' as const };
    expect(materialIdentityKeys([m])).toEqual(['Артикул', 'Наименование']);
  });

  it('поле без номера идёт после нумерованных', () => {
    const m = { ...dt({ fields: [
      field('Наименование', { tags: ['identity'] }),
      field('Артикул', { tags: ['identity:1'] }),
      field('Кач', { type: 'complex', tags: ['material.qualityDocLink'] }),
    ] }), kind: 'Composite' as const };
    expect(materialIdentityKeys([m])).toEqual(['Артикул', 'Наименование']);
  });

  it('номера сравниваются сквозь типы: подтип с «1» стоит перед базовым с «2»', () => {
    const base = { ...dt({ fields: [
      field('Наименование', { tags: ['identity:2'] }),
      field('Кач', { type: 'complex', tags: ['material.qualityDocLink'] }),
    ] }, null, 'dt-base'), kind: 'Composite' as const };
    const cable = { ...dt({ fields: [field('МаркаКабеля', { tags: ['identity:1'] })] }, 'dt-base', 'dt-cable'),
      kind: 'Composite' as const };
    expect(materialIdentityKeys([base, cable])).toEqual(['МаркаКабеля', 'Наименование']);
  });

  it('без номеров поля предка идут перед собственными — как их показывает форма', () => {
    const base = { ...dt({ fields: [
      field('Наименование', { tags: ['identity'] }),
      field('Кач', { type: 'complex', tags: ['material.qualityDocLink'] }),
    ] }, null, 'dt-base2'), kind: 'Composite' as const };
    const cable = { ...dt({ fields: [field('МаркаКабеля', { tags: ['identity'] })] }, 'dt-base2', 'dt-cable2'),
      kind: 'Composite' as const };
    expect(materialIdentityKeys([base, cable])).toEqual(['Наименование', 'МаркаКабеля']);
  });
});
