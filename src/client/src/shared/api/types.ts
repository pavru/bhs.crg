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
  /** Альтернативные имена (issue #74) — для поиска записи при связывании с источниками данных. */
  aliases: string[];
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
  /** issue #89: объект этого типа может быть ролью/прокси — ссылаться (_baseRef) на реальный объект того же типа. */
  allowsProxy: boolean;
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
  /** Необязательное примечание к версии (issue #360) — что за версия. */
  comment: string | null;
  version: number;
  isActive: boolean;
  isDefault: boolean;
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
  /** Стройка комплекта — только у запроса одного комплекта (GET /document-sets/{id}); нужна там, где
   *  привязка заводится на уровень выше комплекта (issue #587). */
  constructionId?: string;
  createdAt: string;
  updatedAt: string;
  /** Полный состав документов — только у запроса одного комплекта (GET /document-sets/{id}). */
  instances: DocumentInstance[];
  /** Число документов комплекта — в дереве стройки (GET /constructions[/{id}]); для навигации/каскадов. */
  documentCount?: number;
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

/** Точность ввода/отображения date-значения (issue #60). Хранение всегда полный ISO YYYY-MM-DD;
 *  точность управляет только раскладкой ввода и отображением. Отсутствие ≡ 'day' (обратная совм.). */
export type DatePrecision = 'day' | 'month' | 'year';

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
  /** Точность date-типа (issue #60). Отсутствует ≡ 'day' — полная дата ДД.ММ.ГГГГ. */
  datePrecision?: DatePrecision;
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

// ─── Enum Types (issue #59) ────────────────────────────────────────────────────

/** Один вариант перечисления: код (хранится в реквизитах) + отображаемое имя. */
export interface EnumOptionDef {
  code: string;
  label: string;
}

