import { describe, it, expect } from 'vitest';
import {
  parseSchemaFields,
  resolveEffectiveFields,
  chainFieldKeys,
  groupEffectiveFields,
  isSubtypeOf,
  inheritanceDistance,
  isUnionType,
  placeInUnion,
  getDefaultValues,
  isFieldMissing,
  isScalarField,
  isMaterialType,
  materialIdentityKeys,
  identityFieldKeys,
  compositeFieldHasTag,
  collectMaterialRows,
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

// ── Ключи идентичности ОДНОГО типа (issue #663) ───────────────────────────────

describe('identityFieldKeys', () => {
  /**
   * Зеркало серверного `SchemaTags.OrderedKeysWithTag`. В отличие от `materialIdentityKeys` охват —
   * один тип: из этих значений складывается имя, предлагаемое при выносе объекта в общие данные, и
   * тип там известен точно — он объявлен полем.
   */
  it('берёт только поля с тэгом identity', () => {
    const t = dt({ fields: [
      field('Наименование', { tags: ['identity'] }),
      field('Адрес'),
      field('ИНН', { tags: ['identity'] }),
    ] });
    expect(identityFieldKeys(t, [t])).toEqual(['Наименование', 'ИНН']);
  });

  it('номер у тэга задаёт порядок компонентов, а не порядок полей в схеме', () => {
    const t = dt({ fields: [
      field('Наименование', { tags: ['identity:2'] }),
      field('ИНН', { tags: ['identity:1'] }),
    ] });
    expect(identityFieldKeys(t, [t])).toEqual(['ИНН', 'Наименование']);
  });

  it('поле без номера идёт после нумерованных', () => {
    const t = dt({ fields: [
      field('Наименование', { tags: ['identity'] }),
      field('ИНН', { tags: ['identity:1'] }),
    ] });
    expect(identityFieldKeys(t, [t])).toEqual(['ИНН', 'Наименование']);
  });

  it('наследует поля предка, и они идут первыми — как их показывает форма', () => {
    const base = dt({ fields: [field('Наименование', { tags: ['identity'] })] }, null, 'idk-base');
    const child = dt({ fields: [field('СРО', { tags: ['identity'] })] }, 'idk-base', 'idk-child');
    expect(identityFieldKeys(child, [base, child])).toEqual(['Наименование', 'СРО']);
  });

  it('исключённое в подтипе поле в ключ не попадает', () => {
    const base = dt({ fields: [
      field('Наименование', { tags: ['identity'] }),
      field('Адрес', { tags: ['identity'] }),
    ] }, null, 'idk-base2');
    const child = dt({ fields: [], excludedFields: ['Адрес'] }, 'idk-base2', 'idk-child2');
    expect(identityFieldKeys(child, [base, child])).toEqual(['Наименование']);
  });

  it('тип без полей идентичности даёт пустой список — имя тогда берут из сводки', () => {
    const t = dt({ fields: [field('Наименование'), field('Адрес')] });
    expect(identityFieldKeys(t, [t])).toEqual([]);
  });
});

// ── Материалы через union-обёртку (issue #648) ────────────────────────────────

describe('материалы за составной обёрткой', () => {
  /**
   * Живой случай: пользователь внёс материалы прямо в АОСР — inline-ветка union-поля «Материалы»
   * (#320), а не ссылку на реестр. Обход «ровно на один уровень» до типа «Материал» не доходил:
   * закладки «Документы качества» у документа не было вовсе, подобрать сертификаты негде.
   *
   *   АОСР → «Материалы» (complex, тип «МатериалыАОСР», тэг union)
   *        → «Материалы» (array, тип «Материал») | «Реестр» (doc-ref)
   *        → «ДокументПодтверждающийКачество» (тэг material.qualityDocLink)
   */
  const material = dt({ fields: [
    field('Наименование', { tags: ['identity'] }),
    field('Артикул', { tags: ['identity'] }),
    field('ДокументПодтверждающийКачество', { type: 'complex', tags: ['material.qualityDocLink'] }),
  ] }, null, 'material');
  const wrapper = dt({ tags: ['type.union'], fields: [
    field('Материалы', { type: 'array', typeId: 'material' }),
    field('Реестр', { type: 'doc-ref', typeId: 'registry' }),
  ] }, null, 'wrapper');
  const aosr = dt({ fields: [
    field('Номер'),
    field('Материалы', { type: 'complex', typeId: 'wrapper' }),
  ] }, null, 'aosr');
  const registry = dt({ fields: [field('Материалы', { type: 'array', typeId: 'material' })] }, null, 'registry');
  const all = [material, wrapper, aosr, registry];

  it('тэг находится через обёртку — закладка у АОСР появляется', () => {
    expect(compositeFieldHasTag(aosr, 'material.qualityDocLink', all)).toBe(true);
  });

  it('прямой массив материалов (реестр) работает как работал', () => {
    expect(compositeFieldHasTag(registry, 'material.qualityDocLink', all)).toBe(true);
  });

  it('тип без материалов остаётся без закладки', () => {
    const plain = dt({ fields: [field('Работы', { type: 'array', typeId: 'work' })] }, null, 'plain');
    const work = dt({ fields: [field('Наименование')] }, null, 'work');
    expect(compositeFieldHasTag(plain, 'material.qualityDocLink', [plain, work])).toBe(false);
  });

  it('тип, ссылающийся сам на себя, не зацикливает обход', () => {
    const selfRef = dt({ fields: [field('Вложенное', { type: 'complex', typeId: 'self' })] }, null, 'self');
    expect(compositeFieldHasTag(selfRef, 'material.qualityDocLink', [selfRef])).toBe(false);
  });


  it('глубоко вложенный тэг находится — предел глубины не отсекает ветку', () => {
    // Цепочка длиннее прежнего предела (6): тип, срезанный по глубине, помечался посещённым, и
    // соседняя ветка, где тэг нашёлся бы, получала «нет» без проверки.
    const chain = Array.from({ length: 9 }, (_, i) => dt({ fields: [
      i === 8
        ? field('ДокументПодтверждающийКачество', { type: 'complex', tags: ['material.qualityDocLink'] })
        : field('Дальше', { type: 'complex', typeId: `lvl${i + 1}` }),
    ] }, null, `lvl${i}`));
    const root = dt({ fields: [field('Вложенное', { type: 'complex', typeId: 'lvl0' })] }, null, 'deep-root');
    expect(compositeFieldHasTag(root, 'material.qualityDocLink', [root, ...chain])).toBe(true);
  });

  // ── collectMaterialRows ─────────────────────────────────────────────────────

  it('строки материалов достаются из-под обёртки', () => {
    const requisites = { Номер: '1', Материалы: { Материалы: [
      { Наименование: 'проверка', Артикул: '2342' },
      { Наименование: 'кабель', Артикул: '77' },
    ] } };
    expect(collectMaterialRows(aosr, all, requisites).map(r => r.Артикул)).toEqual(['2342', '77']);
  });

  it('прямой массив материалов собирается как прежде', () => {
    const requisites = { Материалы: [{ Наименование: 'кабель', Артикул: '77' }] };
    expect(collectMaterialRows(registry, all, requisites)).toHaveLength(1);
  });

  it('выбранная ветка union со ссылкой на реестр материалов не даёт', () => {
    // Ссылку не разворачиваем: материалы чужой записи приходят на вкладку набором данных.
    const requisites = { Материалы: { Реестр: { $ref: 'instance:123', displayName: 'Реестр ЭОМ-1' } } };
    expect(collectMaterialRows(aosr, all, requisites)).toEqual([]);
  });

  it('пустая обёртка и отсутствующие ключи материалов не дают', () => {
    expect(collectMaterialRows(aosr, all, {})).toEqual([]);
    expect(collectMaterialRows(aosr, all, { Материалы: {} })).toEqual([]);
  });
});

// ── Куда ляжет запись каталога в union-строке (issue #747) ───────────────────

/**
 * Правило матчинга варианта. Проверяем не «работает ли фильтр», а те четыре исхода, спутав любые
 * два из которых, приложение ломается по-разному: голая ссылка вместо обёртки нарушит инвариант
 * «ровно один ключ» (#320), обёртка вместо голой — сломает round-trip с «Вынести в общие данные»,
 * тихий выбор при ничьей поставит запись не в тот вариант, а «none» вместо ничьей уберёт из пикера
 * запись, которую человек видит в каталоге.
 */
describe('placeInUnion', () => {
  const base = dt({ fields: [] }, null, 'base');
  const child = dt({ fields: [] }, 'base', 'child');
  const other = dt({ fields: [] }, null, 'other');
  const listItem = dt({ fields: [] }, null, 'listItem');
  const union = dt({
    tags: ['type.union'],
    fields: [
      { key: 'Осн', title: 'Основной', type: 'doc-ref', typeId: 'base', required: false },
      { key: 'Список', title: 'Список', type: 'array', typeId: 'listItem', required: false },
    ],
  }, null, 'union');
  const all = [base, child, other, listItem, union];

  it('запись типа самого union-а кладётся голой ссылкой', () => {
    expect(placeInUnion('union', union, all)).toEqual({ kind: 'self' });
  });

  it('запись типа варианта заворачивается в его ключ', () => {
    expect(placeInUnion('base', union, all)).toEqual({ kind: 'variant', variantKey: 'Осн' });
  });

  it('подтип цели варианта тоже подходит', () => {
    expect(placeInUnion('child', union, all)).toEqual({ kind: 'variant', variantKey: 'Осн' });
  });

  it('чужой тип не подходит ничему', () => {
    expect(placeInUnion('other', union, all)).toEqual({ kind: 'none' });
  });

  it('варианты-списки не участвуют: одиночная ссылка в массиве пропала бы молча', () => {
    expect(placeInUnion('listItem', union, all)).toEqual({ kind: 'none' });
  });

  it('из нескольких подходящих вариантов побеждает ближайший по цепочке', () => {
    const u = dt({
      tags: ['type.union'],
      fields: [
        { key: 'Дальний', title: 'Дальний', type: 'doc-ref', typeId: 'base', required: false },
        { key: 'Ближний', title: 'Ближний', type: 'doc-ref', typeId: 'child', required: false },
      ],
    }, null, 'u2');
    expect(placeInUnion('child', u, [...all, u])).toEqual({ kind: 'variant', variantKey: 'Ближний' });
  });

  it('два варианта на один тип дают ничью — выбрать за пользователя нечем', () => {
    // Живой случай: у «Кабельной линии» варианты освещения внутреннего и наружного смотрят на один
    // тип «Основная кабельная линия» — форма данных одна, различие живёт только в ключе варианта.
    const u = dt({
      tags: ['type.union'],
      fields: [
        { key: 'ЭО', title: 'Внутреннее', type: 'complex', typeId: 'base', required: false },
        { key: 'ЭОН', title: 'Наружное', type: 'complex', typeId: 'base', required: false },
      ],
    }, null, 'u3');
    expect(placeInUnion('base', u, [...all, u])).toEqual({ kind: 'ambiguous', variantKeys: ['ЭО', 'ЭОН'] });
  });

  /**
   * Документ комплекта целиком — второй вид источника (issue #751).
   *
   * Проверяем именно РАЗЛИЧИЕ видов, а не «фильтр работает»: complex-вариант объявлен на составной
   * тип и ждёт значение, doc-ref — документ. Сотри мы различие, документ уехал бы в complex-вариант
   * всюду, где типы случайно совпали, и никто бы не заметил: строка осталась бы валидной по форме.
   */
  describe('источник — документ комплекта, а не значение', () => {
    const mixed = dt({
      tags: ['type.union'],
      fields: [
        { key: 'Док', title: 'Документ', type: 'doc-ref', typeId: 'base', required: false },
        { key: 'Знач', title: 'Значение', type: 'complex', typeId: 'other', required: false },
      ],
    }, null, 'mixed');
    const mixedAll = [...all, mixed];

    it('документ ложится в doc-ref-вариант', () => {
      expect(placeInUnion('base', mixed, mixedAll, 'document'))
        .toEqual({ kind: 'variant', variantKey: 'Док' });
    });

    it('в complex-вариант документ не ложится, хотя значение того же типа — ложится', () => {
      expect(placeInUnion('other', mixed, mixedAll, 'document')).toEqual({ kind: 'none' });
      expect(placeInUnion('other', mixed, mixedAll, 'value'))
        .toEqual({ kind: 'variant', variantKey: 'Знач' });
    });

    it('документ типа самого union-а не становится строкой целиком', () => {
      // Для значения это «self» — запись каталога заводит «Вынести в общие данные». Для документа
      // self означал бы «положить документ вместо всей строки», а строкой типизирован union.
      expect(placeInUnion('union', union, all, 'value')).toEqual({ kind: 'self' });
      expect(placeInUnion('union', union, all, 'document')).toEqual({ kind: 'none' });
    });

    it('ничья среди doc-ref-вариантов спрашивается так же, как у записи', () => {
      const u = dt({
        tags: ['type.union'],
        fields: [
          { key: 'А', title: 'А', type: 'doc-ref', typeId: 'base', required: false },
          { key: 'Б', title: 'Б', type: 'doc-ref', typeId: 'base', required: false },
        ],
      }, null, 'u4');
      expect(placeInUnion('base', u, [...all, u], 'document'))
        .toEqual({ kind: 'ambiguous', variantKeys: ['А', 'Б'] });
    });

    it('подтип цели doc-ref-варианта подходит и документом', () => {
      expect(placeInUnion('child', mixed, mixedAll, 'document'))
        .toEqual({ kind: 'variant', variantKey: 'Док' });
    });

    it('вид источника по умолчанию — значение: прежние вызовы ведут себя как раньше', () => {
      expect(placeInUnion('base', union, all)).toEqual(placeInUnion('base', union, all, 'value'));
    });
  });

  it('union опознаётся по УНАСЛЕДОВАННОМУ и по параметризованному тэгу', () => {
    const parametrized = dt({ tags: ['type.union:2'], fields: [] }, null, 'p');
    const heir = dt({ fields: [] }, 'p', 'heir');
    expect(isUnionType(parametrized, [parametrized, heir])).toBe(true);
    expect(isUnionType(heir, [parametrized, heir])).toBe(true);
  });
});

describe('inheritanceDistance', () => {
  const a = dt({ fields: [] }, null, 'a');
  const b = dt({ fields: [] }, 'a', 'b');
  const c = dt({ fields: [] }, 'b', 'c');
  const all = [a, b, c];

  it('считает шаги вверх по цепочке', () => {
    expect(inheritanceDistance('c', 'c', all)).toBe(0);
    expect(inheritanceDistance('c', 'b', all)).toBe(1);
    expect(inheritanceDistance('c', 'a', all)).toBe(2);
    expect(inheritanceDistance('a', 'c', all)).toBeNull();
  });

  it('цикл в цепочке не вешает обход', () => {
    // Цепочку типов строит пользователь, и испорченный parentId раньше валил вкладку
    // переполнением стека: у прежней isSubtypeOf не было ни предела шагов, ни visited-set.
    const x = dt({ fields: [] }, 'y', 'x');
    const y = dt({ fields: [] }, 'x', 'y');
    expect(inheritanceDistance('x', 'somewhere', [x, y])).toBeNull();
    expect(isSubtypeOf('x', 'somewhere', [x, y])).toBe(false);
  });
});
