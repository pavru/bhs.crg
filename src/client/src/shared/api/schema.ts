import type { DocumentType } from './types';
import { FUNCTIONAL_TAG, findTagEntry, hasTag, tagOrder } from './tags';
import { ROW_UID, withRowUid } from '@/shared/utils/rowIdentity';

/**
 * Личность поля в редакторе схемы — частный случай личности строки списка (issue #527, #517).
 * Почему не ключ поля и не индекс — см. [[ROW_UID]]: ключ как раз переименовывают, а индекс не
 * переживает удаление и перетаскивание, и состояние карточки достаётся чужому полю.
 */
export const FIELD_UID: typeof ROW_UID = ROW_UID;

/** Поле с личностью: уже помеченное возвращается как есть. */
export function withFieldUid<T extends SchemaField>(field: T): T & { [FIELD_UID]: string } {
  return withRowUid(field) as T & { [FIELD_UID]: string };
}

export interface SchemaField {
  /** Личность поля в редакторе (см. FIELD_UID). В сохранённой схеме отсутствует. */
  [FIELD_UID]?: string;
  key: string;
  title: string;
  /** Primitive type, 'enum' for a fixed list, 'complex' for composite type, 'primitive' for user-defined constrained type, 'array' for repeating rows, 'doc-ref'/'doc-array' for document instance links */
  type: 'string' | 'text' | 'number' | 'date' | 'boolean' | 'enum' | 'complex' | 'primitive' | 'array' | 'doc-ref' | 'doc-array' | 'image' | 'file';
  /** Set when type === 'complex', 'array', or 'primitive'. ID of the composite DocumentType or PrimitiveType. */
  typeId?: string;
  /** Set when type === 'enum'. List of allowed string values. */
  options?: string[];
  required: boolean;
  /** Pre-filled value for new entries. For 'array' fields use [] as default. */
  defaultValue?: unknown;
  /** Functional tags binding this field to hard-coded behaviour (registry: GET /api/tags). */
  tags?: string[];
  /** Расчётное поле (issue #368): значение вычисляется `expression` по другим полям при генерации,
   *  вручную не вводится и не хранится. `type` = тип результата. */
  computed?: boolean;
  /** JS/Jint-выражение расчётного поля (читает соседние поля через get("ключ")). */
  expression?: string;
}

/** Опции отображения картинки (width/height/align/fit). Задаются в значении инстанса (issue #246). */
export interface ImageOptions {
  /** Typst length, e.g. "4cm", "100%". */
  width?: string;
  height?: string;
  align?: 'left' | 'center' | 'right';
  fit?: 'cover' | 'contain' | 'stretch';
}

/** Значение поля-картинки: data-URI + опции размера/выравнивания (issue #246). */
export interface ImageValue extends ImageOptions {
  /** data:image/...;base64,... */
  src: string;
}

/**
 * Значение поля-картинки, лежащей в блоб-хранилище (issue #522). Новая форма записи; читать data-URI
 * (`ImageValue`) при этом не перестаём никогда — восстановление бэкапа заново впрыскивает старую
 * форму, а архивы восстановимы неограниченно долго.
 *
 * Дискриминатор `"image"`, не `"file"`: узел вложения (`FileAttachment`) обслуживается другим путём
 * и несёт другой контракт — свести их значило бы отнять у картинки опции размера.
 */
export interface ImageBlobValue extends ImageOptions {
  $type: 'image';
  /** Рабочая картинка: уменьшенная копия, если уменьшение понадобилось (issue #523). */
  blobPath: string;
  /** Оригинал как загрузили. Есть только если копия отличается от него. */
  originalBlobPath?: string;
  fileName: string;
  mimeType: string;
}

export interface FieldGroup {
  key: string;
  title: string;
  /** Ordered list of field keys that belong to this group (inherited + own). */
  fieldKeys: string[];
}

