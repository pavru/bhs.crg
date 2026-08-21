import type { FilterGroup, FilterNode, DataSetBindingPreviewResult, DataOrigin, DataSetStaleReason } from './types';

/** A column descriptor cached on a DataSetSource. */
export interface DataSetColumn {
  name: string;
  sampleValues?: string[];
}

/** Safely parses the cached JSON schema of a DataSetSource into column descriptors. */
export function parseSourceColumns(cachedSchema: string | undefined | null): DataSetColumn[] {
  if (!cachedSchema) return [];
  try {
    const parsed = JSON.parse(cachedSchema);
    return Array.isArray(parsed) ? (parsed as DataSetColumn[]) : [];
  } catch {
    return [];
  }
}

/** Convenience: column names only. */
export function parseSourceColumnNames(cachedSchema: string | undefined | null): string[] {
  return parseSourceColumns(cachedSchema).map(c => c.name);
}

// ─── Reference (catalog) mapping ────────────────────────────────────────────
// Составное поле элемента может заполняться ссылкой на запись каталога: значение
// колонки ищется среди записей составного типа. Кодируется в значении маппинга
// строкой "@@ref:{json}". Формат разделяется с backend (DataSetMappingValue).

const REF_PREFIX = '@@ref:';

/**
 * Ссылочный маппинг «строка→объект каталога». Два user-facing варианта (issue #243, решение #183):
 *  • Name — по имени/алиасам значения одной колонки (`strategy:'Name'`, `column`);
 *  • Identity — по составному ключу identity-полей типа (`strategy:'Identity'`, `identityColumns`:
 *    identityПоле→колонка, по одной на каждое identity-поле).
 * Legacy `{column, match}` (непустой `match` = стратегия Field, произвольное поле) читается вечно, но
 * из UI больше не создаётся; при редактировании конвертируется в Name/Identity.
 */
export interface RefMapping {
  typeId: string;
  column?: string;
  /** Legacy: поле для матча (непустой = Field, пусто = Name). Новый формат его не пишет. */
  match?: string;
  strategy?: 'Name' | 'Identity';
  /** Identity: identityПоле→колонка файла. */
  identityColumns?: Record<string, string>;
}

export function isRefMappingValue(value: string | undefined | null): boolean {
  return typeof value === 'string' && value.startsWith(REF_PREFIX);
}

export function parseRefMapping(value: string | undefined | null): RefMapping | null {
  if (!isRefMappingValue(value)) return null;
  try {
    const p = JSON.parse((value as string).slice(REF_PREFIX.length)) as Partial<RefMapping>;
    const hasIdentity = !!p.identityColumns && Object.keys(p.identityColumns).length > 0;
    if (!p.typeId || (!p.column && !hasIdentity)) return null;
    return {
      typeId: p.typeId, column: p.column, match: p.match ?? '',
      strategy: p.strategy, identityColumns: p.identityColumns,
    };
  } catch {
    return null;
  }
}

/** Резолв по имени/алиасам одной колонки. */
export function buildRefMappingByName(typeId: string, column: string): string {
  return REF_PREFIX + JSON.stringify({ strategy: 'Name', column, typeId });
}

/** Резолв по составному identity-ключу: identityПоле→колонка (по одной на каждое identity-поле). */
export function buildRefMappingByIdentity(typeId: string, identityColumns: Record<string, string>): string {
  return REF_PREFIX + JSON.stringify({ strategy: 'Identity', identityColumns, typeId });
}

// ─── Файловый маппинг ───────────────────────────────────────────────────────
// Поле типа "file" заполняется вложением, синтезированным из колонок ТОЙ ЖЕ строки источника
// (в отличие от ref-маппинга — здесь нет поиска по каталогу). Кодируется строкой
// "@@file:{json}". Формат разделяется с backend (DataSetMappingValue.ResolveFileValue).

const FILE_PREFIX = '@@file:';

export interface FileMapping {
  /** Колонка с путём к blob'у (напр. "ФайлПуть"). */
  column: string;
  /** Необязательная колонка с размером в байтах (напр. "РазмерБайт"); пусто — size=0. */
  sizeColumn: string;
}

export function isFileMappingValue(value: string | undefined | null): boolean {
  return typeof value === 'string' && value.startsWith(FILE_PREFIX);
}

export function parseFileMapping(value: string | undefined | null): FileMapping | null {
  if (!isFileMappingValue(value)) return null;
  try {
    const parsed = JSON.parse((value as string).slice(FILE_PREFIX.length)) as Partial<FileMapping>;
    if (!parsed.column) return null;
    return { column: parsed.column, sizeColumn: parsed.sizeColumn ?? '' };
  } catch {
    return null;
  }
}

export function buildFileMapping(m: FileMapping): string {
  return FILE_PREFIX + JSON.stringify({ column: m.column, sizeColumn: m.sizeColumn || undefined });
}

