// ─── Common Data Catalog ──────────────────────────────────────────────────────

export type CatalogScope = 'Set' | 'Section' | 'Construction' | 'System';

export const SCOPE_PRIORITY: Record<CatalogScope, number> = {
  Set: 1, Section: 2, Construction: 3, System: 5,
};

export const SCOPE_LABELS: Record<CatalogScope, string> = {
  Set: 'Комплект', Section: 'Раздел', Construction: 'Стройка', System: 'Система',
};

export interface CommonDataEntry {
  id: string;
  displayName: string;
  compositeTypeId: string;
  data: Record<string, unknown>;
  scope: CatalogScope;
  scopeId: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface CommonDataEntryWithScope extends CommonDataEntry {
  priority: number;
}

/** Ссылка на объект каталога общих данных, поле другого документа или весь DocumentInstance. */
export interface FieldRef {
  readonly $ref: 'catalog' | 'document' | 'instance';
  entryId?: string;      // catalog
  instanceId?: string;   // document | instance
  fieldKey?: string;     // document — ключ поля в реквизитах другого документа
  displayName: string;
  scope?: CatalogScope;
}

export function isFieldRef(val: unknown): val is FieldRef {
  return val != null && typeof val === 'object'
    && '$ref' in (val as Record<string, unknown>)
    && ['catalog', 'document', 'instance'].includes((val as FieldRef).$ref);
}

export function isInstanceRef(val: unknown): val is FieldRef & { $ref: 'instance' } {
  return isFieldRef(val) && (val as FieldRef).$ref === 'instance';
}

// ─── Catalog Entity ───────────────────────────────────────────────────────────

export interface CatalogEntity {
  id: string;
  entityType: string;
  displayName: string;
  data: Record<string, unknown>;
  ownerId: string | null;
  createdAt: string;
  updatedAt: string;
}

export type DocumentTypeKind = 'Document' | 'Composite';

export interface DocumentType {
  id: string;
  name: string;
  code: string;
  kind: DocumentTypeKind;
  isAbstract: boolean;
  parentId: string | null;
  schema: Record<string, unknown>;
  pluginBindings: Record<string, unknown>;
  group: string | null;
  createdAt: string;
  updatedAt: string;
}

/** Объявленный параметр шаблона (значение по умолчанию + переопределение на документе). */
export interface TemplateParam {
  name: string;
  label: string;
  type: 'string' | 'number' | 'boolean';
  default: string | number | boolean | null;
}

export interface Template {
  id: string;
  documentTypeId: string;
  name: string;
  content: string;
  /** JSON-текст массива TemplateParam[] (jsonb-строка) или null. Парсить на клиенте. */
  parameters: string | null;
  version: number;
  isActive: boolean;
  isDefault: boolean;
  pageSize: string;
  pageOrientation: string;
  marginTop: number;
  marginRight: number;
  marginBottom: number;
  marginLeft: number;
  createdAt: string;
  updatedAt: string;
}

export interface Construction {
  id: string;
  name: string;
  createdByUserId: string;
  createdAt: string;
  updatedAt: string;
  sections: Section[];
}

export interface Section {
  id: string;
  name: string;
  constructionId: string;
  createdAt: string;
  updatedAt: string;
  documentSets: DocumentSet[];
}

export interface DocumentSet {
  id: string;
  name: string;
  sectionId: string;
  createdAt: string;
  updatedAt: string;
  instances: DocumentInstance[];
}

export interface DocumentInstance {
  id: string;
  documentSetId: string;
  documentTypeId: string;
  name: string | null;
  templateId: string | null;
  /** JSON-текст массива Guid выбранных шаблонов для мульти-генерации (jsonb-строка) или null. */
  templateIds: string | null;
  requisites: Record<string, unknown>;
  pluginData: Record<string, unknown>;
  /** JSON-текст объекта {имя:значение} переопределений параметров шаблона (jsonb-строка) или null. */
  templateParams: string | null;
  status: 'Draft' | 'Generating' | 'Generated' | 'Failed';
  generatedFiles: GeneratedFile[];
  /** Порядок документа в комплекте (для сборки в один файл). */
  sortOrder: number;
  createdAt: string;
  updatedAt: string;
}

export interface DocumentSearchResult {
  instanceId: string;
  name: string | null;
  typeName: string;
  status: DocumentInstance['status'];
  hasPdf: boolean;
  constructionId: string;
  constructionName: string;
  sectionName: string;
  setId: string;
  setName: string;
}

export interface GeneratedFile {
  id: string;
  documentInstanceId: string;
  format: 'Pdf';
  blobPath: string;
  /** Шаблон, которым сгенерирован файл (мульти-шаблоны — один файл на шаблон). Null — legacy/дефолт. */
  templateId: string | null;
}

// ─── Primitive Types ─────────────────────────────────────────────────────────

export interface FieldConstraints {
  pattern?: string;
  patternMessage?: string;
  minLength?: number;
  maxLength?: number;
  min?: number;
  max?: number;
  integer?: boolean;
  minDate?: string;
  maxDate?: string;
}

export interface PrimitiveTypeDef {
  id: string;
  name: string;
  code: string;
  baseType: 'string' | 'number' | 'date';
  description?: string;
  constraints: FieldConstraints;
  /** Коды функциональных тэгов, применимых к полям этого типа. */
  allowedTags: string[];
  group: string | null;
  createdAt: string;
  updatedAt: string;
}

// ─── Backup / Restore ─────────────────────────────────────────────────────────

export interface BackupManifest {
  schemaVersion: number;
  appVersion: string;
  createdAt: string;
  documentTypes: unknown[];
  templates: unknown[];
  catalogEntities: unknown[];
  commonDataEntries: unknown[];
  primitiveTypes?: unknown[];
}

export interface RestoreReport {
  success: boolean;
  conversionNotice: string | null;
  warnings: string[];
  documentTypesCreated: number;
  documentTypesUpdated: number;
  templatesCreated: number;
  templatesUpdated: number;
  catalogEntitiesCreated: number;
  catalogEntitiesUpdated: number;
  commonDataEntriesCreated: number;
  commonDataEntriesUpdated: number;
  primitiveTypesCreated: number;
  primitiveTypesUpdated: number;
}

// ─── DataSets ─────────────────────────────────────────────────────────────────

export type DataSetFormat = 'Csv' | 'Xlsx' | 'Xls' | 'Xml' | 'Json' | 'Zip' | 'Pdf';

export const DATA_SET_FORMAT_LABELS: Record<DataSetFormat, string> = {
  Csv: 'CSV / TXT',
  Xlsx: 'Excel (.xlsx)',
  Xls: 'Excel (.xls)',
  Xml: 'XML',
  Json: 'JSON',
  Zip: 'ZIP-архив',
  Pdf: 'PDF',
};

/** Явная относительная колонка XML-источника (см. XPathBuilder). */
export interface ColumnExprDef {
  name: string;
  expr: string;
}

export interface DataSetSource {
  id: string;
  fileId: string;
  name: string;
  sheetOrPath: string;
  /** Только для XML: JSON-массив ColumnExprDef[]. Null — авто-определение колонок (легаси). */
  columnExpressions: string | null;
  cachedSchema: string; // JSON of {name, sampleValues}[]
  cachedRowCount: number;
  /** Обработка (Filter/Transformation/Sort) — своя, независимая. Применение шаблона обработки
   *  копирует его значения сюда единожды (не живая ссылка). */
  rowFilter: RowFilterDef | null;
  computedColumns: ComputedColumn[] | null;
  sortSpec: SortSpec | null;
  /** Коды функциональных тэгов источника (scope Dataset) — только для PDF. */
  tags: string[] | null;
  /** Распознанный PDF-источник устарел: файл заменён после распознавания — нужно перераспознать. */
  recognitionStale: boolean;
  /** Материализация (issue #19): ID типа (составной/документ), в сущности которого разворачиваются строки. Null — не настроена. */
  materializeTypeId: string | null;
  /** Маппинг колонок → поля материализуемого типа: {ключПоля: "Колонка"|"@@ref:…"|"@@file:…"}. */
  materializeMapping: Record<string, string> | null;
}

/** Группировка страниц ГОСТ-профиля — для редактора ручной корректировки разбиения. */
export type GostGroupKind = 'Document' | 'Cover' | 'TitlePage';

export interface GostGroupingGroup {
  kind: GostGroupKind;
  /** Шифр — только для документа; для обложки/титула null. */
  code: string | null;
  /** Наименование — только для документа; для обложки/титула null. */
  name: string | null;
  pageIndices: number[];
  /** Функциональные тэги документа (тип таблицы — спецификация/кабельный журнал). */
  tags?: string[] | null;
}

export interface GostGrouping {
  groups: GostGroupingGroup[];
  manuallyEdited: boolean;
  /** Общее число страниц исходного PDF — включая не вошедшие ни в одну группу. */
  pageCount: number;
}

export interface DataSetFile {
  id: string;
  name: string;
  format: DataSetFormat;
  scope: CatalogScope;
  scopeId: string | null;
  sources: DataSetSource[];
  createdAt: string;
  /** Профиль препроцессинга PDF (issue #38): 'gost-titleblock' | 'invoice' | null (ещё не выбран). */
  preprocessingProfile?: string | null;
}

/** Привязка набора данных к документу или записи каталога — только Mapping.
 * Filter/Transformation/Sort — на DataSetSource. Владелец — ровно одно из instanceId/commonDataEntryId. */
export interface DataSetBinding {
  id: string;
  instanceId: string | null;
  commonDataEntryId: string | null;
  sourceId: string;
  targetFieldKey: string | null;
  mapping: Record<string, string>;
  source?: DataSetSource & { file?: Pick<DataSetFile, 'id' | 'name' | 'format' | 'scope' | 'scopeId'> };
}

/** Владелец привязки к набору данных — ровно одно из полей задано. */
export interface DataSetBindingOwner {
  instanceId?: string;
  commonDataEntryId?: string;
}

export interface DataSetPreview {
  columns: string[];
  rows: (string | null)[][];
  totalRows: number;
}

// ─── Dataset filter / transform types ─────────────────────────────────────────

export type FilterOp =
  | 'eq' | 'neq'
  | 'contains' | 'not_contains'
  | 'starts_with' | 'ends_with'
  | 'gt' | 'lt' | 'gte' | 'lte'
  | 'is_empty' | 'is_not_empty';

export const FILTER_OP_LABELS: Record<FilterOp, string> = {
  eq: '= равно',
  neq: '≠ не равно',
  contains: 'содержит',
  not_contains: 'не содержит',
  starts_with: 'начинается с',
  ends_with: 'заканчивается на',
  gt: '> больше',
  lt: '< меньше',
  gte: '>= больше или равно',
  lte: '<= меньше или равно',
  is_empty: 'пусто',
  is_not_empty: 'не пусто',
};

export const FILTER_OPS_NO_VALUE: FilterOp[] = ['is_empty', 'is_not_empty'];

/** Leaf node: one comparison condition. */
export interface FilterCondition {
  type: 'condition';
  column: string;
  op: FilterOp;
  value?: string;
}

/** Branch node: logical group of child nodes (conditions or sub-groups). */
export interface FilterGroup {
  type: 'group';
  logic: 'and' | 'or';
  children: FilterNode[];
}

export type FilterNode = FilterCondition | FilterGroup;

/** Root of the filter tree — always a FilterGroup. */
export type RowFilterDef = FilterGroup;

export interface ComputedColumn {
  alias: string;
  expr: string;
}

/** Одна ступень сортировки — по колонке (в т.ч. вычисляемой). */
export interface SortColumn {
  column: string;
  direction: 'asc' | 'desc';
}

export type SortSpec = SortColumn[];

/** Шаблон маппинга (для типа документа). Filter/Transformation/Sort — см. DataSetProcessingTemplate. */
export interface DataSetBindingTemplate {
  id: string;
  documentTypeId: string;
  name: string;
  targetFieldKey: string | null;
  columnMappings: Record<string, string>;
  sortOrder: number;
  createdAt: string;
  updatedAt: string;
}

/** Переиспользуемый рецепт обработки (Filter/Transformation/Sort) — не привязан к типу документа. */
export interface DataSetProcessingTemplate {
  id: string;
  name: string;
  /** Extraction (опционально): row-selector — формат-зависимый (XPath/JSONPath/имя листа). */
  sheetOrPath: string | null;
  /** JSON-массив ColumnExprDef[] (как на DataSetSource). */
  columnExpressions: string | null;
  rowFilter: RowFilterDef | null;
  computedColumns: ComputedColumn[] | null;
  sortSpec: SortSpec | null;
  createdAt: string;
  updatedAt: string;
}

export interface DataSetBindingPreviewResult {
  bindingId: string;
  sourceName: string;
  fileName: string;
  mode: 'scalar' | 'tabular' | 'error';
  targetFieldKey: string | null;
  totalRows: number;
  /** Значение ячейки — строка (обычный/ref-маппинг) или FileAttachment-объект (файловый маппинг). */
  data: Record<string, unknown> | Record<string, unknown>[];
  error: string | null;
}