/** A named Typst rendering function for a Composite type. */
export interface TypstRender {
  /** Display label shown in the UI, e.g. "Краткое", "Для печати" */
  name: string;
  /** Typst function name (ASCII), e.g. "render_org_short" */
  fnName: string;
  /** Complete Typst expression used as the function body, e.g. "[*#it.НаимКраткое*]" */
  block: string;
}

export interface SchemaDefinition {
  fields: SchemaField[];
  groups?: FieldGroup[];
  excludedFields?: string[];
  fieldOverrides?: Record<string, { required?: boolean; defaultValue?: unknown }>;
  /** Named Typst rendering functions for Composite types (generated as preamble before template). */
  typstRenders?: TypstRender[];
  /** Type-level functional tags (registry: GET /api/tags, scope=Type). */
  tags?: string[];
  /** Явный порядок полей секции «Без группы» (свои + унаследованные, по ключу). Ключи не из списка
   *  сохраняют относительный порядок в конце (унаследованные — по родителю, свои — по массиву fields). */
  ungroupedOrder?: string[];
  /** Справка для пользователя (markdown) — показывается при редактировании инстанса типа. */
  help?: string;
}

/**
 * Returns fields organized by their groups.
 * Fields not in any group appear first (ungrouped), then each defined group.
 * If no groups defined, returns a single ungrouped section.
 */
export function groupEffectiveFields(
  fields: SchemaField[],
  schema: Record<string, unknown>,
): Array<{ key: string; title: string | null; fields: SchemaField[] }> {
  const def = schema as unknown as SchemaDefinition;
  const groups = def.groups ?? [];

  // Явный порядок «Без группы» (issue: DnD унаследованных в ungrouped) — стабильная сортировка,
  // ключи вне ungroupedOrder сохраняют относительный порядок (унаслед.→свои).
  const order = def.ungroupedOrder ?? [];
  const sortUngrouped = <T extends SchemaField>(arr: T[]): T[] => {
    if (order.length === 0) return arr;
    const pos = new Map(order.map((k, i) => [k, i] as const));
    const rank = (k: string) => (pos.has(k) ? pos.get(k)! : Number.POSITIVE_INFINITY);
    return [...arr].sort((a, b) => rank(a.key) - rank(b.key));
  };

  if (groups.length === 0) return [{ key: '__all__', title: null, fields: sortUngrouped(fields) }];

  const groupedKeys = new Set(groups.flatMap(g => g.fieldKeys));
  const ungrouped = sortUngrouped(fields.filter(f => !groupedKeys.has(f.key)));
  const result: Array<{ key: string; title: string | null; fields: SchemaField[] }> = [];

  if (ungrouped.length > 0) result.push({ key: '__ungrouped__', title: null, fields: ungrouped });

  for (const group of groups) {
    const gFields = group.fieldKeys
      .map(k => fields.find(f => f.key === k))
      .filter((f): f is SchemaField => f != null);
    if (gFields.length > 0) result.push({ key: group.key, title: group.title, fields: gFields });
  }
  return result;
}

/** Parses own fields from a raw schema object. */
export function parseSchemaFields(schema: Record<string, unknown>): SchemaField[] {
  const fields = schema?.fields;
  if (!Array.isArray(fields)) return [];
  return (fields as Partial<SchemaField>[]).map(f => withFieldUid({
    key: f.key ?? '',
    title: f.title ?? '',
    type: (f.type as SchemaField['type']) ?? 'string',
    typeId: f.typeId,
    options: f.options,
    required: f.required ?? false,
    defaultValue: f.defaultValue,
    tags: f.tags,
    computed: f.computed,
    expression: f.expression,
  }));
}

/**
 * Все ключи полей, объявленные в цепочке наследования, — ДО применения исключений (issue #639).
 *
 * Отличается от {@link resolveEffectiveFields} ровно исключениями, и это принципиально: исключение
 * ссылается на поле, которое существует, — в этом его смысл. Эффективный набор родителя таких полей
 * уже не содержит, и по нему собственное исключение потомка выглядело бы ссылкой в пустоту.
 *
 * Случай не выдуманный: дед объявляет «Примечание», отец его исключает, сын исключает тоже. По
 * эффективным ключам отца исключение сына оказалось бы «висячим» — и было бы предложено убрать,
 * а вместе с ним и намерение: вернув поле у отца, сын молча получил бы то, от чего отказывался.
 */
