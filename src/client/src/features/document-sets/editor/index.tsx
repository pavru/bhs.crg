import { useState, useEffect, useRef, useMemo } from 'react';
import { Loader2, FileText, Download, Eye, Pencil, Bug, ShieldCheck, AlertTriangle, AlertCircle, CheckCircle2, Circle, CircleDot, Mail, Database, X, Info, ChevronDown, ChevronRight } from 'lucide-react';
import { Markdown } from '@/shared/ui/Markdown';
import { useAuth } from '@/shared/hooks/useAuth';
import { useEmailDocument } from '@/shared/api/documentSets';
import { EmailSendDialog } from '../EmailSendDialog';
import { useListPrimitiveTypes } from '@/shared/api/primitiveTypes';
import { useListEnumTypes } from '@/shared/api/enumTypes';
import {
  useUpdateRequisites, useGenerateDocument,
  useSetDocumentTemplates, useRenameDocumentInstance,
  downloadGeneratedFile, previewGeneratedFile, downloadDebugBundle,
  validateResolution, type ResolutionDiagnostic,
} from '@/shared/api/documentSets';
import { useListTemplates } from '@/shared/api/templates';
import { FUNCTIONAL_TAG } from '@/shared/api/tags';
import type { DocumentInstance, DocumentType, Template, PrimitiveTypeDef, EnumTypeDef, CommonDataEntry } from '@/shared/api/types';
import { SCOPE_LABELS, isFieldRef } from '@/shared/api/types';
import { useCommonDataForSet } from '@/shared/api/commonData';
import {
  groupEffectiveFields, resolveEffectiveFields, compositeFieldHasTag, parseSchemaFields,
  getDefaultValues, isScalarField, type SchemaField,
} from '@/shared/api/schema';
import { FieldSourceBinding } from './FieldSourceBinding';
import { ContainerFieldBinding } from './ContainerFieldBinding';
import {
  STATUS_LABELS, STATUS_COLORS,
  validateConstraint, isMissing, PrimitiveInput, FileField, ImageField,
  DocRefField, DocArrayField, ArrayFieldEditor, ComplexFieldGroup, AutoFieldsSection,
  SCOPE_TIER, ancestorTypeIds, parseBaseRef, BaseInstanceChip, BaseCandidatePicker, type BaseCandidate,
} from '../fields';
import { ruCount } from '@/shared/utils/pluralize';
import { DataSetsTab } from './DataSetsTab';
import { DocumentPreviewPanel } from './DocumentPreviewPanel';
import { useListDataSetBindings, usePreviewDataSetBindings } from '@/shared/api/datasets';
import { mergeBindingPreviewsIntoValues } from '@/shared/api/datasetHelpers';
import { QualityLinksTab } from './QualityLinksTab';
import { DocumentTemplateParams } from './DocumentTemplateParams';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';

type SaveRef = { current: (() => Promise<boolean>) | null };

/** Парсит JSON-строку массива id (templateIds) в массив; безопасно к битому/пустому значению. */
function parseIdArray(json: string | null): string[] {
  if (!json) return [];
  try { const a = JSON.parse(json); return Array.isArray(a) ? a as string[] : []; } catch { return []; }
}

/// Плашка для doc-ref/doc-array поля, которое заполняется привязанным источником данных:
/// ручные ссылки скрываем, т.к. при генерации источник перезаписывает поле целиком
/// (см. issue #17 — «источник ИЛИ ссылки», взаимоисключающе).
function SourceBoundDocField() {
  return (
    <div className="flex items-center gap-2 border border-brand/40 rounded-lg px-3 py-2 bg-brand/5">
      <Database size={14} className="text-brand shrink-0" />
      <span className="text-xs text-fg3">
        Заполняется из привязанного источника данных — правьте связку по иконке источника у поля.
      </span>
    </div>
  );
}

