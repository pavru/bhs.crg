import { describe, it, expect } from 'vitest';
import type { CoverageField, CoverageGroup, CoverageRow, CoverageType } from '@/shared/api/fieldMapping';
import { offeredInRoles, type TagDefinition } from '@/shared/api/tags';
import {
  NOT_ASSIGNED, changedRows, droppedNotice, fieldLabel, fullSentence, listedFields, missingKey, offeredFields,
  reconciled, restoreTags, schemaWrites, selectedKey, unfitKey, withTagPlacements, withTypeTag,
} from './fieldMapping';

const ORG: CoverageType = { id: 'org', name: 'Организация', code: 'Организация', kind: 'Composite' };
const PARTY: CoverageType = { id: 'party', name: 'Сторона', code: 'Сторона', kind: 'Composite' };

function field(key: string, over: Partial<CoverageField> = {}): CoverageField {
  return { key, title: key, type: 'string', declaredIn: ORG, fits: true, tagged: false, ...over };
}

function row(over: Partial<CoverageRow> = {}): CoverageRow {
  return {
    tag: 'org.inn', label: 'ИНН организации', description: '', required: true, withoutIt: 'Не ищется.',
    state: 'mapped', fault: 'None', reason: null, key: 'ИНН', kinds: ['string'],
    fields: [field('ИНН', { tagged: true }), field('Код')],
    ...over,
  };
}

function group(...rows: CoverageRow[]): CoverageGroup {
  return {
    role: 'role.organization', label: 'Справочник организаций', purpose: '', withoutIt: '',
    state: 'mapped', reason: null, type: ORG, candidates: [], rows,
  };
}

describe('что уйдёт в схему', () => {
  it('тот же выбор — не правка', () => {
    const g = group(row());
    expect(changedRows(g, { 'org.inn': 'ИНН' })).toHaveLength(0);
    expect(schemaWrites(g, { 'org.inn': 'ИНН' })).toHaveLength(0);
  });

  it('замена поля — одна правка типа: тэг переезжает', () => {
    const writes = schemaWrites(group(row()), { 'org.inn': 'Код' });

    expect(writes).toEqual([{ typeId: 'org', typeName: 'Организация', placements: [{ tag: 'org.inn', key: 'Код' }] }]);
  });

  // Нарушение «два поля»: выбор одного из двух обязан снять тэг со второго — даже когда выбрано то,
  // что уже помечено. Иначе «выберите одно» ничего бы не чинило.
  it('у нарушенной строки правка — любой выбор, даже совпавший с помеченным полем', () => {
    const twice = row({
      state: 'violated', fault: 'Several', key: null,
      fields: [field('ИНН', { tagged: true }), field('ИННСтарый', { tagged: true })],
    });

    expect(schemaWrites(group(twice), { 'org.inn': 'ИНН' }))
      .toEqual([{ typeId: 'org', typeName: 'Организация', placements: [{ tag: 'org.inn', key: 'ИНН' }] }]);
  });

  // Тэг унаследованного поля лежит у предка. Перенос на собственное поле — две правки: у предка
  // снять, у справочника поставить. Одна правка оставила бы «назначено дважды».
  it('перенос с поля предка на своё — правка каждого типа, у предка тэг снимается', () => {
    const inherited = row({ fields: [field('ИНН', { tagged: true, declaredIn: PARTY }), field('Код')] });

    const writes = schemaWrites(group(inherited), { 'org.inn': 'Код' });

    expect(writes).toEqual([
      { typeId: 'party', typeName: 'Сторона', placements: [{ tag: 'org.inn', key: null }] },
      { typeId: 'org', typeName: 'Организация', placements: [{ tag: 'org.inn', key: 'Код' }] },
    ]);
  });

  it('снятие необязательного — тэг уходит отовсюду', () => {
    const optional = row({ required: false });

    expect(schemaWrites(group(optional), { 'org.inn': null }))
      .toEqual([{ typeId: 'org', typeName: 'Организация', placements: [{ tag: 'org.inn', key: null }] }]);
  });
});