export function chainFieldKeys(docType: DocumentType, allDocTypes: DocumentType[]): string[] {
  const keys: string[] = [];
  const visited = new Set<string>();
  let current: DocumentType | undefined = docType;
  while (current && !visited.has(current.id)) {
    visited.add(current.id);
    keys.push(...parseSchemaFields(current.schema).map(f => f.key));
    current = current.parentId ? allDocTypes.find(dt => dt.id === current!.parentId) : undefined;
  }
  return keys;
}

/**
 * Resolves the effective (merged) field list for a document type,
 * walking the inheritance chain: parent fields first, then own fields.
 * Applies excludedFields and fieldOverrides from the child schema.
 */
export function resolveEffectiveFields(
  docType: DocumentType,
  allDocTypes: DocumentType[],
  /** Пройденные типы — защита от цикла в цепочке наследования (issue #747); внутренний параметр. */
  visited: Set<string> = new Set(),
): SchemaField[] {
  const schema = docType.schema as unknown as SchemaDefinition;
  const ownFields = parseSchemaFields(docType.schema);

  if (!docType.parentId || visited.has(docType.id)) return ownFields;
  visited.add(docType.id);

  const parent = allDocTypes.find(dt => dt.id === docType.parentId);
  if (!parent) return ownFields;

  const parentFields = resolveEffectiveFields(parent, allDocTypes, visited);
  const excluded = new Set(schema.excludedFields ?? []);
  const overrides = schema.fieldOverrides ?? {};

  const inheritedFields = parentFields
    .filter(f => !excluded.has(f.key))
    .map(f => {
      const ov = overrides[f.key];
      if (!ov) return f;
      return {
        ...f,
        ...(ov.required !== undefined ? { required: ov.required } : {}),
        ...(ov.defaultValue !== undefined ? { defaultValue: ov.defaultValue } : {}),
      };
    });

  const inheritedKeys = new Set(inheritedFields.map(f => f.key));
  return [
    ...inheritedFields,
    ...ownFields.filter(f => !inheritedKeys.has(f.key)),
  ];
}

/**
 * Сколько шагов вверх по цепочке наследования от `childId` до `parentId`; `null` — не потомок.
 * 0 — это тот же тип.
 *
 * Дистанция, а не просто «да/нет», нужна выбору варианта union'а (issue #747): когда запись
 * подходит нескольким вариантам, побеждает БЛИЖАЙШИЙ. Ту же специфичность считает сервер при
 * материализации наборов (`MaterializeDiscriminator.InheritanceDistance`, issue #716) — правила там
 * задаёт админ явно, а здесь они выводятся из `typeId` варианта, но мера близости обязана совпадать.
 *
 * <p>Обход итеративный и с visited-set. Прежняя <code>isSubtypeOf</code> была рекурсивна без предела
 * и без защиты от цикла: испорченный <code>parentId</code> (цепочку типов строит пользователь) вешал
 * вкладку переполнением стека. У серверного аналога предел 32 шага, у <code>typeHasTag</code> —
 * visited-set; здесь не было ничего. По той же цепочке ходит <code>resolveEffectiveFields</code>, и
 * её защитили заодно: закрывать один вход из двух бессмысленно — цикл валил бы вкладку следующей же
 * строкой.</p>
 */
export function inheritanceDistance(
  childId: string, parentId: string, allDocTypes: DocumentType[],
): number | null {
  const visited = new Set<string>();
  let current: string | undefined = childId;
  let steps = 0;
  while (current && !visited.has(current)) {
    if (current === parentId) return steps;
    visited.add(current);
    current = allDocTypes.find(t => t.id === current)?.parentId ?? undefined;
    steps++;
  }
  return null;
}