// ─── Inline-маппинг составного поля (issue #374) ──────────────────────────────
// Составное поле собирается КАК ВСТРОЕННЫЙ ОБЪЕКТ из колонок той же строки (без поиска в каталоге).
// fields: под-поле → токен (та же грамматика: колонка / @@ref / вложенный @@inline). Кодируется
// "@@inline:{json}". Формат разделяется с backend (DataSetMappingValue.ParseInline).

const INLINE_PREFIX = '@@inline:';

export interface InlineMapping {
  typeId: string;
  fields: Record<string, string>;
}

export function isInlineMappingValue(value: string | undefined | null): boolean {
  return typeof value === 'string' && value.startsWith(INLINE_PREFIX);
}

export function parseInlineMapping(value: string | undefined | null): InlineMapping | null {
  if (!isInlineMappingValue(value)) return null;
  try {
    const p = JSON.parse((value as string).slice(INLINE_PREFIX.length)) as Partial<InlineMapping>;
    if (!p.typeId || !p.fields || Object.keys(p.fields).length === 0) return null;
    return { typeId: p.typeId, fields: p.fields };
  } catch {
    return null;
  }
}

export function buildInlineMapping(typeId: string, fields: Record<string, string>): string {
  return INLINE_PREFIX + JSON.stringify({ typeId, fields });
}

// ─── Слияние результата preview биндингов в значения формы ─────────────────────
// Клиентское зеркало серверного CommonDataBindingMerge (Application/Documents) — те же правила:
// пустое скалярное значение не затирает существующее, табличное поле пишется целиком (даже []).

export function mergeBindingPreviewsIntoValues(
  values: Record<string, unknown>,
  previews: DataSetBindingPreviewResult[],
): Record<string, unknown> {
  const next = { ...values };
  for (const p of previews) {
    if (p.error) continue;
    if (p.mode === 'scalar') {
      const data = p.data as Record<string, unknown>;
      for (const [key, value] of Object.entries(data)) {
        if (value === null || value === '') continue;
        next[key] = value;
      }
    } else if (p.mode === 'tabular' && p.targetFieldKey) {
      next[p.targetFieldKey] = p.data;
    }
  }
  return next;
}

/** Ключи полей, покрытых биндингами: скалярные (top-level) отдельно от табличных (array-полей). */
export function computeBoundFieldKeys(
  bindings: { targetFieldKey: string | null; mapping: Record<string, string> }[],
): { scalarKeys: Set<string>; arrayKeys: Set<string> } {
  const scalarKeys = new Set<string>();
  const arrayKeys = new Set<string>();
  for (const b of bindings) {
    if (b.targetFieldKey === null) {
      for (const key of Object.keys(b.mapping)) scalarKeys.add(key);
    } else {
      arrayKeys.add(b.targetFieldKey);
    }
  }
  return { scalarKeys, arrayKeys };
}

/**
 * Почему данные источника устарели — ОДНОЙ фразой-констатацией, без глагола (issue #815).
 *
 * Действие дописывает место показа, а не эта функция: путь к «Перераспознать» есть не отовсюду, и
 * подсказка, требующая невыполнимого, обесценивается ровно так же, как та, что горит всегда.
 * Причину пишет сервер — своего правила у клиента нет, иначе тексты разъедутся с событиями.
 */
export function staleReasonText(reason: DataSetStaleReason | null | undefined): string {
  switch (reason) {
    case 'FileReplaced':
      return 'Файл набора заменён после распознавания — значения относятся к прежнему содержимому';
    case 'NotParsedAgainstNewFile':
      return 'Источник не разобрался против нового файла — лист или путь исчез, значения остались от прежнего';
    case 'TableBoundariesChanged':
      return 'Состав страниц документа изменился после распознавания таблицы — строки относятся к прежним границам';
    case 'ProfileChanged':
      return 'Профиль распознавания изменён после распознавания — значения прочитаны прежними параметрами';
    default:
      // Причина с сервера незнакома (новее клиента) — сказать «устарело» всё равно честнее, чем
      // промолчать: признак сюда попал не сам по себе.
      return 'Данные источника устарели';
  }
}

interface FieldKeyBinding {
  targetFieldKey: string | null;
  mapping: Record<string, string>;
  source?: {
    origin?: DataOrigin;
    recognitionStale?: boolean;
    staleReason?: DataSetStaleReason | null;
    materializeMapping?: Record<string, string> | null;
  };
}

/**
 * Ключи полей, которые заполняет привязка, удовлетворяющая условию.
 *
 * Разбор привязки на ключи полей — ОДИН на все признаки (issue #815): происхождение и устаревание
 * спрашивают о разном, но отвечают об одних и тех же полях. Своя копия у каждого признака уже
 * однажды разъехалась по правилу fallback'а — и часть полей осталась read-only без объяснения.
 */
