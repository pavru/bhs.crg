import { useState, useEffect, useMemo } from 'react';
import { AlertTriangle, AlertCircle, CheckCircle2, Circle, CircleDot, Database, ScanText, Info, ChevronDown, ChevronRight, FunctionSquare } from 'lucide-react';
import { Markdown } from '@/shared/ui/Markdown';
import { useListPrimitiveTypes } from '@/shared/api/primitiveTypes';
import { useListEnumTypes } from '@/shared/api/enumTypes';
import { useUpdateRequisites, useResolutionDiagnostics, brokenRefPaths, useAuditInstance } from '@/shared/api/documentSets';
import { valueIssuesByPath, deepIssueCount, issueCountInFields } from '@/shared/api/valueIssues';
import { ValueIssueHint, ValueIssueBadge } from '@/shared/ui/ValueIssue';
import { FUNCTIONAL_TAG, hasTag } from '@/shared/api/tags';
import type { DocumentInstance, DocumentType, PrimitiveTypeDef, EnumTypeDef, CommonDataEntry, DataSetStaleReason } from '@/shared/api/types';
import { SCOPE_LABELS, isFieldRef } from '@/shared/api/types';
import { useCommonDataForSet } from '@/shared/api/commonData';
import { groupEffectiveFields, parseSchemaFields, getDefaultValues, isScalarField, type SchemaField } from '@/shared/api/schema';
import { FieldSourceBinding } from './FieldSourceBinding';
import { ContainerFieldBinding } from './ContainerFieldBinding';
import { validateConstraint, isMissing, PrimitiveInput, FileField, ImageField, collectConstraintViolations, DocRefField, DocArrayField, ArrayFieldEditor, ComplexFieldGroup, AutoFieldsSection, SCOPE_TIER, ancestorTypeIds, parseBaseRef, BaseCandidatePicker, type BaseCandidate } from '../fields';
import { evalComputed, referencedKeys } from '@/shared/utils/computedExpression';
import { DocumentPreviewPanel } from './DocumentPreviewPanel';
import { useListDataSetBindings, usePreviewDataSetBindings } from '@/shared/api/datasets';
import { computeRecognizedFieldKeys, computeStaleFieldKeys, computeStaleReasonByField, staleReasonText } from '@/shared/api/datasetHelpers';
import { SourceOriginIcon } from '@/shared/ui/SourceOriginIcon';
import { mergeBindingPreviewsIntoValues } from '@/shared/api/datasetHelpers';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { containsRef } from './brokenRefs';
import { BrokenCountBadge } from './BrokenCountBadge';

/** Ссылка на сохранение вкладки: оболочка вызывает его, не зная внутренностей формы. */
export type SaveRef = { current: (() => Promise<boolean>) | null };

// ── Вкладка «Реквизиты» ─────────────────────────────────────────────────────────
// Выделено из editor/index.tsx (#490): файл на 1220 строк был оболочкой вкладок и двумя
// самими вкладками. Перенос без изменения поведения — вкладки ничего не разделяли между
// собой, кроме мелких помощников, которые уехали в отдельный модуль.

/// Плашка для doc-ref/doc-array поля, которое заполняется привязанным источником данных:
/// ручные ссылки скрываем, т.к. при генерации источник перезаписывает поле целиком
/// (см. issue #17 — «источник ИЛИ ссылки», взаимоисключающе).
function SourceBoundDocField(
  { recognized, stale, staleReason }:
  { recognized?: boolean; stale?: boolean; staleReason?: DataSetStaleReason | null },
) {
  const tone = stale ? 'text-warning' : 'text-brand';
  return (
    <div className="flex items-center gap-2 border border-brand/40 rounded-lg px-3 py-2 bg-brand/5">
      {recognized
        ? <ScanText size={14} className={`${tone} shrink-0`} />
        : <Database size={14} className={`${tone} shrink-0`} />}
      <span className="text-xs text-fg3">
        {recognized
          // Плашка вместо самих ссылок — единственное, что человек тут видит; промолчав о
          // происхождении здесь, мы оставили бы doc-ref немым, закрыв дверь у соседних полей.
          ? 'Заполняется из привязанного источника — значения распознаны со скана, возможны ошибки чтения.'
          : 'Заполняется из привязанного источника данных — правьте связку по иконке источника у поля.'}
        {/* Устаревание дописывается второй фразой, а не заменяет первую: происхождение и годность —
            разные вопросы, и ответ на один не отменяет другого (issue #815). */}
        {stale && <span className="text-warning"> {staleReasonText(staleReason)}.</span>}
      </span>
    </div>
  );
}

/// Подсказка о состоянии связанного скалярного поля без значения (issue #67): грузится / источник
/// недоступен / источник не дал значения — чтобы пустой read-only бокс не выглядел как «немой».
function BoundStateHint({ loading, error }: { loading: boolean; error: boolean }) {
  const text = loading ? 'Загрузка значения из источника…'
    : error ? 'Источник недоступен — проверьте в «Источниках»'
    : 'Источник не дал значения';
  return <p className="text-[11px] text-fg4 mt-0.5 italic">{text}</p>;
}

// Стабильный пустой дефолт для загружающихся списочных запросов. КРИТИЧНО (issue #305): `= []` в
// деструктуризации создаёт НОВЫЙ массив на каждый рендер, пока `data === undefined` (загрузка). Если
// такой список — dep useMemo, который в свою очередь dep эффекта с setState, получаем бесконечный цикл
// «Maximum update depth exceeded» до догрузки запроса (симптом «пустой экран при открытии документа»).
const EMPTY: never[] = [];