/** Returns true if childId equals parentId or has parentId anywhere in its ancestor chain. */
export function isSubtypeOf(childId: string, parentId: string, allDocTypes: DocumentType[]): boolean {
  return inheritanceDistance(childId, parentId, allDocTypes) !== null;
}

/**
 * True if the type has an array/complex field whose row composite type (incl. inheritance)
 * carries a field with the given functional tag. Used e.g. to detect documents that require
 * quality documents (material.qualityDocLink on a material row type).
 *
 * Обход рекурсивный, ВСЕЙ цепочкой составных полей (issue #648). Спуск ровно на один уровень
 * пропускал материалы в АОСР: union-обёртка «массив ИЛИ ссылка на реестр» (#320) добавила
 * промежуточный составной тип, и «Материалы» стали лежать на два уровня — АОСР → «МатериалыАОСР»
 * → массив «Материал». Тэг не находился, закладки «Документы качества» у документа не было вовсе,
 * и подобрать сертификаты к внесённым вручную материалам было негде.
 *
 * Тэг ищется на полях ВЛОЖЕННЫХ типов, а не на собственных полях документа — как и раньше:
 * тэг материала стоит на строке материала, а не на поле, которое их содержит.
 */
export function compositeFieldHasTag(docType: DocumentType, tag: string, allDocTypes: DocumentType[]): boolean {
  // Глубину НЕ ограничиваем: обход конечен сам по себе — каждый тип посещается один раз, а типов
  // конечное число. Предел глубины вместе с этой пометкой давал бы неверный ответ: тип, срезанный
  // по глубине в одной ветке, считался бы «проверенным» и в другой, где тэг нашёлся бы.
  const visited = new Set<string>(); // цикл (тип, ссылающийся на себя) — законная схема
  function descend(typeId: string): boolean {
    if (visited.has(typeId)) return false;
    visited.add(typeId);
    const ct = allDocTypes.find(d => d.id === typeId);
    if (!ct) return false;
    return resolveEffectiveFields(ct, allDocTypes).some(cf =>
      hasTag(cf.tags, tag)
      || ((cf.type === 'array' || cf.type === 'complex') && !!cf.typeId && descend(cf.typeId)));
  }
  return resolveEffectiveFields(docType, allDocTypes).some(f =>
    (f.type === 'array' || f.type === 'complex') && !!f.typeId && descend(f.typeId));
}

/** Предел вложенности при обходе ДАННЫХ по схеме — от патологических данных, не от нормы.
 *  (Обходу самой схемы предел не нужен: там каждый тип посещается один раз.) */
const MAX_COMPOSITE_DEPTH = 6;

/**
 * Path (dotted segments) to the first effective field carrying the given functional tag,
 * searching the type's own fields and one level into complex fields. Null if none.
 * E.g. for quality.validUntil → ["ПериодДействия", "Окончание"].
 */
export function findTaggedFieldPath(docType: DocumentType, tag: string, allDocTypes: DocumentType[]): string[] | null {
  const eff = resolveEffectiveFields(docType, allDocTypes);
  const direct = eff.find(f => hasTag(f.tags, tag));
  if (direct) return [direct.key];
  for (const f of eff) {
    if (f.type === 'complex' && f.typeId) {
      const ct = allDocTypes.find(d => d.id === f.typeId);
      if (!ct) continue;
      const inner = resolveEffectiveFields(ct, allDocTypes).find(cf => hasTag(cf.tags, tag));
      if (inner) return [f.key, inner.key];
    }
  }
  return null;
}