// Список полей перечитывается, и выбранное поле тем временем могли удалить. Ключ, оставшийся в
// черновике, — не выбор: иначе экран считал бы строку изменённой, а запись свелась бы к снятию тэга
// с прежнего поля — повторное «Сохранить» после отказа молча снимало обязательное назначение.
describe('выбор, которого в списке полей больше нет', () => {
  const stale = { 'org.inn': 'Удалённое' };

  it('у назначенной строки — не правка: назначение остаётся, писать нечего', () => {
    const g = group(row());

    expect(changedRows(g, stale)).toHaveLength(0);
    expect(schemaWrites(g, stale)).toHaveLength(0);
    expect(selectedKey(row(), stale)).toBe('ИНН');
  });

  it('у неназначенной — тоже: «сохранено» без единой записи не бывает', () => {
    const none = row({ state: 'absent', fault: 'Absent', key: null, fields: [field('Код')] });

    expect(changedRows(group(none), stale)).toHaveLength(0);
    expect(selectedKey(none, stale)).toBeUndefined();
  });

  it('правки соседних строк при этом остаются', () => {
    const name = row({ tag: 'org.name', key: 'Наим', fields: [field('Наим', { tagged: true }), field('Код')] });
    const g = group(row(), name);

    expect(changedRows(g, { ...stale, 'org.name': 'Код' }).map(r => r.tag)).toEqual(['org.name']);
  });

  it('«Не назначено» выбором остаётся: это не ключ поля', () => {
    const optional = row({ required: false });

    expect(changedRows(group(optional), { 'org.inn': null })).toHaveLength(1);
    expect(selectedKey(optional, { 'org.inn': null })).toBe(NOT_ASSIGNED);
  });

  // Поле на месте, но выбрать его уже нельзя: ему сменили вид или сделали расчётным. Сервер такое
  // поле присылает (оно может нести тэг), поэтому сверка «есть ли поле в строке» его пропускала —
  // строка оставалась «не сохранено», а запись отказывала при каждом нажатии (ревью PR #1319).
  it('поле осталось, но перестало подходить — выбор тоже недействителен', () => {
    const unfit = row({ fields: [field('ИНН', { tagged: true }), field('Код', { fits: false })] });
    const draft = { 'org.inn': 'Код' };

    expect(changedRows(group(unfit), draft)).toHaveLength(0);
    expect(schemaWrites(group(unfit), draft)).toHaveLength(0);
    expect(selectedKey(unfit, draft)).toBe('ИНН');
  });

  // У такой строки списка нет вовсе — она чинится в редакторе типа. Оставшийся выбор держал бы
  // «не сохранено» и кнопку «Сохранить» у строки, в которой выбирать нечем.
  it('строка стала «расходится в подтипе» — выбирать нечем, выбор недействителен', () => {
    const differs = row({ state: 'violated', fault: 'DiffersInSubtype', key: null });

    expect(offeredFields(differs)).toHaveLength(0);
    expect(changedRows(group(differs), { 'org.inn': 'Код' })).toHaveLength(0);
  });

  it('справочник перестал быть назначенным — строк на экране нет, и правок в них тоже', () => {
    const lost: CoverageGroup = { ...group(row()), state: 'violated' };

    expect(changedRows(lost, { 'org.inn': 'Код' })).toHaveLength(0);
    expect(schemaWrites(lost, { 'org.inn': 'Код' })).toHaveLength(0);
  });

  it('«Не назначено» у обязательной строки список не предлагает — это не выбор', () => {
    expect(changedRows(group(row()), { 'org.inn': null })).toHaveLength(0);
  });
});