/**
 * Ключи полей ОДНОЙ привязки — единственное место, где привязка разбирается на поля.
 *
 * Табличная привязка названа целевым полем и маппингом поля НЕ покрывает: у неё маппинг описывает
 * колонки строк, а не реквизиты документа. Смешай эти два случая — и поле получило бы признак от
 * источника, который его не заполняет.
 */
function bindingFieldKeys(b: FieldKeyBinding): string[] {
  if (b.targetFieldKey) return [b.targetFieldKey];
  // Эффективный маппинг — собственный, а при пустом берётся с материализации источника: ровно так
  // же считаются поля, которые форма делает read-only. Разойдись эти два правила — часть полей
  // стала бы нередактируемой без объяснения, откуда взялось значение.
  return Object.keys(Object.keys(b.mapping).length > 0 ? b.mapping : (b.source?.materializeMapping ?? {}));
}

function computeFieldKeysWhere(
  bindings: FieldKeyBinding[],
  matches: (b: FieldKeyBinding) => boolean,
): Set<string> {
  const keys = new Set<string>();
  for (const b of bindings) {
    if (!matches(b)) continue;
    for (const key of bindingFieldKeys(b)) keys.add(key);
  }
  return keys;
}

/**
 * Происхождение значений по ключу поля: к какому источнику поле привязано, оттуда и признак
 * (issue про точку потребления). Рядом с `computeBoundFieldKeys` и по тем же правилам разбора
 * привязки — скалярные ключи лежат в маппинге, табличное поле названо в `targetFieldKey`.
 *
 * Возвращаются ТОЛЬКО распознанные: `Parsed` и `System` в интерфейсе ничего не меняют — у них нет
 * действия для читателя, а метка без действия становится фоном, который перестают замечать.
 */
export function computeRecognizedFieldKeys(bindings: FieldKeyBinding[]): Set<string> {
  return computeFieldKeysWhere(bindings, b => b.source?.origin === 'Recognized');
}

/**
 * Поля, чей источник УСТАРЕЛ: данные разошлись с файлом, из которого их читали (issue #815).
 *
 * Считается по всем источникам, а не только распознанным: устареть может и парсерный источник,
 * не разобравшийся против нового файла, — и человеку у поля это так же важно, как распознавание.
 */
export function computeStaleFieldKeys(bindings: FieldKeyBinding[]): Set<string> {
  return computeFieldKeysWhere(bindings, b => b.source?.recognitionStale === true);
}

/**
 * Поле → причина, по которой его источник устарел. Тем же разбором привязки, что и множества выше:
 * искать причину отдельным условием уже приводило к тому, что полю показывали причину чужого
 * источника — табличная привязка совпадала и по целевому полю, и по ключам своего маппинга.
 *
 * Поле заполняет одна привязка; если их всё же две, берётся первая — спорить о причине бессмысленно,
 * когда сама настройка противоречива.
 */
export function computeStaleReasonByField(
  bindings: FieldKeyBinding[],
): Map<string, DataSetStaleReason | null | undefined> {
  const byField = new Map<string, DataSetStaleReason | null | undefined>();
  for (const b of bindings) {
    if (b.source?.recognitionStale !== true) continue;
    for (const key of bindingFieldKeys(b)) {
      if (!byField.has(key)) byField.set(key, b.source?.staleReason);
    }
  }
  return byField;
}

/** Recursively counts non-empty conditions in a filter tree. */
export function countFilterConditions(node: FilterNode | null | undefined): number {
  if (!node) return 0;
  if (node.type === 'condition') return node.column ? 1 : 0;
  return (node as FilterGroup).children.reduce((sum, c) => sum + countFilterConditions(c), 0);
}

/**
 * Prunes empty conditions (blank column) and empty groups from a filter tree.
 * Returns null when nothing meaningful remains.
 */
export function cleanFilterNode(node: FilterNode): FilterNode | null {
  if (node.type === 'condition') {
    return node.column.trim() ? node : null;
  }
  const validChildren = node.children
    .map(cleanFilterNode)
    .filter((c): c is FilterNode => c !== null);
  if (validChildren.length === 0) return null;
  return { ...node, children: validChildren };
}

/**
 * Ближайшее свободное имя источника внутри набора — «База» / «База — 2» / «База — 3» (issue #717).
 *
 * Нужно диалогу имени: второй источник на ту же консолидацию заводят именно потому, что первый уже
 * есть, и встречать пользователя занятым именем незачем. Повторяет правило сервера (SourceNaming) —
 * там оно остаётся как страховка для вызовов без имени, здесь работает на предзаполнение.
 */
export function nextSourceName(existingNames: string[], name: string): string {
  const base = name.trim().replace(/\s+—\s+\d+$/, '');
  const taken = new Set(existingNames.map(n => n.trim().toLocaleLowerCase('ru')));
  const free = (candidate: string) => !taken.has(candidate.toLocaleLowerCase('ru'));
  if (free(base)) return base;
  let n = 2;
  while (!free(`${base} — ${n}`)) n++;
  return `${base} — ${n}`;
}