/** True if the type (or any ancestor) carries the given type-level functional tag (schema.tags). */
export function typeHasTag(docType: DocumentType, tag: string, allDocTypes: DocumentType[]): boolean {
  let current: DocumentType | undefined = docType;
  const visited = new Set<string>();
  while (current && !visited.has(current.id)) {
    visited.add(current.id);
    const tags = (current.schema as unknown as SchemaDefinition)?.tags;
    if (Array.isArray(tags) && hasTag(tags, tag)) return true;
    current = current.parentId ? allDocTypes.find(t => t.id === current!.parentId) : undefined;
  }
  return false;
}

/** Является ли составной тип union'ом — «заполняется ровно один из вариантов» (issue #320). */
export function isUnionType(docType: DocumentType, allDocTypes: DocumentType[]): boolean {
  return typeHasTag(docType, FUNCTIONAL_TAG.typeUnion, allDocTypes);
}

/**
 * Куда ляжет запись каталога, выбранная для строки union-типа (issue #747).
 *
 * <ul>
 *   <li><b>self</b> — запись типизирована самим union'ом (или потомком): кладётся голая ссылка, как
 *       у обычного составного поля. Путь не гипотетический: «Вынести в общие данные» из
 *       union-массива создаёт запись именно union-типа (issue #663);</li>
 *   <li><b>variant</b> — запись подходит одному варианту (или нескольким, и тогда берётся
 *       БЛИЖАЙШИЙ по цепочке наследования): кладётся <code>{ключВарианта: ссылка}</code>;</li>
 *   <li><b>ambiguous</b> — вариант по типу не определить, спрашиваем человека;</li>
 *   <li><b>none</b> — запись не подходит ничему, кандидата не показываем.</li>
 * </ul>
 *
 * <p><b>Почему «ambiguous» существует, а не сведён к «взять первый».</b> Наследование одиночное,
 * поэтому все подходящие варианты лежат на ОДНОЙ цепочке предков записи и различаются глубиной;
 * равенство дистанций возможно исключительно тогда, когда два варианта объявлены на один и тот же
 * <code>typeId</code>. Это не порча данных, а осмысленная конфигурация: у «Кабельной линии» варианты
 * «КабельнаяЛинияЭО» и «КабельнаяЛинияЭОН» смотрят на один тип «Основная кабельная линия» —
 * освещение внутреннее и наружное, форма данных одна, различие живёт только в ключе варианта.
 * Выбрать за пользователя тут нечем, а спрятать кандидата значило бы молча отнять у него запись,
 * которую он видит в каталоге.</p>
 *
 * <p><b>Почему только одиночные варианты.</b> <code>array</code> и <code>doc-array</code> объявляют
 * СПИСОК, а пикер отдаёт одну запись. Завернуть её в одноэлементный массив логично ровно до второго
 * выбора, который сделает вторую строку вместо добавления в первую. Хуже того, отказ был бы тихим:
 * <code>ArrayFieldEditor</code> берёт значение как <code>Array.isArray(v) ? v : []</code>, то есть
 * одиночная ссылка исчезла бы из редактора без следа, а в шаблон уехал объект вместо перечисления.</p>
 *
 * <p>Ту же меру близости считает сервер при материализации наборов (issue #716); правила там задаёт
 * админ явно, здесь они выводятся из <code>typeId</code> варианта, но исходы совпадают.</p>
 */
export type UnionPlacement =
  | { kind: 'self' }
  | { kind: 'variant'; variantKey: string }
  | { kind: 'ambiguous'; variantKeys: string[] }
  | { kind: 'none' };

export function placeInUnion(
  entryTypeId: string, unionType: DocumentType, allDocTypes: DocumentType[],
): UnionPlacement {
  if (inheritanceDistance(entryTypeId, unionType.id, allDocTypes) !== null) return { kind: 'self' };

  const matches = resolveEffectiveFields(unionType, allDocTypes)
    .filter(f => (f.type === 'complex' || f.type === 'doc-ref') && !!f.typeId)
    .map(f => ({ key: f.key, distance: inheritanceDistance(entryTypeId, f.typeId!, allDocTypes) }))
    .filter((m): m is { key: string; distance: number } => m.distance !== null);

  if (matches.length === 0) return { kind: 'none' };

  const nearest = Math.min(...matches.map(m => m.distance));
  const winners = matches.filter(m => m.distance === nearest).map(m => m.key);
  return winners.length === 1
    ? { kind: 'variant', variantKey: winners[0] }
    : { kind: 'ambiguous', variantKeys: winners };
}