/// Подсказка о состоянии связанного скалярного поля без значения (issue #67): грузится / источник
/// недоступен / источник не дал значения — чтобы пустой read-only бокс не выглядел как «немой».
function BoundStateHint({ loading, error }: { loading: boolean; error: boolean }) {
  const text = loading ? 'Загрузка значения из источника…'
    : error ? 'Источник недоступен — проверьте на вкладке «Данные»'
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

function RequisitesTab({ instance, setId, schemaFields, allDocTypes, docType, otherInstances, onDirty, saveRef, onBaseState, baseControlRef }: {
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
  // Скалярные поля — для per-field привязки «линза» (issue #296, фаза 1): выбор источника на поле +
  // авто-предложение покрыть остальные скалярные поля этого источника.
  const scalarSchemaFields = useMemo(() => schemaFields.filter(f => isScalarField(f) && f.type !== 'file'), [schemaFields]);
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
  // оно резолвится только при генерации. Тот же preview-эндпоинт, что и на вкладке «Данные»,
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
    const constraintViolations: Record<string, string> = {};
    for (const f of schemaFields) {
      const def = getPrimitiveDef(f);
      if (def) {
        const err = validateConstraint(values[f.key], def);
        if (err) constraintViolations[f.key] = err;
      }
    }
    if (Object.keys(constraintViolations).length > 0) {
      setConstraintErrors(constraintViolations);
      setError('Исправьте ошибки формата в полях');
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
                {field.type !== 'boolean' && field.type !== 'complex' && field.type !== 'array' && (
                  <label className="block text-xs font-medium text-fg2 mb-1 pr-5">
                    {field.title}
                    {field.required && <span className="ml-0.5 text-danger">*</span>}
                    {!field.required && <span className="ml-1 text-[10px] text-fg4 font-normal">опц.</span>}
                  </label>
                )}
                {field.type === 'complex' ? (
                  bound ? <SourceBoundDocField /> : (
                  <div>
                    <div className="flex items-center justify-between mb-1">
                      <label className="block text-xs font-medium text-fg2 pr-5">
                        {field.title}
                        {field.required && <span className="ml-0.5 text-danger">*</span>}
                      </label>
                    </div>
                    <ComplexFieldGroup field={field} allDocTypes={allDocTypes} value={raw}
                      onChange={v => setValue(field.key, v)} showValidation={showValidation}
                      setId={setId} otherInstances={otherInstances} docRefMode="instance" />
                  </div>
                  )
                ) : field.type === 'array' ? (
                  bound ? <SourceBoundDocField /> : (
                  <ArrayFieldEditor field={field} allDocTypes={allDocTypes} value={raw}
                    onChange={v => setValue(field.key, v)} showValidation={showValidation}
                    setId={setId} otherInstances={otherInstances} docRefMode="instance" />
                  )
                ) : field.type === 'doc-ref' ? (
                  sourceBoundFields.has(field.key) ? <SourceBoundDocField /> : (
                    <DocRefField field={field} allDocTypes={allDocTypes} value={raw}
                      onChange={v => setValue(field.key, v)} otherInstances={otherInstances} setId={setId} />
                  )
                ) : field.type === 'doc-array' ? (
                  sourceBoundFields.has(field.key) ? <SourceBoundDocField /> : (
                    <DocArrayField field={field} allDocTypes={allDocTypes} value={raw}
                      onChange={v => setValue(field.key, v)} otherInstances={otherInstances} setId={setId} />
                  )
                ) : field.type === 'image' ? (
                  <ImageField value={raw} onChange={v => setValue(field.key, v)} />
                ) : field.type === 'file' ? (
                  <FileField value={raw} onChange={v => setValue(field.key, v)}
                    printForm={field.tags?.includes(FUNCTIONAL_TAG.docPrintForm) ? {
                      setId, instanceId: instance.id, fieldKey: field.key,
                      onMetaUpdated: updates => {
                        setValues(prev => ({ ...prev, ...updates }));
                      },
                    } : undefined} />
                ) : (
                  <PrimitiveInput field={field} value={displayValue}
                    onChange={v => setValue(field.key, v, primitiveDef)}
                    invalid={hasError} primitiveTypeDef={primitiveDef} enumTypeDef={getEnumDef(field)} readOnly={bound} />
                )}
                {boundEmpty && <BoundStateHint loading={previewingBindings} error={hasBindingError} />}
                {missing && <p className="text-xs text-danger mt-1">Обязательное поле</p>}
                {!missing && constraintError && <p className="text-xs text-danger mt-1">{constraintError}</p>}
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
          <AutoFieldsSection count={auto.length}>{fieldGrid(auto)}</AutoFieldsSection>
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
    return tags.includes('profile.construction') || tags.includes('profile.section') || tags.includes('profile.set');
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
              let Icon = Circle, iconCls = 'text-fg4';
              if (showValidation && stats.missing > 0) { Icon = AlertCircle; iconCls = 'text-danger'; }
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

function DiagnosticsPanel({ diagnostics, objectName }: { diagnostics: ResolutionDiagnostic[]; objectName: string }) {
  if (diagnostics.length === 0) {
    return (
      <div className="flex items-center gap-2 p-3 rounded-md text-sm bg-success-subtle text-success">
        <CheckCircle2 size={15} className="shrink-0" /> Проблем разрешения ссылок не найдено
      </div>
    );
  }
  const errors = diagnostics.filter(d => d.severity === 'error');
  const warnings = diagnostics.filter(d => d.severity === 'warning');
  return (
    <div className="rounded-md border border-stroke overflow-hidden">
      <div className="px-3 py-2 text-xs font-medium bg-base text-fg2">
        Диагностика ссылок — объект «{objectName}»: {errors.length} ошиб., {warnings.length} предупр.
      </div>
      <div className="divide-y divide-muted max-h-72 overflow-y-auto">
        {[...errors, ...warnings].map((d, i) => (
          <div key={i} className="flex items-start gap-2 px-3 py-2 text-xs">
            {d.severity === 'error'
              ? <AlertCircle size={14} className="text-danger shrink-0 mt-0.5" />
              : <AlertTriangle size={14} className="text-warning shrink-0 mt-0.5" />}
            <div className="min-w-0">
              <div className="text-fg3">Реквизит: <code className="text-fg2">{d.path}</code></div>
              <p className={d.severity === 'error' ? 'text-danger' : 'text-fg2'}>{d.message}</p>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

function extractDiagnostics(err: unknown): ResolutionDiagnostic[] | null {
  const data = (err as { response?: { data?: { diagnostics?: ResolutionDiagnostic[] } } })?.response?.data;
  return Array.isArray(data?.diagnostics) ? data!.diagnostics! : null;
}

function GenerationTab({ instance, setId }: { instance: DocumentInstance; setId: string }) {
  const { user } = useAuth();
  const isAdmin = user?.role === 'Admin';
  const [emailOpen, setEmailOpen] = useState(false);
  const emailDoc = useEmailDocument();
  const [error, setError] = useState('');
  const [debugBusy, setDebugBusy] = useState(false);
  const [validating, setValidating] = useState(false);
  const [diagnostics, setDiagnostics] = useState<ResolutionDiagnostic[] | null>(null);
  const mutation = useGenerateDocument();
  const setTemplatesMutation = useSetDocumentTemplates();
  const { data: templates = [], isLoading: templatesLoading } = useListTemplates(instance.documentTypeId);
  const activeTemplates = templates.filter((t: Template) => t.isActive);
  const noTemplates = !templatesLoading && activeTemplates.length === 0;
  // Локальный стейт выбора (оптимистичный): функциональный апдейтер копит выбор из ПОСЛЕДНЕГО значения,
  // а не из отрендеренного (иначе быстрый второй клик до рефетча затирал первый — «выбор не сохранялся»).
  const [selectedTemplateIds, setSelectedTemplateIds] = useState<string[]>(() => parseIdArray(instance.templateIds));
  useEffect(() => { setSelectedTemplateIds(parseIdArray(instance.templateIds)); }, [instance.templateIds]);
  // Эффективный шаблон для параметров/дефолт-скачивания: первый выбранный → по умолчанию → первый активный.
  const effectiveTemplate = activeTemplates.find((t: Template) => t.id === selectedTemplateIds[0])
    ?? activeTemplates.find((t: Template) => t.id === instance.templateId)
    ?? activeTemplates.find((t: Template) => t.isDefault)
    ?? activeTemplates[0];
  // Фокус — какой шаблон сейчас «раскрыт» в блоке параметров (ортогонально членству в генерации).
  // По умолчанию — эффективный; держим фокус при переключении галок, если он ещё валиден.
  const [focusedTemplateId, setFocusedTemplateId] = useState<string | null>(null);
  useEffect(() => {
    if (activeTemplates.length === 0) return;
    setFocusedTemplateId(prev =>
      prev && activeTemplates.some((t: Template) => t.id === prev) ? prev : (effectiveTemplate?.id ?? null));
  }, [templates, effectiveTemplate?.id]); // eslint-disable-line react-hooks/exhaustive-deps
  const focusedTemplate = activeTemplates.find((t: Template) => t.id === focusedTemplateId) ?? effectiveTemplate;

  async function handleGenerate() {
    setError('');
    setDiagnostics(null);
    try { await mutation.mutateAsync({ instanceId: instance.id, setId }); }
    catch (err: unknown) {
      const diag = extractDiagnostics(err);
      if (diag) { setDiagnostics(diag); setError('Генерация прервана: ошибки разрешения ссылок'); }
      else setError(err instanceof Error ? err.message : 'Ошибка');
    }
  }

  async function handleValidate() {
    setError('');
    setValidating(true);
    try { setDiagnostics(await validateResolution(instance.id)); }
    catch (err: unknown) { setError(err instanceof Error ? err.message : 'Ошибка'); }
    finally { setValidating(false); }
  }

  async function handleDebugBundle() {
    setError('');
    setDebugBusy(true);
    try { await downloadDebugBundle(instance.id); }
    catch (err: unknown) { setError(err instanceof Error ? err.message : 'Ошибка'); }
    finally { setDebugBusy(false); }
  }

  function toggleTemplate(id: string, on: boolean) {
    setSelectedTemplateIds(prev => {
      const next = on ? [...new Set([...prev, id])] : prev.filter(x => x !== id);
      setTemplatesMutation.mutate({ setId, instanceId: instance.id, templateIds: next });
      return next;
    });
  }

  const pdfFiles = instance.generatedFiles.filter(f => f.format === 'Pdf');
  // Идёт генерация (файлы перезаписываются) — блокируем открытие/скачивание, чтобы не попасть на
  // заменяемую/устаревшую версию (ссылка на «старый» файл → пустая страница до обновления).
  const busy = mutation.isPending || instance.status === 'Generating';

  return (
    <div className="space-y-5">
      <div className={`p-3 rounded-md text-sm ${STATUS_COLORS[instance.status] ?? 'bg-base text-fg2'}`}>
        Статус: <strong>{STATUS_LABELS[instance.status] ?? instance.status}</strong>
        {instance.status === 'Generating' && <Loader2 size={14} className="inline-block ml-2 animate-spin" />}
      </div>

      <div className="space-y-1">
        <label className="block text-xs font-medium text-fg2">Шаблоны <span className="text-fg4 font-normal">(можно несколько — по PDF на каждый)</span></label>
        {noTemplates ? (
          <p className="text-xs text-warning">
            Для этого типа документа нет шаблонов. Создайте шаблон в разделе «Шаблоны».
          </p>
        ) : (
          <>
            {/* Клик по ВСЕЙ строке (обёртка-label) переключает участие в генерации (issue #316);
                фокус (показ параметров ниже) следует за кликом через onChange. Галочка отражает участие,
                подсветка строки — фокус. */}
            <div className="rounded-md border border-stroke-strong divide-y divide-stroke overflow-hidden">
              {activeTemplates.map((t: Template) => {
                const selected = selectedTemplateIds.includes(t.id);
                const focused = focusedTemplate?.id === t.id;
                return (
                  <label key={t.id}
                    className={`flex items-center gap-2 pr-2.5 text-sm border-l-2 transition-colors cursor-pointer ${focused ? 'bg-brand-subtle border-brand' : 'border-transparent hover:bg-base'}`}>
                    <input type="checkbox" checked={selected} disabled={setTemplatesMutation.isPending}
                      onChange={e => { toggleTemplate(t.id, e.target.checked); setFocusedTemplateId(t.id); }}
                      aria-label={`Использовать шаблон «${t.name}» для генерации`}
                      className="ml-2.5 shrink-0" />
                    <span className="flex-1 min-w-0 py-1.5">
                      <span className={`truncate ${focused ? 'text-brand-hover font-medium' : 'text-fg1'}`}>
                        {t.isDefault ? '★ ' : ''}{t.name} <span className="text-fg4 font-normal">(v{t.version})</span>
                      </span>
                    </span>
                  </label>
                );
              })}
            </div>
            {selectedTemplateIds.length === 0 && (
              <p className="text-[11px] text-fg4">Ничего не выбрано — будет один PDF по шаблону по умолчанию.</p>
            )}
          </>
        )}
      </div>

      {focusedTemplate && (
        <DocumentTemplateParams setId={setId} instance={instance} template={focusedTemplate}
          participating={selectedTemplateIds.length === 0 || selectedTemplateIds.includes(focusedTemplate.id)} />
      )}

      <div className="flex gap-3">
        <Button variant="filled" onClick={() => handleGenerate()} disabled={noTemplates}
          loading={mutation.isPending} icon={<FileText size={14} />}>
          Сгенерировать PDF
        </Button>
        <Button variant="outlined" onClick={handleValidate} loading={validating}
          title="Проверить разрешение ссылок (каталог, наборы данных) без генерации"
          icon={<ShieldCheck size={14} />}>
          Проверить ссылки
        </Button>
        <Button variant="outlined" onClick={handleDebugBundle} disabled={debugBusy || noTemplates}
          loading={debugBusy}
          title="Скачать ZIP с template.typ, data.json, typeblocks.typ и userlib.typ для отладки во внешнем инструменте (typst compile template.typ)"
          icon={<Bug size={14} />}>
          Отладочный пакет
        </Button>
      </div>
      {(mutation.isPending || instance.status === 'Generating') && (
        <p className="flex items-center gap-2 text-xs text-fg4">
          <Loader2 size={12} className="animate-spin shrink-0" />
          Генерация PDF может занять несколько секунд — идёт сбор данных и компиляция Typst.
        </p>
      )}
      {error && <p className="text-sm text-danger">{error}</p>}

      {diagnostics && <DiagnosticsPanel diagnostics={diagnostics} objectName={instance.name || 'без названия'} />}
      {pdfFiles.length > 0 && (
        <div className="space-y-2">
          <div className="flex items-center justify-between">
            <p className="text-xs font-medium text-fg2">Сгенерированные файлы</p>
            {isAdmin && (
              <button onClick={() => setEmailOpen(true)}
                className="flex items-center gap-1.5 text-xs px-2.5 py-1.5 border border-stroke rounded-md hover:bg-base transition-colors"
                title="Отправить сгенерированные PDF документа по почте (подписчикам и/или на внешние адреса)">
                <Mail size={13} className="text-brand" /> Отправить по почте
              </button>
            )}
          </div>
          <div className="space-y-1.5">
            {pdfFiles.map(f => {
              const tpl = templates.find((t: Template) => t.id === f.templateId);
              return (
                <div key={f.id} className="flex items-center gap-2">
                  <span className="text-xs text-fg2 flex-1 min-w-0 truncate" title={tpl?.name}>{tpl ? tpl.name : 'PDF'}</span>
                  {/* Во время генерации файл перезаписывается — блокируем открытие/скачивание, чтобы
                      не открыть неактуальную/заменяемую версию (ссылка ведёт на старое состояние). */}
                  <button onClick={() => previewGeneratedFile(instance.id, f.templateId)} disabled={busy}
                    className="flex items-center gap-1 px-2.5 py-1.5 text-xs border border-stroke rounded-md hover:bg-base disabled:opacity-40 disabled:pointer-events-none">
                    <Eye size={13} className="text-brand" /> Открыть
                  </button>
                  <button onClick={() => downloadGeneratedFile(instance.id, f.templateId)} disabled={busy}
                    className="flex items-center gap-1 px-2.5 py-1.5 text-xs border border-stroke rounded-md hover:bg-base disabled:opacity-40 disabled:pointer-events-none">
                    <Download size={13} className="text-brand" /> Скачать
                  </button>
                </div>
              );
            })}
          </div>
        </div>
      )}

      {isAdmin && (
        <EmailSendDialog open={emailOpen} onClose={() => setEmailOpen(false)}
          setId={setId} itemName={`Документ «${instance.name || 'документ'}»`}
          defaultSubjectHint={`Исполнительная документация — ${instance.name || 'документ'}`}
          defaultBodyHint={`Направляем документ «${instance.name || 'документ'}» исполнительной документации.`}
          ready={pdfFiles.length > 0} notReadyHint="У документа нет сгенерированных PDF — сначала сгенерируйте."
          onSend={(to, subject, body) => emailDoc.mutateAsync({ setId, instanceId: instance.id, to, subject, body })} />
      )}
    </div>
  );
}

// ─── DataSets tab ─────────────────────────────────────────────────────────────


// ─── Instance name editor ─────────────────────────────────────────────────────

function InstanceNameEditor({ instance, setId, docType }: {
  instance: DocumentInstance; setId: string; docType: DocumentType | undefined;
}) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState('');
  const rename = useRenameDocumentInstance();

  function start() { setDraft(instance.name ?? ''); setEditing(true); }
  function save() {
    const trimmed = draft.trim();
    if (trimmed !== (instance.name ?? '')) {
      rename.mutate({ setId, instanceId: instance.id, name: trimmed });
    }
    setEditing(false);
  }

  if (editing) {
    return (
      <input
        value={draft}
        onChange={e => setDraft(e.target.value)}
        onBlur={save}
        onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); save(); } if (e.key === 'Escape') setEditing(false); }}
        autoFocus
        placeholder={docType?.name ?? 'Название документа'}
        className="text-sm font-medium text-fg1 bg-transparent border-b border-brand outline-none w-full min-w-0"
      />
    );
  }

  return (
    <button type="button" onClick={start} className="flex items-center gap-1.5 group/name text-left min-w-0 max-w-full">
      <span className={`text-sm font-medium truncate ${instance.name ? 'text-fg1' : 'text-fg3'}`}>
        {instance.name || docType?.name || 'Документ'}
      </span>
      <Pencil size={12} className="text-fg4 shrink-0 opacity-0 group-hover/name:opacity-100 transition-opacity" />
    </button>
  );
}

// ─── Instance editor modal ────────────────────────────────────────────────────

type InstanceTab = 'requisites' | 'quality' | 'generation';

export function InstanceEditor({ instance, setId, docType, allDocTypes, otherInstances, onClose, onDirtyChange, requestClose }: {
  instance: DocumentInstance; setId: string; docType: DocumentType | undefined;
  allDocTypes: DocumentType[]; otherInstances: DocumentInstance[]; onClose: () => void;
  onDirtyChange?: (dirty: boolean) => void;
  /** Закрытие с guard несохранённых изменений (крестик top app bar). */
  requestClose?: () => void;
}) {
  const schemaFields = docType ? resolveEffectiveFields(docType, allDocTypes) : [];
  const [tab, setTab] = useState<InstanceTab>('requisites');
  const [dataSourcesOpen, setDataSourcesOpen] = useState(false);
  const [dirty, setDirty] = useState(false);
  const [pendingTab, setPendingTab] = useState<InstanceTab | null>(null);
  const [switching, setSwitching] = useState(false);
  const [saving, setSaving] = useState(false);
  const [savedFlash, setSavedFlash] = useState(false);
  // Актуальная функция сохранения активной редактируемой вкладки.
  const saveRef = useRef<(() => Promise<boolean>) | null>(null);
  // «Основа» (issue #223): состояние-зеркало базы для chip шапки — источник правды `_baseRef` живёт в
  // `values` внутри RequisitesTab, сюда синкается для отрисовки. Управление — через baseControlRef
  // (доступно только пока вкладка реквизитов смонтирована).
  const [baseState, setBaseState] = useState<{
    hasBase: boolean; selected: BaseCandidate | undefined; missing: boolean;
    candidates: BaseCandidate[]; coveredCount: number;
  } | null>(null);
  const baseControlRef = useRef<{ select: (c: BaseCandidate) => void; clear: () => void } | null>(null);

  // Редактируемые вкладки (есть что сохранять на уровне документа).
  const editable = tab === 'requisites';
  async function doSave(): Promise<boolean> {
    if (!saveRef.current) return true; // на этой вкладке нечего сохранять
    setSaving(true);
    try {
      const ok = await saveRef.current();
      if (ok) { setSavedFlash(true); setTimeout(() => setSavedFlash(false), 2000); }
      return ok;
    } finally { setSaving(false); }
  }
  async function doSaveAndClose() {
    if (editable) { if (await doSave()) onClose(); }
    else onClose();
  }

  // Прокидываем «грязное» состояние наверх — для защиты от закрытия модалки (X/Esc/клик вне).
  useEffect(() => { onDirtyChange?.(dirty); }, [dirty, onDirtyChange]);

  // Вкладка «Документы качества» — только для типов, требующих их (есть материал-массив
  // с полем-ссылкой на документ качества, тэг material.qualityDocLink).
  const requiresQuality = !!docType && compositeFieldHasTag(docType, FUNCTIONAL_TAG.materialQualityDocLink, allDocTypes);
  const tabs: [InstanceTab, string][] = [
    ['requisites', 'Реквизиты'],
    ...(requiresQuality ? [['quality', 'Документы качества'] as [InstanceTab, string]] : []),
    ['generation', 'Генерация'],
  ];

  const tabRefs = useRef<(HTMLButtonElement | null)[]>([]);
  function requestTab(next: InstanceTab) {
    if (next === tab) return;
    if (dirty) setPendingTab(next);   // есть несохранённые изменения — спрашиваем
    else setTab(next);
  }
  // APG-tablist (issue #107 F3): стрелки/Home/End двигают ФОКУС между вкладками (manual
  // activation — переключение по Enter/Space через onClick, чтобы dirty-guard не срабатывал на скролле).
  function onTabKey(e: React.KeyboardEvent, i: number) {
    let ni = -1;
    if (e.key === 'ArrowRight') ni = (i + 1) % tabs.length;
    else if (e.key === 'ArrowLeft') ni = (i - 1 + tabs.length) % tabs.length;
    else if (e.key === 'Home') ni = 0;
    else if (e.key === 'End') ni = tabs.length - 1;
    else return;
    e.preventDefault();
    tabRefs.current[ni]?.focus();
  }
  function switchTo(next: InstanceTab) {
    setDirty(false);
    setTab(next);
    setPendingTab(null);
  }
  async function saveThenSwitch() {
    if (!pendingTab) return;
    setSwitching(true);
    try {
      const ok = await saveRef.current?.();
      if (ok) switchTo(pendingTab);   // успех → переходим, редактор НЕ закрываем
      else setPendingTab(null);       // ошибка валидации → закрываем диалог, чтобы её было видно
    } finally { setSwitching(false); }
  }

  return (
    <div className="flex flex-col min-h-0 flex-1"
      onKeyDown={e => {
        // Ctrl/⌘+Enter — сохранить и закрыть (issue #107 F7); работает из любого поля вкладки реквизитов.
        if ((e.ctrlKey || e.metaKey) && e.key === 'Enter' && editable && !saving) {
          e.preventDefault();
          void doSaveAndClose();
        }
      }}>
      {/* MD3 top app bar: крестик слева, имя+подзаголовок, статус, действия справа */}
      <div className="shrink-0 bg-surface">
        <div className="flex items-center gap-3 h-16 px-3 sm:px-4">
          <button type="button" onClick={() => (requestClose ?? onClose)()} aria-label="Закрыть"
            className="flex items-center justify-center w-11 h-11 shrink-0 rounded-full text-fg3 hover:text-fg1 hover:bg-black/5 dark:hover:bg-white/10 transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand">
            <X size={20} />
          </button>
          <div className="flex-1 min-w-0">
            <InstanceNameEditor instance={instance} setId={setId} docType={docType} />
            <p className="text-xs text-fg4 mt-0.5 truncate">
              {docType?.name ? `${docType.name} · Редактирование` : 'Редактирование'}
              {baseState?.selected && baseState.coveredCount > 0 &&
                ` · ${ruCount(baseState.coveredCount, 'поле', 'поля', 'полей')} из основы`}
            </p>
          </div>
          {baseState?.hasBase && (
            <BaseInstanceChip
              selected={baseState.selected} missing={baseState.missing} candidates={baseState.candidates}
              editable={tab === 'requisites'}
              onSelect={c => baseControlRef.current?.select(c)}
              onClear={() => baseControlRef.current?.clear()} />
          )}
          <span className={`text-xs px-2 py-0.5 rounded-full font-medium shrink-0 ${STATUS_COLORS[instance.status] ?? 'bg-brand-subtle text-brand'}`}>
            {STATUS_LABELS[instance.status] ?? instance.status}
          </span>
          {/* Источники данных (issue #296, фаза 3): пакетные операции уровня документа — обзор
              привязок, «Проверить данные», «Из шаблона». Точечная привязка — на самих полях. */}
          <Button variant="text" size="sm" icon={<Database size={15} />} onClick={() => setDataSourcesOpen(true)}
            className="shrink-0" title="Обзор привязок, проверка данных, шаблоны">
            <span className="hidden sm:inline">Источники</span>
          </Button>
          {editable && (
            <div className="flex items-center gap-2 shrink-0">
              {savedFlash && <span className="text-sm text-success hidden sm:inline">Сохранено</span>}
              <Button variant="text" onClick={() => void doSave()} disabled={saving}>
                {saving ? 'Сохранение…' : 'Сохранить'}
              </Button>
              <Button variant="filled" onClick={() => void doSaveAndClose()} loading={saving} title="Ctrl+Enter">
                Сохранить и закрыть
              </Button>
            </div>
          )}
        </div>
        <div role="tablist" aria-label="Разделы документа" className="flex border-b border-stroke gap-0 px-3 sm:px-4">
          {tabs.map(([key, label], i) => (
            <button key={key} role="tab" aria-selected={tab === key} tabIndex={tab === key ? 0 : -1}
              ref={el => { tabRefs.current[i] = el; }}
              onClick={() => requestTab(key)} onKeyDown={e => onTabKey(e, i)}
              className={`h-12 px-4 text-sm font-medium border-b-2 transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-brand ${
                tab === key ? 'border-brand text-brand' : 'border-transparent text-fg3 hover:text-fg1'}`}>
              {label}{key === tab && dirty && <span className="ml-1 text-warning" title="Есть несохранённые изменения">•</span>}
            </button>
          ))}
        </div>
      </div>
      {tab === 'requisites' && (
        <RequisitesTab instance={instance} setId={setId} schemaFields={schemaFields}
          allDocTypes={allDocTypes} docType={docType} otherInstances={otherInstances}
          onClose={onClose} onDirty={setDirty} saveRef={saveRef}
          onBaseState={setBaseState} baseControlRef={baseControlRef} />
      )}
      <Modal open={dataSourcesOpen} onOpenChange={setDataSourcesOpen} title="Источники данных" wide>
        {dataSourcesOpen && (
          <div className="space-y-3">
            <p className="text-xs text-fg4">
              Точечная привязка полей — по иконке источника у каждого поля в реквизитах.
              Здесь — обзор привязок, проверка данных и применение шаблонов на весь документ.
            </p>
            <DataSetsTab instance={instance} setId={setId} schemaFields={schemaFields} allDocTypes={allDocTypes} docType={docType} />
          </div>
        )}
      </Modal>
      {tab === 'quality' && (
        <div className="flex-1 min-h-0 overflow-y-auto px-6 py-4">
          <QualityLinksTab instance={instance} setId={setId} allDocTypes={allDocTypes} />
        </div>
      )}
      {tab === 'generation' && (
        <div className="flex-1 min-h-0 overflow-y-auto px-6 py-4">
          <GenerationTab instance={instance} setId={setId} />
        </div>
      )}

      {pendingTab && (
        <Modal
          open
          onOpenChange={o => { if (!o && !switching) setPendingTab(null); }}
          title="Документ не сохранён"
          footer={
            <div className="flex gap-2 justify-end flex-wrap">
              <Button variant="text" size="sm" onClick={() => setPendingTab(null)} disabled={switching}>
                Отмена
              </Button>
              <Button variant="text" size="sm" danger onClick={() => switchTo(pendingTab)} disabled={switching}>
                Не сохранять
              </Button>
              <Button variant="filled" size="sm" onClick={saveThenSwitch} loading={switching}>
                {switching ? 'Сохранение...' : 'Сохранить'}
              </Button>
            </div>
          }>
          <p className="text-xs text-fg3">
            На текущей вкладке есть несохранённые изменения. Сохранить перед переходом?
            Иначе они будут потеряны.
          </p>
        </Modal>
      )}
    </div>
  );
}