export interface EnumTypeDef {
  id: string;
  name: string;
  code: string;
  description?: string;
  values: EnumOptionDef[];
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

/** Вес копии, снятой прямо сейчас, и предел, на котором откажет восстановление (issue #711). */
export interface BackupSizeEstimate {
  totalBytes: number;
  manifestBytes: number;
  blobBytes: number;
  blobCount: number;
  /** Файлы, потерянные хранилищем: в копию они не попадут. */
  missingBlobCount: number;
  limitBytes: number;
  exceedsLimit: boolean;
}

/** Раздел копии и число записей в нём — «состав» в списке копий (issue #831). */
export interface BackupSectionCount {
  label: string;
  count: number;
}

/**
 * Копия, лежащая в каталоге на сервере (issue #831).
 *
 * `problem` — не техническая деталь, а единственный честный ответ про файл без паспорта: копию,
 * снятую старой версией, восстановить можно, чужой zip — нельзя, и спрятать оба было бы хуже всего.
 */
export interface BackupFileInfo {
  fileName: string;
  sizeBytes: number;
  createdAt: string;
  appVersion: string | null;
  schemaVersion: number | null;
  blobCount: number | null;
  sections: BackupSectionCount[] | null;
  problem: string | null;
}

/** Расписание копирования — то, что задал администратор (issue #832). */
export interface BackupScheduleSettings {
  enabled: boolean;
  /** «ЧЧ:ММ» по часам сервера. */
  timeOfDay: string;
  /** Сколько ПЛАНОВЫХ копий хранить; ручных и принесённых уборка не касается. */
  keepCount: number;
}

/** Расписание вместе со следом службы: когда снимали, чем кончилось, идёт ли прямо сейчас. */
export interface BackupScheduleStatus extends BackupScheduleSettings {
  lastRunAt: string | null;
  lastSuccessAt: string | null;
  lastFileName: string | null;
  lastError: string | null;
  lastErrorAt: string | null;
  running: boolean;
}

export interface BackupFilesResponse {
  files: BackupFileInfo[];
  /** Предел числа копий: достигнут — новая не создаётся, пока не удалят старые. */
  keepCount: number;
  /** Каталог на сервере: по этому пути копию кладут и забирают в обход браузера. */
  directory: string;
  schedule: BackupScheduleStatus;
  /**
   * Копии, снятые расписанием на ЭТОЙ установке, — уборка трогает только их. Признак хранится у
   * установки, а не внутри копии: иначе принесённая с другого сервера копия считалась бы плановой
   * и здесь, попадала под уборку и однажды исчезла бы ночью.
   */
  scheduledFiles: string[];
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
  enumTypesCreated?: number;
  enumTypesUpdated?: number;
  templateAssetsCreated?: number;
  templateAssetsUpdated?: number;
  typstUserLibRestored?: boolean;
  recognitionProfilesCreated?: number;
  recognitionProfilesUpdated?: number;
  dataSetBindingTemplatesCreated?: number;
  dataSetBindingTemplatesUpdated?: number;
  reconciliationAliasesCreated?: number;
  reconciliationAliasesUpdated?: number;
  dataSetProcessingTemplatesCreated?: number;
  dataSetProcessingTemplatesUpdated?: number;
  qualityDocumentsCreated?: number;
  qualityDocumentsUpdated?: number;
}

// ─── DataSets ─────────────────────────────────────────────────────────────────

/** 'System' — набор без файла: строки консолидирует система из своих же данных (issue #580). */
export type DataSetFormat = 'Csv' | 'Xlsx' | 'Xls' | 'Xml' | 'Json' | 'Zip' | 'Pdf' | 'System';

export const DATA_SET_FORMAT_LABELS: Record<DataSetFormat, string> = {
  Csv: 'CSV / TXT',
  Xlsx: 'Excel (.xlsx)',
  Xls: 'Excel (.xls)',
  Xml: 'XML',
  Json: 'JSON',
  Zip: 'ZIP-архив',
  Pdf: 'PDF',
  System: 'Данные системы',
};

/** Явная относительная колонка XML-источника (см. XPathBuilder). */
export interface ColumnExprDef {
  name: string;
  expr: string;
}

/**
 * Почему данные источника устарели (issue #815). Хранится на источнике: устаревание — это событие
 * (файл заменили, границы сдвинули, профиль сменили), а не свойство текущих данных, — задним числом
 * его не вычислить. Своей копии правила у клиента нет и быть не должно: она разошлась бы с серверной
 * ровно так, как до этого разошлись признак на файле и признак на источнике.
 */
export type DataSetStaleReason =
  | 'FileReplaced'
  | 'NotParsedAgainstNewFile'
  | 'TableBoundariesChanged'
  | 'ProfileChanged';

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
  /** Данные источника разошлись со своим происхождением — нужно перераспознать. */
  recognitionStale: boolean;
  /** Почему разошлись; null/undefined — не разошлись. Текст пишет клиент: у поля документа он без
   *  глагола (перераспознать оттуда нельзя), в списке источников — с действием. */
  staleReason?: DataSetStaleReason | null;
  /**
   * Откуда взялись значения источника. Считает СЕРВЕР (правило маркеров живёт в домене): формат
   * файла отвечает на другой вопрос — чем файл был, а не как из него получили значения, и своя
   * копия правила на клиенте разъехалась бы с серверной.
   */
  origin?: DataOrigin;
  /** Сколько привязок ссылается на источник (issue #417). null/undefined — не считали (ответ мутации). */
  bindingCount?: number | null;
  /** Живая оговорка системного источника (issue #626): например, реестр раздела не знает
   *  метаданных несобранных комплектов. null — сказать нечего. */
  warning?: string | null;
  /** Материализация (issue #19): ID типа (составной/документ), в сущности которого разворачиваются строки. Null — не настроена. */
  materializeTypeId: string | null;
  /** Маппинг колонок → поля материализуемого типа: {ключПоля: "Колонка"|"@@ref:…"|"@@file:…"}. */
  materializeMapping: Record<string, string> | null;
  /** Правило выбора варианта union'а по строке (issue #716); null — один вариант на все строки. */
  materializeDiscriminator?: MaterializeDiscriminator | null;
  /** Колонка с Ид существующего документа (issue #725): непустая = строка целиком становится
   *  ссылкой на документ (маппинг в этом режиме не задаётся). Null — сборка объекта из колонок. */
  materializeByIdColumn?: string | null;
}