/**
 * True for fields that map 1:1 to a single scalar value — i.e. can be bound to one
 * dataset column. Excludes containers (array/complex) and document references.
 */
export function isScalarField(f: SchemaField): boolean {
  return f.type !== 'array' && f.type !== 'complex' && f.type !== 'doc-ref' && f.type !== 'doc-array';
}

/** Returns a record pre-filled with defaultValue for each field that has one. */
export function getDefaultValues(fields: SchemaField[]): Record<string, unknown> {
  const result: Record<string, unknown> = {};
  for (const f of fields) {
    if (f.defaultValue !== undefined) result[f.key] = f.defaultValue;
  }
  return result;
}

/** Checks whether a field value is considered "missing" for validation purposes. */
export function isFieldMissing(field: SchemaField, value: unknown): boolean {
  if (!field.required) return false;
  if (field.type === 'boolean') return false;
  if (field.type === 'complex') {
    return value == null || (typeof value === 'object' && Object.keys(value as object).length === 0);
  }
  return value == null || String(value).trim() === '';
}

/**
 * Тип-МАТЕРИАЛ — тот, что может нести документ качества (issue #569).
 *
 * Тэг identity носят и другие справочные типы: единица измерения опознаётся своим наименованием,
 * организация — сокращённым названием. Пока поля идентичности собирались по ВСЕМ составным типам,
 * строка набора данных с колонкой «ЕдиницаИзмерения» получала ключ «шт» — и 151 материал реестра
 * схлопывался в четыре единицы измерения.
 */
export function isMaterialType(t: DocumentType, allDocTypes: DocumentType[]): boolean {
  return resolveEffectiveFields(t, allDocTypes)
    .some(f => hasTag(f.tags, FUNCTIONAL_TAG.materialQualityDocLink));
}

/**
 * Ключи полей идентичности ОДНОГО типа — в порядке компонентов его составного ключа (issue #663).
 *
 * ЗЕРКАЛО серверного `SchemaTags.OrderedKeysWithTag(type, all, 'identity')`: сортировка по параметру
 * тэга («identity:1» перед «identity:2»), поля без номера — следом, в порядке эффективной схемы.
 * Порядок здесь виден человеку — из этих значений складывается предлагаемое имя выносимой записи
 * (issue #663), — поэтому расходиться с серверным ему незачем.
 *
 * От {@link materialIdentityKeys} отличается охватом: тот сквозной по ВСЕМ материальным типам и
 * задаёт ключ связки «материал → документ качества», этот описывает один тип. Свести в одну функцию
 * нельзя: сквозной порядок перемежает типы (`order → typeIndex → fieldIndex`), и для одного типа
 * такой сортировки просто нет.
 */
export function identityFieldKeys(docType: DocumentType, allDocTypes: DocumentType[]): string[] {
  const found: { key: string; order: number; index: number }[] = [];
  resolveEffectiveFields(docType, allDocTypes).forEach((f, index) => {
    const entry = findTagEntry(f.tags, FUNCTIONAL_TAG.identity);
    if (entry === undefined) return;
    // Поле без номера идёт ПОСЛЕ нумерованных — так существующие схемы работают без правки.
    found.push({ key: f.key, order: tagOrder(entry) ?? Number.MAX_SAFE_INTEGER, index });
  });
  found.sort((a, b) => a.order - b.order || a.index - b.index);
  return Array.from(new Set(found.map(f => f.key)));
}