// Недействительный выбор мало не читать: спящий ключ оживает, стоит полю с тем же ключом появиться
// снова. Поэтому экран вычищает его из черновика с приходом ответа — и говорит об этом словами.
describe('сверка черновика с пришедшим ответом', () => {
  it('действительный выбор остаётся, и объект черновика тот же', () => {
    const draft = { 'org.inn': 'Код' };

    const result = reconciled(group(row()), draft);

    expect(result.draft).toBe(draft);
    expect(result.dropped).toHaveLength(0);
  });

  it('недействительный снимается, соседний остаётся, строка названа', () => {
    const name = row({ tag: 'org.name', label: 'Название организации', key: 'Наим',
      fields: [field('Наим', { tagged: true }), field('Код')] });
    const g = group(row(), name);

    const result = reconciled(g, { 'org.inn': 'Удалённое', 'org.name': 'Код' });

    expect(result.draft).toEqual({ 'org.name': 'Код' });
    expect(result.dropped.map(r => r.tag)).toEqual(['org.inn']);
  });

  // Главное, ради чего чистка: после неё вернувшееся поле с тем же ключом правкой не становится.
  it('снятый выбор не оживает, когда поле с тем же ключом появляется снова', () => {
    const without = group(row({ fields: [field('ИНН', { tagged: true })] }));
    const { draft } = reconciled(without, { 'org.inn': 'Код' });

    expect(changedRows(group(row()), draft)).toHaveLength(0);
    expect(selectedKey(row(), draft)).toBe('ИНН');
  });

  it('строки больше нет или справочник не назначен — выбор снимается весь', () => {
    expect(reconciled(group(), { 'org.inn': 'Код' }).draft).toEqual({});
    const lost: CoverageGroup = { ...group(row()), state: 'absent' };
    expect(reconciled(lost, { 'org.inn': 'Код' }).draft).toEqual({});
  });

  it('сообщение называет строку и зовёт выбрать заново, только если есть из чего', () => {
    const g = group(row());
    expect(droppedNotice(g, [row()]))
      .toBe('Выбор для «ИНН организации» отменён: тип тем временем изменили, и список этого больше не предлагает. Выберите заново.');

    const nothing = row({ fields: [field('ИНН', { tagged: true, fits: false })] });
    expect(droppedNotice(group(nothing), [nothing])).not.toContain('Выберите заново');
    expect(droppedNotice(g, [])).toBe('Несохранённый выбор отменён: справочник тем временем изменили.');
  });
});

describe('правка сырой схемы', () => {
  const schema = {
    help: 'справка', excludedFields: ['Чужое'],
    fields: [
      { key: 'ИНН', type: 'string', tags: ['org.inn', 'identity:2'], required: true },
      { key: 'Код', type: 'string' },
    ],
  };

  it('тэг переезжает, всё прочее в схеме остаётся как было', () => {
    const next = withTagPlacements(schema, [{ tag: 'org.inn', key: 'Код' }]);

    expect(next).toEqual({
      help: 'справка', excludedFields: ['Чужое'],
      fields: [
        { key: 'ИНН', type: 'string', tags: ['identity:2'], required: true },
        { key: 'Код', type: 'string', tags: ['org.inn'] },
      ],
    });
  });

  // Пустое сохранение легло бы в журнал действий записью «изменена схема», в которой ничего не изменено.
  it('менять нечего — возвращается тот же объект', () => {
    expect(withTagPlacements(schema, [{ tag: 'org.inn', key: 'ИНН' }])).toBe(schema);
    expect(withTypeTag({ ...schema, tags: ['role.organization'] }, 'role.organization').tags).toEqual(['role.organization']);
  });

  it('тэг типа дописывается к стоящим', () => {
    expect(withTypeTag({ tags: ['type.union'], fields: [] }, 'role.organization').tags)
      .toEqual(['type.union', 'role.organization']);
    expect(withTypeTag({ fields: [] }, 'role.organization').tags).toEqual(['role.organization']);
  });

  // Список на экране мог устареть. Тэг несуществующему полю не поставить, а с прежнего он снялся
  // бы — назначение исчезло бы молча.
  it('поля в свежей схеме уже нет — это названо, а не записано', () => {
    expect(missingKey(schema, [{ tag: 'org.inn', key: 'Удалённое' }])).toBe('Удалённое');
    expect(missingKey(schema, [{ tag: 'org.inn', key: 'Код' }, { tag: 'org.name', key: null }])).toBeNull();
  });
});