/** Как читать колонку-признак: код типа документа либо идентификатор самого документа. */
export type DiscriminatorKind = 'docTypeCode' | 'docId';

/**
 * Правило выбора варианта union'а по строке источника (issue #716).
 *
 * `rules` — вариант → типы документов, которые к нему относятся. Вариант без правила ВЫКЛЮЧЕН:
 * строки его типов пропускаются. Это законная настройка, а не недоделка — реестр вполне может
 * собирать не все виды документов комплекта.
 */
export interface MaterializeDiscriminator {
  column: string;
  kind: DiscriminatorKind;
  rules: Record<string, string[]>;
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
  /**
   * Листы, по которым движок НЕ ОТВЕТИЛ (issue #803). Отдельно от пустых полей: лист без штампа даёт
   * пустые поля законно, и пометив его наравне с неотвеченным, интерфейс кричал бы на каждом
   * графическом листе альбома — а признак, который горит всегда, перестают замечать.
   */
  pagesWithoutAnswer?: number[] | null;
  /** Привязанный профиль распознавания (issue #410): снимает требование тэга — так распознаются
   *  произвольные таблицы, для которых функционального тэга не существует. */
  profileId?: string | null;
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
  /** Профили распознавания набора: {вид: id профиля} (issue #412); нет ключа — встроенный. */
  recognitionProfiles?: Record<string, string> | null;
}

/**
 * Происхождение значений источника: `Parsed` — детерминированный разбор файла (XML/CSV/XLSX),
 * `Recognized` — прочитано моделью со скана, `System` — консолидация данных самой системы.
 */
export type DataOrigin = 'Parsed' | 'Recognized' | 'System';

/** Привязка набора данных к объекту — только Mapping. Filter/Transformation/Sort — на DataSetSource.
 * Владелец — единый ownerId (DomainObject: документ или запись общих данных). */
/**
 * Что перезапустит «Перераспознать» у источника — считает СЕРВЕР (issue #815).
 *
 * `None` — действия нет вовсе (источник не из PDF): предлагать его значит вести в тупик, эндпоинт
 * отобьёт вызов на входе. `Source` — перезапустится только этот источник. `File` — распознавание
 * ВСЕГО набора: минуты работы модели и перезапись всех его проекций, о чём человек должен узнать
 * до нажатия, а не после.
 */
export type RecognizeScope = 'None' | 'Source' | 'File';

/**
 * Источник в составе привязки — УЗКИЙ DTO (`BindingSourceDto` на сервере), а не полный
 * `DataSetSource`.
 *
 * Перечислен полями, а не выведен из `DataSetSource`, потому что однажды именно это и подвело:
 * тип обещал полный источник, точка потребления читала `recognitionStale`, сервер его не слал —
 * и весь показ устаревания молча ничего не рисовал, а TypeScript был доволен. Добавляя сюда поле,
 * добавьте его и в `BindingSourceDto`, иначе оно будет вечно `undefined`.
 */
export interface DataSetBindingSource {
  id: string;
  name: string;
  sheetOrPath: string;
  cachedSchema: string;
  cachedRowCount: number;
  file?: Pick<DataSetFile, 'id' | 'name' | 'format' | 'scope' | 'scopeId'>;
  materializeTypeId: string | null;
  materializeMapping: Record<string, string> | null;
  origin?: DataOrigin;
  recognitionStale?: boolean;
  staleReason?: DataSetStaleReason | null;
  bindingCount?: number | null;
  recognizeScope?: RecognizeScope;
  /** Вычисляемые колонки: в `cachedSchema` их нет, а маппить по ним можно (issue #49). */
  computedColumns?: ComputedColumn[] | null;
}

export interface DataSetBinding {
  id: string;
  ownerId: string;
  sourceId: string;
  targetFieldKey: string | null;
  mapping: Record<string, string>;
  source?: DataSetBindingSource;
}

/** Владелец привязки к набору данных — единый объект (документ или запись общих данных). */
export interface DataSetBindingOwner {
  ownerId?: string;
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