/**
 * Ключи полей идентичности у типов-материалов — по тэгам, без хардкода имён полей, В ПОРЯДКЕ
 * КОМПОНЕНТОВ составного ключа (issue #583): сначала номер из параметра тэга («identity:1»), затем
 * поля без номера.
 *
 * ЗЕРКАЛО серверного `MaterialIdentity.KeysOf` — вплоть до правил сортировки. Номер сравнивается
 * сквозь типы (пользователь видит его как сквозную нумерацию компонентов), типы — построковым
 * сравнением идентификаторов (сервер сортирует их так же, ordinal, ровно ради этого совпадения).
 * Разойдись порядок — ключ связки, заведённой здесь, не совпал бы с ключом на генерации, и связка
 * молча никогда бы не срабатывала.
 */
export function materialIdentityKeys(allDocTypes: DocumentType[]): string[] {
  const found: { key: string; order: number; typeIndex: number; fieldIndex: number }[] = [];
  // Материал — тот, кто может нести документ качества, и только это (issue #569). Вид типа не
  // проверяем: сервер его тоже не проверяет, а список типов обязан совпадать до последнего — из
  // него складывается порядок компонентов ключа.
  const materialTypes = allDocTypes
    .filter(t => isMaterialType(t, allDocTypes))
    .sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));

  materialTypes.forEach((t, typeIndex) => {
    resolveEffectiveFields(t, allDocTypes).forEach((f, fieldIndex) => {
      const entry = findTagEntry(f.tags, FUNCTIONAL_TAG.identity);
      if (entry === undefined) return;
      // Поле без номера идёт ПОСЛЕ нумерованных — так существующие схемы работают без правки.
      found.push({ key: f.key, order: tagOrder(entry) ?? Number.MAX_SAFE_INTEGER, typeIndex, fieldIndex });
    });
  });

  found.sort((a, b) => a.order - b.order || a.typeIndex - b.typeIndex || a.fieldIndex - b.fieldIndex);
  return Array.from(new Set(found.map(f => f.key)));
}

/**
 * Все строки материалов, лежащие в реквизитах, — по схеме и по всей глубине вложенности (issue #648).
 *
 * Раньше вкладка собирала материалы только из массивов ВЕРХНЕГО уровня, и inline-ветка union
 * «массив ИЛИ ссылка на реестр» (#320) выпадала: в АОСР материалы лежат в `Материалы.Материалы`,
 * то есть внутри составной обёртки. Закладка оставалась пустой ровно там, где материалы внесли
 * руками, — в мелком акте без отдельного реестра.
 *
 * Материалом считается строка массива материального типа (см. {@link isMaterialType}); одиночное
 * составное поле материального типа — тоже строка, случай «в документе один материал». Ссылки
 * (`doc-ref`/`doc-array`) не разворачиваем: материалы чужой записи приходят на вкладку набором
 * данных, а не отсюда.
 */
export function collectMaterialRows(
  docType: DocumentType, allDocTypes: DocumentType[], requisites: Record<string, unknown>,
): Record<string, unknown>[] {
  const rows: Record<string, unknown>[] = [];

  function walk(type: DocumentType, values: Record<string, unknown>, depth: number) {
    if (depth > MAX_COMPOSITE_DEPTH) return;
    for (const f of resolveEffectiveFields(type, allDocTypes)) {
      if ((f.type !== 'array' && f.type !== 'complex') || !f.typeId) continue;
      const ct = allDocTypes.find(t => t.id === f.typeId);
      if (!ct) continue;
      const value = values[f.key];
      const material = isMaterialType(ct, allDocTypes);
      const items = f.type === 'array'
        ? (Array.isArray(value) ? value : [])
        : [value];
      for (const item of items) {
        if (!item || typeof item !== 'object' || Array.isArray(item)) continue;
        const record = item as Record<string, unknown>;
        if ('$ref' in record) continue; // ссылка на запись каталога — не инлайн-материал
        if (material) rows.push(record);
        else walk(ct, record, depth + 1); // обёртка (в т.ч. union) — материалы могут быть глубже
      }
    }
  }

  walk(docType, requisites, 0);
  return rows;
}