describe('свежая схема перед записью', () => {
  const schema = {
    fields: [
      { key: 'ИНН', type: 'string', tags: ['org.inn:2'] },
      { key: 'Реквизиты', type: 'complex' },
      { key: 'Итог', type: 'string', computed: true },
    ],
  };
  const kinds = { 'org.inn': ['string', 'text'], 'any.tag': [] };

  // Вид поля на экране мог устареть так же, как список: «сохранено» и тут же «нарушено» — не ответ.
  it('поле сменило вид или стало расчётным — названо, а не записано', () => {
    expect(unfitKey(schema, [{ tag: 'org.inn', key: 'Реквизиты' }], kinds)).toBe('Реквизиты');
    expect(unfitKey(schema, [{ tag: 'org.inn', key: 'Итог' }], kinds)).toBe('Итог');
    expect(unfitKey(schema, [{ tag: 'org.inn', key: 'ИНН' }, { tag: 'org.inn', key: null }], kinds)).toBeNull();
    // Поле без вида сервер читает строкой — отказа ему нет.
    expect(unfitKey({ fields: [{ key: 'Старое' }] }, [{ tag: 'org.inn', key: 'Старое' }], kinds)).toBeNull();
    // Тэг без ограничения по виду ложится на любое поле, кроме расчётного.
    expect(unfitKey(schema, [{ tag: 'any.tag', key: 'Реквизиты' }], kinds)).toBeNull();
  });

  it('откат возвращает тэг туда, где он стоял, — с параметром и не трогая прочее', () => {
    const moved = {
      fields: [
        { key: 'ИНН', type: 'string', tags: [] },
        { key: 'Реквизиты', type: 'complex', tags: ['org.inn', 'identity'] },
        { key: 'Новое', type: 'string' },
      ],
    };

    expect(restoreTags(moved, schema, ['org.inn'])).toEqual({
      fields: [
        { key: 'ИНН', type: 'string', tags: ['org.inn:2'] },
        { key: 'Реквизиты', type: 'complex', tags: ['identity'] },
        { key: 'Новое', type: 'string' },
      ],
    });
    expect(restoreTags(schema, schema, ['org.inn'])).toBe(schema);
  });

  it('причина сервера становится законченной фразой', () => {
    expect(fullSentence('поле назначено дважды')).toBe('Поле назначено дважды.');
    expect(fullSentence('Тип закрыт.')).toBe('Тип закрыт.');
    expect(fullSentence('')).toBe('');
  });
});

describe('список полей строки', () => {
  it('негодное поле показывается, только если тэг стоит на нём', () => {
    const r = row({
      state: 'violated', fault: 'WrongKind', key: null,
      fields: [field('ИНН', { tagged: true, fits: false, type: 'complex' }), field('Код'),
        field('Логотип', { fits: false, type: 'image' })],
    });

    expect(listedFields(r).map(f => f.key)).toEqual(['ИНН', 'Код']);
    // Одно негодное поле с тэгом стоит в списке выбранным: пустой список скрыл бы, что тэг стоит.
    expect(selectedKey(r, {})).toBe('ИНН');
  });

  it('два поля с тэгом — не выбрано ни одно', () => {
    const r = row({
      state: 'violated', fault: 'Several', key: null,
      fields: [field('ИНН', { tagged: true }), field('ИННСтарый', { tagged: true })],
    });

    expect(selectedKey(r, {})).toBeUndefined();
    // И у необязательной: стой там «Не назначено», выбрать его ещё раз было бы нельзя — и снять тэг
    // с обоих полей было бы нечем.
    expect(selectedKey({ ...r, required: false }, {})).toBeUndefined();
    expect(changedRows(group({ ...r, required: false }), { 'org.inn': null })).toHaveLength(1);
  });

  it('необязательное без назначения стоит на «Не назначено», обязательное — пусто', () => {
    const none = { state: 'absent' as const, fault: 'Absent' as const, key: null, fields: [field('Код')] };

    expect(selectedKey(row({ ...none, required: false }), {})).toBe(NOT_ASSIGNED);
    expect(selectedKey(row(none), {})).toBeUndefined();
  });

  it('ключ дописывается только полям с одинаковым названием', () => {
    const a = field('Наим', { title: 'Наименование' });
    const b = field('Наим2', { title: 'Наименование' });
    const c = field('ИНН');

    expect(fieldLabel(a, [a, b, c])).toBe('Наименование · Строка · ключ «Наим»');
    expect(fieldLabel(c, [a, b, c])).toBe('ИНН · Строка');
  });
});

describe('тэги справочника в редакторе поля', () => {
  const tag = (code: string, role?: string): TagDefinition => ({
    code, label: code, description: '', scope: 'Field', appliesTo: [], multiple: false, owner: 'core', role,
  });
  const all = [tag('identity'), tag('org.inn', 'role.organization')];

  it('тэг справочника предлагается только полю справочника', () => {
    expect(offeredInRoles(all, []).map(t => t.code)).toEqual(['identity']);
    expect(offeredInRoles(all, ['role.organization']).map(t => t.code)).toEqual(['identity', 'org.inn']);
  });

  // Тэг, оставшийся на поле чужого типа, из списка не пропадает: иначе снять его было бы нечем.
  it('уже поставленный тэг остаётся в списке', () => {
    expect(offeredInRoles(all, [], ['org.inn']).map(t => t.code)).toEqual(['identity', 'org.inn']);
  });

  it('роли не названы — отбора нет', () => {
    expect(offeredInRoles(all).map(t => t.code)).toEqual(['identity', 'org.inn']);
  });
});