// ─── Базовый экземпляр (issue #71) ────────────────────────────────────────────
// Документ дочернего типа может наследоваться от базы — документа комплекта ЛИБО записи общих данных.
// Кандидаты берутся по всей цепочке типов-предков и по скоп-близости (комплект > раздел > стройка >
// система), внутри уровня — по близости наследования. Ссылка хранится как _baseRef {kind,id}.
export function RequisitesTab({ instance, setId, schemaFields, allDocTypes, docType, otherInstances, onDirty, saveRef, onBaseState, baseControlRef }: {
  instance: DocumentInstance; setId: string; schemaFields: SchemaField[];
  allDocTypes: DocumentType[]; docType: DocumentType | undefined;
  otherInstances: DocumentInstance[]; onClose: () => void;
  onDirty: (dirty: boolean) => void; saveRef: SaveRef;
  /** Синк состояния «Основы» вверх — для chip в шапке (issue #223). */
  onBaseState: (s: { hasBase: boolean; selected: BaseCandidate | undefined; missing: boolean; candidates: BaseCandidate[]; coveredCount: number }) => void;
  /** Канал управления «Основой» из шапки (доступен, пока смонтирована вкладка реквизитов). */
  baseControlRef: React.MutableRefObject<{ select: (c: BaseCandidate) => void; clear: () => void } | null>;
}) {
  const { data: primitiveTypes = [] } = useListPrimitiveTypes();
  const { data: enumTypes = [] } = useListEnumTypes();
  // Свежий документ (пустые реквизиты) — засеваем значения по умолчанию из эффективных полей
  // (включая переопределённые в дочернем типе); существующий грузим как сохранён. См. каталог,
  // где дефолты применялись, а у документов — нет.
  const [values, setValues] = useState<Record<string, unknown>>(() =>
    Object.keys(instance.requisites ?? {}).length === 0
      ? getDefaultValues(schemaFields)
      : { ...instance.requisites });
  const [constraintErrors, setConstraintErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState('');
  const [showValidation, setShowValidation] = useState(false);
  const [activeKey, setActiveKey] = useState<string>(''); // активный раздел (list-detail, issue #191)
  const [helpOpen, setHelpOpen] = useState(false); // справка типа (свёрнута по умолчанию)
  const [hintPicker, setHintPicker] = useState(false); // пикер «Основы» из строки-подсказки (issue #223)
  const [pendingBase, setPendingBase] = useState<BaseCandidate | null>(null); // подтверждение замены базы
  const mutation = useUpdateRequisites();

  // Поля, заполняемые привязкой к набору данных при генерации — источник перезаписывает их,
  // поэтому ручной ввод отключаем и не требуем от формы реквизитов (issue #55):
  // табличные (targetFieldKey, issue #17) + скалярные (ключи мэппинга, issue #55). Для скалярной
  // привязки эффективный маппинг — собственный (binding.mapping), а если он пуст — с материализации
  // источника (binding.source.materializeMapping, issue #19), см. DataSetMappingValue.EffectiveMappingJson.
  const { data: dsBindings = [] } = useListDataSetBindings({ ownerId: instance.id });
  const sourceBoundFields = useMemo(() => {
    const s = new Set<string>();
    for (const b of dsBindings) {
      if (b.targetFieldKey) { s.add(b.targetFieldKey); continue; }
      const effectiveMapping = Object.keys(b.mapping).length > 0 ? b.mapping : (b.source?.materializeMapping ?? {});
      for (const key of Object.keys(effectiveMapping)) s.add(key);
    }
    return s;
  }, [dsBindings]);
  // Поля, чей источник — распознанный скан (то же правило разбора привязки, что выше; признак
  // происхождения считает сервер). Человеку у read-only поля иначе неоткуда узнать, что значение
  // прочитала модель, — а к прочитанному моделью и доверие другое.
  const recognizedBoundFields = useMemo(() => computeRecognizedFieldKeys(dsBindings), [dsBindings]);
  // Поля, чей источник УСТАРЕЛ (issue #815): данные разошлись с файлом, из которого их читали. Это
  // не подмножество распознанных — устареть может и парсерный источник, не разобравшийся против
  // нового файла, поэтому счёт и подпись отдельные.
  const staleBoundFields = useMemo(() => computeStaleFieldKeys(dsBindings), [dsBindings]);
  // Причина в подсказке — только когда она у всех устаревших источников ОДНА. Смешав четыре
  // причины под одной формулировкой, подсказка соврала бы про три из них.
  const staleHint = useMemo(() => {
    const reasons = new Set(dsBindings.filter(b => b.source?.recognitionStale).map(b => b.source?.staleReason ?? null));
    const common = reasons.size === 1 ? [...reasons][0] : undefined;
    const what = common !== undefined
      ? staleReasonText(common)
      : 'Источники этих полей изменились после распознавания';
    // Глагол здесь законен: обзор источников документа открывается кнопкой в шапке, уходить из
    // формы (и терять несохранённое) не нужно.
    return `${what}. Перераспознать можно в «Источниках».`;
  }, [dsBindings]);
  // Причина берётся ТЕМ ЖЕ разбором привязки, что и множества выше (issue #815): свой поиск здесь
  // расходился с ним на табличных привязках и мог показать полю причину чужого источника.
  const staleReasons = useMemo(() => computeStaleReasonByField(dsBindings), [dsBindings]);
  const staleReasonOf = (key: string) => staleReasons.get(key) ?? null;
  // Скалярные поля — для per-field привязки «линза» (issue #296, фаза 1): выбор источника на поле +
  // авто-предложение покрыть остальные скалярные поля этого источника.
  const scalarSchemaFields = useMemo(() => schemaFields.filter(f => isScalarField(f) && f.type !== 'file'), [schemaFields]);

  // Битые ссылки (issue #332): цель удалена. Диагностику резолва тянем ОДИН раз в общий кэш (её же
  // читает панель «Проверить ссылки»), только если в реквизитах есть ссылки. Instance-промахи фронт
  // ловит сам в DocRefField; catalog/глубокие пути приходят из этой диагностики (code=leftover-ref).
  const hasRefValues = useMemo(() => containsRef(values), [values]);
  const { data: resolutionDiagnostics } = useResolutionDiagnostics(instance.id, hasRefValues);
  const brokenPaths = useMemo(() => brokenRefPaths(resolutionDiagnostics), [resolutionDiagnostics]);
  // Число битых ссылок под полем (по верхнему сегменту пути) — для свод-бейджей раздела и агрегата
  // на контейнерном поле (issue #334). Прямая ссылка (path == field.key) рисуется danger-плиткой
  // инлайн; «глубокие» пути (внутри complex/массива) видимы только через агрегат-бейдж.
  const brokenUnderKey = useMemo(() => {
    const m = new Map<string, number>();
    for (const p of brokenPaths) {
      const top = p.split(/[.[]/)[0];
      m.set(top, (m.get(top) ?? 0) + 1);
    }
    return m;
  }, [brokenPaths]);
  const brokenInFields = (fields: SchemaField[]) =>
    fields.reduce((n, f) => n + (brokenUnderKey.get(f.key) ?? 0), 0);
  // Значения не по объявленному типу (issue #644). Форма проверяет то, что печатают руками; всё
  // остальное — распознавание, вставка, авто-маппер, привязка набора, запись по API — до сих пор
  // доходило сюда непроверенным и молчало до выпуска. Правила берём с сервера (аудит документа), а
  // не повторяем на клиенте: разойдись они, форма показывала бы одно, а выпуск — другое.
  const { data: auditFindings } = useAuditInstance(setId, instance.id, true, 30_000);
  const valueIssues = useMemo(() => valueIssuesByPath(auditFindings), [auditFindings]);
  // «Глубокие» битые под полем (путь строго глубже самого поля) — прямую ссылку не считаем (она уже
  // danger-плитка). Для complex/массива это count вложенных/элементных битых ссылок.
  const deepBrokenCount = (key: string) => {
    let n = 0;
    for (const p of brokenPaths) if (p !== key && p.split(/[.[]/)[0] === key) n++;
    return n;
  };
  // Базовый экземпляр (issue #71): документ дочернего типа наследуется от базы — документа комплекта
  // ЛИБО записи общих данных (по цепочке типов-предков и скоп-близости). При связке наследуются её
  // данные (мердж при генерации), вручную заполняются только собственные поля. Ссылка — `_baseRef` {kind,id}.
  const ancestorIds = useMemo(() => ancestorTypeIds(docType, allDocTypes), [docType, allDocTypes]);
  const hasBase = ancestorIds.length > 0;
  // Общие данные всех уровней скопа комплекта (Set/Section/Construction/System) — кандидаты-записи.
  const { data: commonData = EMPTY } = useCommonDataForSet({ setId, enabled: hasBase });
  const baseRef = useMemo(() => parseBaseRef(values._baseRef), [values._baseRef]);

  const baseCandidates = useMemo<BaseCandidate[]>(() => {
    if (!hasBase) return [];
    const ancestorSet = new Set(ancestorIds);
    const distOf = (typeId: string) => { const i = ancestorIds.indexOf(typeId); return i < 0 ? 999 : i; };
    const docs: BaseCandidate[] = otherInstances
      .filter(i => ancestorSet.has(i.documentTypeId))
      .map(i => ({ kind: 'instance', id: i.id, name: i.name ?? '(без имени)', typeId: i.documentTypeId,
        tier: 0, scopeLabel: 'Комплект', dist: distOf(i.documentTypeId) }));
    const entries: BaseCandidate[] = (commonData as CommonDataEntry[])
      .filter(e => ancestorSet.has(e.compositeTypeId))
      .map(e => ({ kind: 'catalog', id: e.id, name: e.displayName, typeId: e.compositeTypeId,
        tier: SCOPE_TIER[e.scope], scopeLabel: SCOPE_LABELS[e.scope], dist: distOf(e.compositeTypeId) }));
    return [...docs, ...entries].sort((a, b) => a.tier - b.tier || a.dist - b.dist || a.name.localeCompare(b.name, 'ru'));
  }, [hasBase, ancestorIds, otherInstances, commonData]);

  const selectedBase = baseRef ? baseCandidates.find(c => c.id === baseRef.id) : undefined;
  // Поля, покрытые базовым экземпляром (его собственные ключи), не требуются к заполнению здесь —
  // придут наследованием при генерации (тот же класс, что sourceBoundFields из #55).
  const baseCoveredFields = useMemo(() => {
    if (!baseRef) return new Set<string>();
    if (baseRef.kind === 'instance') {
      const inst = otherInstances.find(i => i.id === baseRef.id);
      return new Set(inst ? Object.keys(inst.requisites) : []);
    }
    const entry = (commonData as CommonDataEntry[]).find(e => e.id === baseRef.id);
    return new Set(entry ? Object.keys(entry.data) : []);
  }, [baseRef, otherInstances, commonData]);
  const ownFields = docType ? parseSchemaFields(docType.schema) : schemaFields;
  const ownFieldKeys = new Set(ownFields.map(f => f.key));
  // При выбранной базе скрываем ТОЛЬКО поля, реально покрытые ею; собственные (можно переопределить)
  // и унаследованные, но НЕ покрытые базой (напр. база — дед/запись общих данных, часть полей не даёт),
  // показываем — их нужно заполнить вручную (issue #71).
  const displayFields = (hasBase && baseRef)
    ? schemaFields.filter(f => ownFieldKeys.has(f.key) || !baseCoveredFields.has(f.key))
    : schemaFields;
  function selectBase(c: BaseCandidate) { setValue('_baseRef', { kind: c.kind, id: c.id }); }
  // Замена уже выбранной базы на другую — сперва подтверждение (issue #223): набор предзаполняемых
  // полей меняется. Значения НЕ удаляются (база мёржится в пустые/покрытые при генерации), поэтому
  // диалог предупреждает, а не «перезаписывает». Первый выбор (базы ещё нет) — сразу, без диалога.
  function requestSelectBase(c: BaseCandidate) {
    if (baseRef && baseRef.id !== c.id) setPendingBase(c);
    else selectBase(c);
  }
  function clearBaseRef() {
    setValues(p => { const n = { ...p }; delete n._baseRef; return n; });
    onDirty(true);
  }

  // Синк «Основы» в шапку (issue #223): источник правды — values._baseRef здесь; шапка лишь отражает
  // состояние (chip) и вызывает select/clear через канал, пока эта вкладка смонтирована.
  const missingBase = hasBase && !!baseRef && !selectedBase;
  const baseCoveredCount = useMemo(
    () => schemaFields.filter(f => baseCoveredFields.has(f.key)).length,
    [schemaFields, baseCoveredFields]);
  useEffect(() => {
    onBaseState({ hasBase, selected: selectedBase, missing: missingBase, candidates: baseCandidates, coveredCount: baseCoveredCount });
  }, [hasBase, selectedBase, missingBase, baseCandidates, baseCoveredCount, onBaseState]);
  useEffect(() => {
    baseControlRef.current = { select: requestSelectBase, clear: clearBaseRef };
    return () => { baseControlRef.current = null; };
  });

  // Обязательное поле, покрытое активной привязкой ИЛИ базовым экземпляром, не блокирует сохранение
  // реквизитов — значение подставится при генерации, форма его не хранит.
  const isFieldMissing = (f: SchemaField, val: unknown) =>
    isMissing(f, val) && !sourceBoundFields.has(f.key) && !baseCoveredFields.has(f.key);

  // Предпросмотр значений привязок (issue #67): скалярный биндинг не пишет значение в реквизиты —
  // оно резолвится только при генерации. Тот же preview-эндпоинт, что и в «Источниках»,
  // даёт резолвнутое значение для показа read-only прямо в поле (в saved-values НЕ пишем).
  const { data: bindingPreviews, isFetching: previewingBindings, refetch: runBindingPreview, error: previewError } =
    usePreviewDataSetBindings({ ownerId: instance.id });
  useEffect(() => {
    if (sourceBoundFields.size > 0) void runBindingPreview();
  }, [sourceBoundFields.size]); // eslint-disable-line react-hooks/exhaustive-deps
  // Оверлей отображения (не сохраняется): резолвнутые значения биндингов поверх пустого объекта.
  const boundValues = useMemo(
    () => bindingPreviews ? mergeBindingPreviewsIntoValues({}, bindingPreviews) : {},
    [bindingPreviews]);
  const hasBindingError = !!previewError || (bindingPreviews?.some(p => p.mode === 'error') ?? false);

  function getEnumDef(field: SchemaField): EnumTypeDef | undefined {
    if (field.type !== 'enum' || !field.typeId) return undefined;
    return enumTypes.find(et => et.id === field.typeId);
  }

  function getPrimitiveDef(field: SchemaField): PrimitiveTypeDef | undefined {
    if (field.type !== 'primitive') return undefined;
    return primitiveTypes.find(pt => pt.id === field.typeId);
  }

  function setValue(key: string, val: unknown, primitiveDef?: PrimitiveTypeDef) {
    setValues(p => ({ ...p, [key]: val }));
    onDirty(true);
    if (primitiveDef) {
      const err = validateConstraint(val, primitiveDef);
      setConstraintErrors(prev => {
        const next = { ...prev };
        if (err) next[key] = err;
        else delete next[key];
        return next;
      });
    }
  }
  // Значение поля непусто (для счётчика заполнения раздела).
  function hasValue(val: unknown): boolean {
    if (isFieldRef(val)) return true;
    if (Array.isArray(val)) return val.length > 0;
    if (val != null && typeof val === 'object') return Object.keys(val).length > 0;
    return val != null && String(val).trim() !== '';
  }
  // Статистика раздела: total/filled (bound/base-covered считаем заполненными — придут при генерации),
  // missing — обязательные незаполненные (гейтится showValidation при отображении иконки ошибки).
  function sectionStats(fields: SchemaField[]) {
    let total = 0, filled = 0, missing = 0;
    for (const f of fields) {
      if (f.computed) continue; // расчётные поля не заполняются пользователем — вне прогресса (#368)
      total++;
      if (hasValue(values[f.key]) || sourceBoundFields.has(f.key) || baseCoveredFields.has(f.key)) filled++;
      if (isFieldMissing(f, values[f.key])) missing++;
    }
    return { total, filled, missing };
  }

  // Обязательные незаполненные (не покрытые привязкой/базой) — для баннера-черновика и маркеров.
  const missingRequired = schemaFields.filter(f => isFieldMissing(f, values[f.key]));

  // Сохраняет реквизиты. Возвращает true при успехе. НЕ закрывает редактор.
  // issue #296 (вариант A): обязательность — инвариант ГЕНЕРАЦИИ, не сохранения. Save хранит черновик
  // (в т.ч. с пустыми обязательными — их можно заполнить позже / привязать к источнику); блокирует
  // только НЕвалидное введённое (формат/ограничения примитивов). Это разрывает дедлок «нельзя уйти
  // на «Данные» привязать поле, потому что не сохраняется из-за этого же поля».
  async function handleSaveCore(): Promise<boolean> {
    setError('');
    // Формат/ограничения — блокируют (нельзя хранить мусор).
    // По ВСЕМУ документу, включая строки таблиц и составные поля (#463). Раньше проверялся только
    // верхний уровень — так в поле «Цело число» и оказался «3.3».
    const constraintViolations = collectConstraintViolations(values, schemaFields, allDocTypes, primitiveTypes);
    if (Object.keys(constraintViolations).length > 0) {
      setConstraintErrors(constraintViolations);
      const nested = Object.keys(constraintViolations).filter(k => k.includes('.') || k.includes('['));
      setError(nested.length > 0
        // Адрес обязателен: нарушение внутри строки таблицы не видно, пока строку не откроешь.
        ? `Исправьте ошибки формата: ${Object.entries(constraintViolations)[0][0]} — ${Object.values(constraintViolations)[0]}`
        : 'Исправьте ошибки формата в полях');
      return false;
    }
    // Незаполненные обязательные не блокируют — но показываем маркеры (не «тихо»).
    setShowValidation(missingRequired.length > 0);
    try {
      await mutation.mutateAsync({ setId, instanceId: instance.id, requisites: values });
      onDirty(false);
      return true;
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Ошибка');
      return false;
    }
  }

  // Регистрируем актуальную функцию сохранения для родителя (guard смены вкладки).
  useEffect(() => { saveRef.current = handleSaveCore; return () => { saveRef.current = null; }; });

  if (schemaFields.length === 0)
    return <div className="text-sm text-fg4 py-4 text-center">Схема полей не задана.</div>;

  const sections = groupEffectiveFields(displayFields, docType?.schema ?? {});
  // Rail разделов — только для крупных форм с несколькими именованными группами (issue #102, P3):
  // на короткой форме или без групп он бесполезен.
  const titledSections = sections.filter(s => s.title);
  const ungrouped = sections.find(s => !s.title && s.fields.length > 0);

  // Пункты list-detail (issue #191): «Основные реквизиты» (несгруппированные поля) → разделы схемы.
  // «Основа» (базовый экземпляр) переехала в chip шапки документа (issue #223) — это документ-левел
  // мета-настройка, а не раздел полей. Слева drawer, справа — только активный пункт.
  type RailItem = { key: string; title: string; kind: 'fields'; fields: SchemaField[] };
  const items: RailItem[] = [];
  if (ungrouped) items.push({ key: ungrouped.key || '__main__', title: 'Основные реквизиты', kind: 'fields', fields: ungrouped.fields });
  for (const s of titledSections) items.push({ key: s.key, title: s.title!, kind: 'fields', fields: s.fields });

  const useDrawer = items.length >= 2; // мелкие формы (<2 пунктов) — плоский fallback без drawer
  const activeIdx = Math.max(0, items.findIndex(i => i.key === activeKey));
  const activeItem = items[activeIdx] ?? items[0];
  // Пустой экран (баг): если ВСЕ поля покрыты основой/привязками (напр. «Титульный лист» наследует
  // всё от базы «Проект»), displayFields пуст → items пуст → activeItem undefined → renderItemBody
  // падал. Показываем сообщение вместо краша (все хуки выше — ранний return безопасен).
  if (!activeItem) {
    return (
      <div className="flex-1 min-h-0 flex items-center justify-center px-6 text-center">
        <p className="text-sm text-fg4 max-w-md">
          Все поля этого документа заполняются автоматически — из основы
          {hasBase && baseRef ? ' (базового экземпляра)' : ''} или привязок к источникам данных.
          Заполнять здесь нечего; проверить итог можно в предпросмотре или при генерации.
        </p>
      </div>
    );
  }
  const prevItem = activeIdx > 0 ? items[activeIdx - 1] : null;
  const nextItem = activeIdx >= 0 && activeIdx < items.length - 1 ? items[activeIdx + 1] : null;
  const fieldsItems = items.filter(i => i.kind === 'fields'); // для подстроки «раздел X из Y»

    const isWide = (f: SchemaField) =>
      f.type === 'complex' || f.type === 'array' || f.type === 'doc-ref' ||
      f.type === 'doc-array' || f.type === 'image' || f.type === 'file' || f.type === 'text';

    function renderCell(field: SchemaField) {
          // Расчётное поле (issue #368) — не ввод: read-only fx-дисплей с клиентским live-предпросмотром
          // (авторитет — бэкенд при генерации). До заполнения зависимостей подсказываем, чего не хватает.
          if (field.computed) {
            const preview = evalComputed(field.expression ?? '', values);
            const missingInputs = referencedKeys(field.expression ?? '')
              .filter(k => !hasValue(values[k]))
              .map(k => schemaFields.find(sf => sf.key === k)?.title ?? k);
            const empty = preview.value == null || preview.value === '';
            return (
              <div key={field.key}>
                <label className="mb-1 flex items-center gap-1 text-xs font-medium text-fg2">
                  <span className="truncate">{field.title}</span>
                  <span className="inline-flex items-center gap-0.5 text-[10px] text-brand shrink-0">
                    <FunctionSquare size={11} /> вычисляется
                  </span>
                </label>
                <div className="w-full rounded-md border border-dashed border-stroke bg-muted/30 px-3 py-1.5 text-sm"
                  title={field.expression}>
                  {preview.error
                    ? <span className="text-danger">Ошибка формулы</span>
                    : empty && missingInputs.length > 0
                      ? <span className="text-fg4">— · заполните {missingInputs.join(', ')}</span>
                      : <span className="text-fg1">{String(preview.value ?? '—')}</span>}
                </div>
              </div>
            );
          }
          const raw = values[field.key];
          const missing = showValidation && isFieldMissing(field, raw);
          const bound = sourceBoundFields.has(field.key);
          // Значение для показа связанного скалярного поля — резолвнутое из источника (issue #67);
          // в saved-values не пишем. Пусто → покажем подсказку о состоянии вместо «немого» бокса.
          const boundVal = bound ? boundValues[field.key] : undefined;
          const displayValue = bound && boundVal != null && boundVal !== '' ? boundVal : raw;
          const boundEmpty = bound && (boundVal == null || boundVal === '');
          const primitiveDef = getPrimitiveDef(field);
          const constraintError = constraintErrors[field.key];
          const hasError = missing || !!constraintError;
          const wide = isWide(field);

          if (wide) {
            const isContainer = field.type === 'complex' || field.type === 'array' || field.type === 'doc-ref' || field.type === 'doc-array';
            return (
              <div key={field.key} className="col-span-2 relative group">
                {/* Per-field привязка контейнерного поля «линза» (issue #296, фаза 2a) — модалка в углу. */}
                {isContainer && (
                  <div className="absolute top-0.5 right-0.5 z-10">
                    <ContainerFieldBinding instanceId={instance.id} setId={setId} field={field}
                      allDocTypes={allDocTypes} bindings={dsBindings} />
                  </div>
                )}
                {/* Текст подписывает себя сам (issue #574): подпись живёт в вырезе рамки, как у
                    соседних однострочных полей. Своя подпись здесь дала бы вторую — над полем.
                    Кроме привязанного к источнику: там контрол read-only и подпись сверху идёт
                    вместе с бейджем привязки — как и у простых полей ниже. */}
                {field.type !== 'boolean' && field.type !== 'complex' && field.type !== 'array'
                  && !(field.type === 'text' && !bound) && (
                  <label className="block text-xs font-medium text-fg2 mb-1 pr-5">
                    {field.title}
                    {field.required && <span className="ml-0.5 text-danger">*</span>}
                    {!field.required && <span className="ml-1 text-[10px] text-fg4 font-normal">опц.</span>}
                    <BrokenCountBadge count={deepBrokenCount(field.key)} className="ml-1.5 align-middle" />
                    <ValueIssueBadge count={deepIssueCount(valueIssues, field.key)} className="ml-1.5 align-middle" />
                    {/* Широкие поля (многострочный текст, картинка, файл) подписываются здесь, и
                        признак происхождения им нужен ровно так же: связанное текстовое поле
                        read-only, и узнать, что значение прочитано со скана, иначе неоткуда. */}
                    {(recognizedBoundFields.has(field.key) || staleBoundFields.has(field.key)) && (
                      <span className="ml-1 inline-block align-text-bottom">
                        <SourceOriginIcon
                          origin={recognizedBoundFields.has(field.key) ? 'Recognized' : undefined}
                          stale={staleBoundFields.has(field.key)}
                          staleReason={staleReasonOf(field.key)} />
                      </span>
                    )}
                  </label>
                )}
                {field.type === 'complex' ? (
                  bound ? <SourceBoundDocField recognized={recognizedBoundFields.has(field.key)}
                    stale={staleBoundFields.has(field.key)} staleReason={staleReasonOf(field.key)} /> : (
                  <div>
                    <div className="flex items-center justify-between mb-1">
                      <label className="block text-xs font-medium text-fg2 pr-5">
                        {field.title}
                        {field.required && <span className="ml-0.5 text-danger">*</span>}
                        <BrokenCountBadge count={deepBrokenCount(field.key)} className="ml-1.5 align-middle" />
                    <ValueIssueBadge count={deepIssueCount(valueIssues, field.key)} className="ml-1.5 align-middle" />
                      </label>
                    </div>
                    <ComplexFieldGroup field={field} allDocTypes={allDocTypes} value={raw}
                      onChange={v => setValue(field.key, v)} showValidation={showValidation}
                      setId={setId} otherInstances={otherInstances} docRefMode="instance"
                      broken={brokenPaths.has(field.key)} />
                  </div>
                  )
                ) : field.type === 'array' ? (
                  bound ? <SourceBoundDocField recognized={recognizedBoundFields.has(field.key)}
                    stale={staleBoundFields.has(field.key)} staleReason={staleReasonOf(field.key)} /> : (
                  <ArrayFieldEditor field={field} allDocTypes={allDocTypes} value={raw}
                    onChange={v => setValue(field.key, v)} showValidation={showValidation}
                    setId={setId} otherInstances={otherInstances} docRefMode="instance"
                    brokenPaths={brokenPaths} basePath={field.key} savedAt={instance.updatedAt} />
                  )
                ) : field.type === 'doc-ref' ? (
                  sourceBoundFields.has(field.key) ? <SourceBoundDocField recognized={recognizedBoundFields.has(field.key)}
                    stale={staleBoundFields.has(field.key)} staleReason={staleReasonOf(field.key)} /> : (
                    <DocRefField field={field} allDocTypes={allDocTypes} value={raw}
                      onChange={v => setValue(field.key, v)} otherInstances={otherInstances} setId={setId}
                      broken={brokenPaths.has(field.key)} />
                  )
                ) : field.type === 'doc-array' ? (
                  sourceBoundFields.has(field.key) ? <SourceBoundDocField recognized={recognizedBoundFields.has(field.key)}
                    stale={staleBoundFields.has(field.key)} staleReason={staleReasonOf(field.key)} /> : (
                    <DocArrayField field={field} allDocTypes={allDocTypes} value={raw}
                      onChange={v => setValue(field.key, v)} otherInstances={otherInstances} setId={setId}
                      brokenPaths={brokenPaths} basePath={field.key} savedAt={instance.updatedAt} />
                  )
                ) : field.type === 'image' ? (
                  <ImageField value={raw} onChange={v => setValue(field.key, v)} />
                ) : field.type === 'file' ? (
                  <FileField value={raw} onChange={v => setValue(field.key, v)}
                    printForm={hasTag(field.tags, FUNCTIONAL_TAG.docPrintForm) ? {
                      setId, instanceId: instance.id, fieldKey: field.key,
                      onMetaUpdated: updates => {
                        setValues(prev => ({ ...prev, ...updates }));
                      },
                    } : undefined} />
                ) : (
                  <PrimitiveInput field={field} value={displayValue}
                    label={field.type === 'text' && !bound ? field.title : undefined}
                    onChange={v => setValue(field.key, v, primitiveDef)}
                    invalid={hasError} primitiveTypeDef={primitiveDef} enumTypeDef={getEnumDef(field)} readOnly={bound} />
                )}
                {boundEmpty && <BoundStateHint loading={previewingBindings} error={hasBindingError} />}
                {missing && <p className="text-xs text-danger mt-1">Обязательное поле</p>}
                {!missing && constraintError && <p className="text-xs text-danger mt-1">{constraintError}</p>}
                {/* Расхождение с типом (issue #644) — после ошибок формы: те про текущий ввод, это
                    про сохранённое значение, чаще всего пришедшее не отсюда. */}
                {!hasError && <ValueIssueHint messages={valueIssues.get(field.key)} />}
              </div>
            );
          }

          // Простые поля (string/number/date/enum/boolean/primitive). Редактируемые — MD3 floating-label
          // (G1); привязанные к источнику (read-only, с бейджем) — оставляем label-сверху.
          return (
            <div key={field.key} className="col-span-1 min-w-0 relative group">
              {/* Per-field привязка «линза» (issue #296, фаза 1): иконка в углу — привязать/изменить/отвязать. */}
              <div className="absolute top-0.5 right-0.5 z-10">
                <FieldSourceBinding instanceId={instance.id} setId={setId} field={field}
                  scalarFields={scalarSchemaFields} bindings={dsBindings} />
              </div>
              {bound ? (
                <>
                  <label className="block text-xs font-medium text-fg2 mb-1 pr-5">
                    {field.title}
                    {field.required && <span className="ml-0.5 text-danger">*</span>}
                    {primitiveDef && <span className="ml-1 text-[10px] text-fg4 font-normal">· {primitiveDef.name}</span>}
                    {(recognizedBoundFields.has(field.key) || staleBoundFields.has(field.key)) && (
                      <span className="ml-1 inline-block align-text-bottom">
                        <SourceOriginIcon
                          origin={recognizedBoundFields.has(field.key) ? 'Recognized' : undefined}
                          stale={staleBoundFields.has(field.key)}
                          staleReason={staleReasonOf(field.key)} />
                      </span>
                    )}
                  </label>
                  <PrimitiveInput field={field} value={displayValue}
                    onChange={v => setValue(field.key, v, primitiveDef)}
                    invalid={hasError} primitiveTypeDef={primitiveDef} enumTypeDef={getEnumDef(field)} readOnly />
                </>
              ) : (
                <PrimitiveInput field={field} value={displayValue} label={field.title}
                  hint={primitiveDef ? primitiveDef.name : undefined}
                  onChange={v => setValue(field.key, v, primitiveDef)}
                  invalid={hasError} primitiveTypeDef={primitiveDef} enumTypeDef={getEnumDef(field)} />
              )}
              {boundEmpty && <BoundStateHint loading={previewingBindings} error={hasBindingError} />}
              {missing && <p className="text-[11px] text-danger mt-0.5">Обязательное поле</p>}
              {!missing && constraintError && <p className="text-[11px] text-danger mt-0.5">{constraintError}</p>}
              {!hasError && <ValueIssueHint messages={valueIssues.get(field.key)} compact />}
            </div>
          );
    }

    function fieldGrid(fields: SchemaField[]) {
      return <div className="grid grid-cols-2 gap-x-4 gap-y-4">{fields.map(renderCell)}</div>;
    }

    // Поля, заполняемые из источника данных (read-only), прячем под сворачиваемую секцию
    // «Заполняются автоматически» — чтобы форма не превращалась в «портянку» (issue #102, P2).
    function renderFields(fields: SchemaField[]) {
      const auto = fields.filter(f => sourceBoundFields.has(f.key));
      if (auto.length === 0) return fieldGrid(fields);
      const normal = fields.filter(f => !sourceBoundFields.has(f.key));
      return (
        <div className="space-y-4">
          {normal.length > 0 && fieldGrid(normal)}
          <AutoFieldsSection count={auto.length}
            recognizedCount={auto.filter(f => recognizedBoundFields.has(f.key)).length}
            staleCount={auto.filter(f => staleBoundFields.has(f.key)).length}
            staleHint={staleHint}>
            {fieldGrid(auto)}
          </AutoFieldsSection>
        </div>
      );
    }

  // Тело пункта справа: заголовок + прогресс + поля. В первом пункте — строка-подсказка про «Основу»
  // (issue #223): сам выбор базы живёт в chip шапки, здесь — только напоминание, когда не выбрана.
  function renderItemBody(item: RailItem) {
    const stats = sectionStats(item.fields);
    const sectionIdx = fieldsItems.findIndex(i => i.key === item.key);
    const isFirst = item.key === items[0]?.key;
    return (
      <>
        {isFirst && hasBase && !baseRef && (
          <p className="text-xs text-fg4 mb-4">
            Основа не выбрана — все поля заполняются вручную.{' '}
            <button type="button" onClick={() => setHintPicker(true)} className="text-brand hover:text-brand-hover underline underline-offset-2">
              Выбрать основу
            </button>
          </p>
        )}
        <div className="mb-4">
          <h2 className="text-xl font-normal text-fg1">{item.title}</h2>
          <p className="text-xs text-fg4 mt-0.5">
            Заполнено {stats.filled} из {stats.total}
            {sectionIdx >= 0 && ` · раздел ${sectionIdx + 1} из ${fieldsItems.length}`}
          </p>
        </div>
        {renderFields(item.fields)}
      </>
    );
  }

  const helpText = (docType?.schema as { help?: string } | undefined)?.help?.trim();
  const hasLevelProfile = allDocTypes.some(t => {
    const tags = (t.schema as { tags?: string[] }).tags ?? [];
    return hasTag(tags, 'profile.construction') || hasTag(tags, 'profile.section') || hasTag(tags, 'profile.set');
  });

  return (
    <div className="flex flex-col min-h-0 flex-1">
      {(helpText || hasLevelProfile) && (
        <div className="shrink-0 px-6 pt-3">
          <div className="rounded-lg border border-stroke bg-brand-subtle/30">
            <button type="button" onClick={() => setHelpOpen(o => !o)}
              className="w-full flex items-center gap-2 px-3 py-2 text-left">
              {helpOpen ? <ChevronDown size={14} className="text-brand shrink-0" /> : <ChevronRight size={14} className="text-brand shrink-0" />}
              <Info size={14} className="text-brand shrink-0" />
              <span className="text-sm font-medium text-fg1">Справка</span>
            </button>
            {helpOpen && (
              <div className="px-3 pb-3 space-y-2">
                {helpText && <Markdown>{helpText}</Markdown>}
                {hasLevelProfile && (
                  <p className="text-xs text-fg3">
                    ℹ Часть данных подтягивается из <span className="text-brand-hover font-medium">профиля уровня</span>{' '}
                    (стройка/раздел/комплект) — они доступны в шаблоне как <code className="font-mono bg-muted text-fg1 px-1 rounded">data.уровень.*</code> и заполняются на странице «Общие данные» уровня, а не здесь.
                  </p>
                )}
              </div>
            )}
          </div>
        </div>
      )}
      {/* Баннер неполноты (issue #296, вариант A): черновик можно сохранить с пустыми обязательными —
          но неполнота не «тихая», показываем всегда; жёсткий гейт — на генерации. */}
      {missingRequired.length > 0 && (
        <div className="shrink-0 px-6 pt-3">
          <div className="flex items-start gap-2.5 rounded-lg border border-warning-border bg-warning-subtle px-3 py-2 text-xs text-warning">
            <AlertTriangle size={15} className="shrink-0 mt-0.5" />
            <span>
              Черновик — не заполнено обязательных: <b>{missingRequired.length}</b>. Их можно заполнить позже или
              привязать к источнику данных; для генерации PDF потребуются.
            </span>
          </div>
        </div>
      )}
      <div className="flex-1 min-h-0 flex">
        {/* Drawer разделов (list-detail, issue #191) */}
        {useDrawer && (
          <nav aria-label="Разделы формы" className="hidden lg:flex flex-col w-72 shrink-0 border-r border-stroke overflow-y-auto p-3 gap-0.5">
            <div className="text-xs font-medium text-fg4 px-3 pb-1.5">Разделы</div>
            {items.map(item => {
              const isActive = item.key === activeItem.key;
              const stats = sectionStats(item.fields);
              const broken = brokenInFields(item.fields);
              let Icon = Circle, iconCls = 'text-fg4';
              // Битые ссылки — наивысший приоритет индикатора раздела (issue #334): всегда danger.
              if (broken > 0) { Icon = AlertCircle; iconCls = 'text-danger'; }
              else if (showValidation && stats.missing > 0) { Icon = AlertCircle; iconCls = 'text-danger'; }
              else if (stats.total > 0 && stats.filled === stats.total) { Icon = CheckCircle2; iconCls = 'text-brand'; }
              else if (isActive) { Icon = CircleDot; iconCls = 'text-brand'; }
              else if (stats.filled > 0) { Icon = CircleDot; iconCls = 'text-fg3'; }
              return (
                <button key={item.key} type="button" onClick={() => setActiveKey(item.key)}
                  aria-current={isActive ? 'true' : undefined}
                  className={`w-full flex items-center gap-3 text-left px-3 h-11 rounded-full transition-colors ${
                    isActive ? 'bg-brand-subtle text-brand-hover font-medium' : 'text-fg3 hover:bg-muted hover:text-fg1'}`}>
                  <Icon size={18} className={`shrink-0 ${iconCls}`} />
                  <span className="flex-1 truncate text-sm">{item.title}</span>
                  <BrokenCountBadge count={broken} className="shrink-0" />
                  <ValueIssueBadge count={issueCountInFields(valueIssues, item.fields.map(f => f.key))} className="shrink-0" />
                  {stats && <span className="text-xs text-fg4 tabular-nums shrink-0">{stats.filled}/{stats.total}</span>}
                </button>
              );
            })}
          </nav>
        )}
        {/* Детали активного раздела */}
        <div className="flex-1 min-h-0 overflow-y-auto">
          <div className="mx-auto max-w-3xl px-8 py-6">
            {useDrawer ? (
              <>
                {renderItemBody(activeItem)}
                {(prevItem || nextItem) && (
                  <div className="flex items-center justify-between gap-3 mt-8 pt-4 border-t border-stroke">
                    {prevItem
                      ? <Button variant="outlined" onClick={() => setActiveKey(prevItem.key)}>← {prevItem.title}</Button>
                      : <span />}
                    {nextItem
                      ? <Button variant="tonal" onClick={() => setActiveKey(nextItem.key)}>{nextItem.title} →</Button>
                      : <span />}
                  </div>
                )}
              </>
            ) : (
              <div className="space-y-6">
                {items.map(item => <div key={item.key}>{renderItemBody(item)}</div>)}
              </div>
            )}
          </div>
        </div>
        <DocumentPreviewPanel instanceId={instance.id} requisites={values} />
      </div>
      {error && (
        <div className="shrink-0 px-6 py-2 bg-surface border-t border-stroke">
          <p className="text-sm text-danger">{error}</p>
        </div>
      )}
      <BaseCandidatePicker open={hintPicker} onOpenChange={setHintPicker} candidates={baseCandidates} onSelect={requestSelectBase} />
      <ConfirmDialog
        open={!!pendingBase}
        onOpenChange={o => { if (!o) setPendingBase(null); }}
        title="Заменить основу?"
        description={
          <>Набор предзаполняемых полей изменится: часть значений может перестать наследоваться от текущей основы, а другие поля станут обязательными. Введённые вручную значения не удаляются.</>
        }
        confirmLabel="Заменить"
        onConfirm={() => { if (pendingBase) selectBase(pendingBase); setPendingBase(null); }}
      />
    </div>
  );
}

// ─── Generation tab ───────────────────────────────────────────────────────────
