import { useState, useEffect, useRef, useMemo } from 'react';
import { Loader2, FileText, Download, Eye, Pencil, ChevronDown, ChevronUp, Bug, ShieldCheck, AlertTriangle, AlertCircle, CheckCircle2, Mail, Database, Link2, Unlink } from 'lucide-react';
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
import type { DocumentInstance, DocumentType, Template, PrimitiveTypeDef, EnumTypeDef } from '@/shared/api/types';
import {
  groupEffectiveFields, resolveEffectiveFields, compositeFieldHasTag, parseSchemaFields, type SchemaField,
} from '@/shared/api/schema';
import {
  STATUS_LABELS, STATUS_COLORS,
  validateConstraint, isMissing, PrimitiveInput, FileField, ImageField,
  DocRefField, DocArrayField, ArrayFieldEditor, ComplexFieldGroup,
} from '../fields';
import { DataSetsTab } from './DataSetsTab';
import { useListDataSetBindings, usePreviewDataSetBindings } from '@/shared/api/datasets';
import { mergeBindingPreviewsIntoValues } from '@/shared/api/datasetHelpers';
import { QualityLinksTab } from './QualityLinksTab';
import { DocumentTemplateParams } from './DocumentTemplateParams';
import { Modal } from '@/shared/ui/Modal';

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
        Заполняется из привязанного источника данных — правьте связку на вкладке «Данные».
      </span>
    </div>
  );
}

/// Компактный индикатор для скалярного поля, заполняемого привязкой (issue #55): иконка в строке
/// лейбла (тот же слот, что занимает тип-хинт) вместо полноразмерной плашки — иначе при «один
/// биндинг → много полей» форма превращается в стену одинаковых боксов.
function SourceBoundBadge({ onGoToDataTab }: { onGoToDataTab: () => void }) {
  return (
    <button type="button" onClick={onGoToDataTab}
      title="Заполняется из привязанного источника данных — открыть вкладку «Данные»"
      className="ml-1 inline-flex align-middle text-brand hover:text-brand-hover">
      <Database size={11} />
    </button>
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

/// Пикер базового экземпляра (issue #71) — порт CatalogBaseEntryPicker для документов комплекта:
/// выбор документа родительского типа из ТОГО ЖЕ комплекта (кандидаты уже под рукой в otherInstances).
function BaseInstancePicker({ open, onOpenChange, parentType, candidates, onSelect }: {
  open: boolean; onOpenChange: (o: boolean) => void;
  parentType: DocumentType; candidates: DocumentInstance[];
  onSelect: (inst: DocumentInstance) => void;
}) {
  const [search, setSearch] = useState('');
  const filtered = candidates.filter(i => (i.name ?? '').toLowerCase().includes(search.toLowerCase()));
  return (
    <Modal open={open} onOpenChange={onOpenChange} title={`Базовый экземпляр: ${parentType.name}`}>
      <div className="space-y-4">
        <input value={search} onChange={e => setSearch(e.target.value)} placeholder="Поиск..." autoFocus
          className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface" />
        {filtered.length === 0 ? (
          <p className="text-sm text-fg4 text-center py-4">
            Нет документов типа «{parentType.name}» в комплекте.
          </p>
        ) : (
          <div className="space-y-1 max-h-72 overflow-y-auto">
            {filtered.map(inst => (
              <button key={inst.id} type="button" onClick={() => { onSelect(inst); onOpenChange(false); }}
                className="w-full flex items-center gap-3 px-3 py-2 text-sm text-left rounded-md hover:bg-brand-subtle transition-colors">
                <Link2 size={13} className="text-brand shrink-0" />
                <span className="flex-1 font-medium text-fg1 truncate">{inst.name ?? '(без имени)'}</span>
              </button>
            ))}
          </div>
        )}
      </div>
    </Modal>
  );
}

function RequisitesTab({ instance, setId, schemaFields, allDocTypes, docType, otherInstances, onDirty, saveRef, onGoToDataTab }: {
  instance: DocumentInstance; setId: string; schemaFields: SchemaField[];
  allDocTypes: DocumentType[]; docType: DocumentType | undefined;
  otherInstances: DocumentInstance[]; onClose: () => void;
  onDirty: (dirty: boolean) => void; saveRef: SaveRef; onGoToDataTab: () => void;
}) {
  const { data: primitiveTypes = [] } = useListPrimitiveTypes();
  const { data: enumTypes = [] } = useListEnumTypes();
  const [values, setValues] = useState<Record<string, unknown>>(() => ({ ...instance.requisites }));
  const [constraintErrors, setConstraintErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState('');
  const [showValidation, setShowValidation] = useState(false);
  const [expandedGroups, setExpandedGroups] = useState<Set<string>>(new Set());
  const mutation = useUpdateRequisites();

  // Поля, заполняемые привязкой к набору данных при генерации — источник перезаписывает их,
  // поэтому ручной ввод отключаем и не требуем от формы реквизитов (issue #55):
  // табличные (targetFieldKey, issue #17) + скалярные (ключи мэппинга, issue #55). Для скалярной
  // привязки эффективный маппинг — собственный (binding.mapping), а если он пуст — с материализации
  // источника (binding.source.materializeMapping, issue #19), см. DataSetMappingValue.EffectiveMappingJson.
  const { data: dsBindings = [] } = useListDataSetBindings({ instanceId: instance.id });
  const sourceBoundFields = useMemo(() => {
    const s = new Set<string>();
    for (const b of dsBindings) {
      if (b.targetFieldKey) { s.add(b.targetFieldKey); continue; }
      const effectiveMapping = Object.keys(b.mapping).length > 0 ? b.mapping : (b.source?.materializeMapping ?? {});
      for (const key of Object.keys(effectiveMapping)) s.add(key);
    }
    return s;
  }, [dsBindings]);
  // Базовый экземпляр (issue #71): документ дочернего типа можно связать с документом РОДИТЕЛЬСКОГО
  // типа в том же комплекте — при связке наследуются его реквизиты (мердж при генерации), а вручную
  // заполняются только собственные поля дочернего типа. Ссылка хранится как `_baseRef` в реквизитах.
  const [basePickerOpen, setBasePickerOpen] = useState(false);
  const parentType = docType?.parentId ? allDocTypes.find(dt => dt.id === docType.parentId) ?? null : null;
  const baseRefId = typeof values._baseRef === 'string' ? values._baseRef : undefined;
  const baseCandidates = parentType ? otherInstances.filter(i => i.documentTypeId === parentType.id) : [];
  const baseInstance = baseRefId ? baseCandidates.find(i => i.id === baseRefId) : undefined;
  const ownFields = docType ? parseSchemaFields(docType.schema) : schemaFields;
  const displayFields = (parentType && baseRefId) ? ownFields : schemaFields;
  // Поля, покрытые базовым экземпляром, не требуются к заполнению здесь — придут наследованием при
  // генерации (тот же класс, что sourceBoundFields из #55).
  const baseCoveredFields = useMemo(
    () => new Set(baseInstance ? Object.keys(baseInstance.requisites) : []),
    [baseInstance]);
  function clearBaseRef() {
    setValues(p => { const n = { ...p }; delete n._baseRef; return n; });
    onDirty(true);
  }

  // Обязательное поле, покрытое активной привязкой ИЛИ базовым экземпляром, не блокирует сохранение
  // реквизитов — значение подставится при генерации, форма его не хранит.
  const isFieldMissing = (f: SchemaField, val: unknown) =>
    isMissing(f, val) && !sourceBoundFields.has(f.key) && !baseCoveredFields.has(f.key);

  // Предпросмотр значений привязок (issue #67): скалярный биндинг не пишет значение в реквизиты —
  // оно резолвится только при генерации. Тот же preview-эндпоинт, что и на вкладке «Данные»,
  // даёт резолвнутое значение для показа read-only прямо в поле (в saved-values НЕ пишем).
  const { data: bindingPreviews, isFetching: previewingBindings, refetch: runBindingPreview, error: previewError } =
    usePreviewDataSetBindings({ instanceId: instance.id });
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
  function toggleGroup(key: string) {
    setExpandedGroups(prev => {
      const next = new Set(prev);
      next.has(key) ? next.delete(key) : next.add(key);
      return next;
    });
  }

  // Сохраняет реквизиты. Возвращает true при успехе. НЕ закрывает редактор.
  async function handleSaveCore(): Promise<boolean> {
    setError('');
    const missingRequired = schemaFields.filter(f => isFieldMissing(f, values[f.key]));
    if (missingRequired.length > 0) {
      setShowValidation(true);
      setError(`Заполните обязательные поля: ${missingRequired.map(f => f.title).join(', ')}`);
      return false;
    }
    // Re-validate all primitive-type fields
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

  function renderFields(fields: SchemaField[]) {
    const isWide = (f: SchemaField) =>
      f.type === 'complex' || f.type === 'array' || f.type === 'doc-ref' ||
      f.type === 'doc-array' || f.type === 'image' || f.type === 'file' || f.type === 'text';

    return (
      <div className="grid grid-cols-2 gap-x-4 gap-y-2.5">
        {fields.map(field => {
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
            return (
              <div key={field.key} className="col-span-2">
                {field.type !== 'boolean' && field.type !== 'complex' && field.type !== 'array' && (
                  <label className="block text-[13px] font-medium text-fg2 mb-0.5 leading-tight">
                    {field.title}
                    {field.required && <span className="ml-0.5 text-danger">*</span>}
                    {!field.required && <span className="ml-1 text-[10px] text-fg4 font-normal">опц.</span>}
                    {bound && <SourceBoundBadge onGoToDataTab={onGoToDataTab} />}
                  </label>
                )}
                {field.type === 'complex' ? (
                  <div>
                    <div className="flex items-center justify-between mb-0.5">
                      <label className="block text-[13px] font-medium text-fg2 leading-tight">
                        {field.title}
                        {field.required && <span className="ml-0.5 text-danger">*</span>}
                      </label>
                    </div>
                    <ComplexFieldGroup field={field} allDocTypes={allDocTypes} value={raw}
                      onChange={v => setValue(field.key, v)} showValidation={showValidation}
                      setId={setId} otherInstances={otherInstances} docRefMode="instance" />
                  </div>
                ) : field.type === 'array' ? (
                  <ArrayFieldEditor field={field} allDocTypes={allDocTypes} value={raw}
                    onChange={v => setValue(field.key, v)} showValidation={showValidation}
                    setId={setId} otherInstances={otherInstances} docRefMode="instance" />
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

          // Label-above layout for simple fields (string, number, date, enum, boolean, primitive)
          return (
            <div key={field.key} className="col-span-1 min-w-0">
              <label className="block text-[13px] font-medium text-fg2 mb-0.5 leading-tight">
                {field.title}
                {field.required && <span className="ml-0.5 text-danger">*</span>}
                {primitiveDef && (
                  <span className="ml-1 text-[10px] text-fg4 font-normal">· {primitiveDef.name}</span>
                )}
                {bound && <SourceBoundBadge onGoToDataTab={onGoToDataTab} />}
              </label>
              <PrimitiveInput field={field} value={displayValue}
                onChange={v => setValue(field.key, v, primitiveDef)}
                invalid={hasError} primitiveTypeDef={primitiveDef} enumTypeDef={getEnumDef(field)} readOnly={bound} />
              {boundEmpty && <BoundStateHint loading={previewingBindings} error={hasBindingError} />}
              {missing && <p className="text-[11px] text-danger mt-0.5">Обязательное поле</p>}
              {!missing && constraintError && <p className="text-[11px] text-danger mt-0.5">{constraintError}</p>}
            </div>
          );
        })}
      </div>
    );
  }

  return (
    <div className="flex flex-col min-h-0 flex-1">
      <div className="flex-1 min-h-0 overflow-y-auto px-6 py-4 space-y-4">
      {parentType && (
        <div className="rounded-lg border border-stroke p-3 space-y-2">
          <p className="text-xs font-semibold text-fg3 uppercase tracking-wide">
            Базовый экземпляр
            <span className="normal-case font-normal ml-1 text-fg4">({parentType.name})</span>
          </p>
          {baseRefId && baseInstance ? (
            <div className="flex items-center gap-2 rounded-md border border-brand-subtle bg-brand-subtle px-3 py-2">
              <Link2 size={14} className="text-brand shrink-0" />
              <span className="flex-1 text-sm font-medium text-brand-hover truncate">{baseInstance.name ?? '(без имени)'}</span>
              <button type="button" onClick={clearBaseRef}
                className="text-brand hover:text-danger transition-colors" title="Снять ссылку">
                <Unlink size={13} />
              </button>
            </div>
          ) : baseRefId && !baseInstance ? (
            <div className="flex items-center gap-2 rounded-md border border-warning/40 bg-warning/5 px-3 py-2">
              <span className="flex-1 text-sm text-warning truncate">Базовый экземпляр не найден в комплекте</span>
              <button type="button" onClick={clearBaseRef}
                className="text-brand hover:text-danger transition-colors" title="Снять ссылку">
                <Unlink size={13} />
              </button>
            </div>
          ) : (
            <button type="button" onClick={() => setBasePickerOpen(true)}
              className="flex items-center gap-2 text-sm text-brand hover:text-brand-hover border border-dashed border-brand-subtle rounded-md px-3 py-2 w-full hover:bg-brand-subtle transition-colors">
              <Link2 size={14} />
              Выбрать из «{parentType.name}»...
            </button>
          )}
          {!baseRefId && ownFields.length < schemaFields.length && (
            <p className="text-xs text-fg4">
              Без базового экземпляра все {schemaFields.length} полей заполняются вручную.
            </p>
          )}
          <BaseInstancePicker
            open={basePickerOpen}
            onOpenChange={setBasePickerOpen}
            parentType={parentType}
            candidates={baseCandidates}
            onSelect={inst => setValue('_baseRef', inst.id)}
          />
        </div>
      )}
      {sections.map(section => {
        if (!section.title) {
          // Ungrouped fields — always visible, no header
          return <div key={section.key}>{renderFields(section.fields)}</div>;
        }
        const isExpanded = expandedGroups.has(section.key);
        const hasMissing = showValidation && section.fields.some(f => isFieldMissing(f, values[f.key]));
        return (
          <div key={section.key} className="border border-stroke rounded-lg overflow-hidden">
            <button type="button"
              onClick={() => toggleGroup(section.key)}
              className="w-full flex items-center gap-2 px-3 py-2.5 bg-base hover:bg-muted transition-colors text-left">
              {isExpanded ? <ChevronUp size={13} className="text-fg4 shrink-0" /> : <ChevronDown size={13} className="text-fg4 shrink-0" />}
              <span className="text-xs font-semibold uppercase tracking-wide text-fg2 flex-1">
                {section.title}
              </span>
              <span className="text-xs text-fg4">{section.fields.length} п.</span>
              {hasMissing && (
                <span className="text-xs text-danger font-medium">! не заполнено</span>
              )}
            </button>
            {isExpanded && (
              <div className="px-3 py-3">
                {renderFields(section.fields)}
              </div>
            )}
          </div>
        );
      })}
      </div>
      {error && (
        <div className="shrink-0 px-6 py-2 bg-surface border-t border-stroke">
          <p className="text-sm text-danger">{error}</p>
        </div>
      )}
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
            {/* Чекбокс = участие в генерации; клик по строке = фокус (показ параметров ниже). Это две
                разные оси: строка подсвечивается языком активной строки, галочка отвечает только за то,
                какие шаблоны дадут PDF. Обёртка не <label> — иначе клик по строке попадал бы в чекбокс. */}
            <div className="rounded-md border border-stroke-strong divide-y divide-stroke overflow-hidden">
              {activeTemplates.map((t: Template) => {
                const selected = selectedTemplateIds.includes(t.id);
                const focused = focusedTemplate?.id === t.id;
                return (
                  <div key={t.id}
                    className={`flex items-center gap-2 pr-2.5 text-sm border-l-2 transition-colors ${focused ? 'bg-brand-subtle border-brand' : 'border-transparent hover:bg-base'}`}>
                    <input type="checkbox" checked={selected} disabled={setTemplatesMutation.isPending}
                      onChange={e => toggleTemplate(t.id, e.target.checked)}
                      aria-label={`Использовать шаблон «${t.name}» для генерации`}
                      className="ml-2.5 shrink-0" />
                    <button type="button" onClick={() => setFocusedTemplateId(t.id)} aria-pressed={focused}
                      title="Показать параметры этого шаблона"
                      className="flex-1 min-w-0 text-left py-1.5 outline-none focus-visible:ring-2 focus-visible:ring-brand rounded">
                      <span className={`truncate ${focused ? 'text-brand-hover font-medium' : 'text-fg1'}`}>
                        {t.isDefault ? '★ ' : ''}{t.name} <span className="text-fg4 font-normal">(v{t.version})</span>
                      </span>
                    </button>
                  </div>
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
        <button onClick={() => handleGenerate()} disabled={mutation.isPending || noTemplates}
          className="flex items-center gap-2 px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md transition-colors disabled:opacity-50">
          {mutation.isPending ? <Loader2 size={14} className="animate-spin" /> : <FileText size={14} />}
          Сгенерировать PDF
        </button>
        <button onClick={handleValidate} disabled={validating}
          title="Проверить разрешение ссылок (каталог, наборы данных) без генерации"
          className="flex items-center gap-2 px-4 py-2 text-sm border border-stroke rounded-md hover:bg-base transition-colors disabled:opacity-50">
          {validating ? <Loader2 size={14} className="animate-spin" /> : <ShieldCheck size={14} className="text-fg3" />}
          Проверить ссылки
        </button>
        <button onClick={handleDebugBundle} disabled={debugBusy || noTemplates}
          title="Скачать ZIP с template.typ, data.json, typeblocks.typ и userlib.typ для отладки во внешнем инструменте (typst compile template.typ)"
          className="flex items-center gap-2 px-4 py-2 text-sm border border-stroke rounded-md hover:bg-base transition-colors disabled:opacity-50">
          {debugBusy ? <Loader2 size={14} className="animate-spin" /> : <Bug size={14} className="text-fg3" />}
          Отладочный пакет
        </button>
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

type InstanceTab = 'requisites' | 'datasets' | 'quality' | 'generation';

export function InstanceEditor({ instance, setId, docType, allDocTypes, otherInstances, onClose, onDirtyChange }: {
  instance: DocumentInstance; setId: string; docType: DocumentType | undefined;
  allDocTypes: DocumentType[]; otherInstances: DocumentInstance[]; onClose: () => void;
  onDirtyChange?: (dirty: boolean) => void;
}) {
  const schemaFields = docType ? resolveEffectiveFields(docType, allDocTypes) : [];
  const [tab, setTab] = useState<InstanceTab>('requisites');
  const [dirty, setDirty] = useState(false);
  const [pendingTab, setPendingTab] = useState<InstanceTab | null>(null);
  const [switching, setSwitching] = useState(false);
  const [saving, setSaving] = useState(false);
  const [savedFlash, setSavedFlash] = useState(false);
  // Актуальная функция сохранения активной редактируемой вкладки.
  const saveRef = useRef<(() => Promise<boolean>) | null>(null);

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
    ['requisites', 'Реквизиты'], ['datasets', 'Данные'],
    ...(requiresQuality ? [['quality', 'Документы качества'] as [InstanceTab, string]] : []),
    ['generation', 'Генерация'],
  ];

  function requestTab(next: InstanceTab) {
    if (next === tab) return;
    if (dirty) setPendingTab(next);   // есть несохранённые изменения — спрашиваем
    else setTab(next);
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
    <div className="flex flex-col min-h-0 flex-1">
      <div className="shrink-0 px-6 pt-1 bg-surface">
        <div className="flex items-center gap-2 mb-1 min-w-0">
          <div className="flex-1 min-w-0">
            <InstanceNameEditor instance={instance} setId={setId} docType={docType} />
            {instance.name && (
              <p className="text-xs text-fg4 mt-0.5">{docType?.name}</p>
            )}
          </div>
          <span className={`text-xs px-2 py-0.5 rounded font-medium shrink-0 ${STATUS_COLORS[instance.status] ?? 'bg-muted text-fg2'}`}>
            {STATUS_LABELS[instance.status] ?? instance.status}
          </span>
        </div>
        <div className="flex border-b border-stroke gap-0">
          {tabs.map(([key, label]) => (
            <button key={key} onClick={() => requestTab(key)}
              className={`px-4 py-2 text-sm font-medium border-b-2 transition-colors ${tab === key ? 'border-brand text-brand-hover' : 'border-transparent text-fg3 hover:text-fg2'}`}>
              {label}{key === tab && dirty && <span className="ml-1 text-warning" title="Есть несохранённые изменения">•</span>}
            </button>
          ))}
        </div>
      </div>
      {tab === 'requisites' && (
        <RequisitesTab instance={instance} setId={setId} schemaFields={schemaFields}
          allDocTypes={allDocTypes} docType={docType} otherInstances={otherInstances}
          onClose={onClose} onDirty={setDirty} saveRef={saveRef}
          onGoToDataTab={() => requestTab('datasets')} />
      )}
      {tab === 'datasets' && (
        <div className="flex-1 min-h-0 overflow-y-auto px-6 py-4">
          <DataSetsTab instance={instance} setId={setId} schemaFields={schemaFields} allDocTypes={allDocTypes} docType={docType} />
        </div>
      )}
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

      {/* Основные действия диалога — только на редактируемых вкладках; на остальных — информационное сообщение */}
      <div className="shrink-0 px-6 py-3 bg-surface border-t border-stroke flex items-center gap-2 flex-wrap">
        {editable ? (
          <>
            <button onClick={() => void doSave()} disabled={saving}
              className="px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md transition-colors disabled:opacity-50">
              {saving ? 'Сохранение...' : 'Сохранить'}
            </button>
            <button onClick={() => void doSaveAndClose()} disabled={saving}
              className="px-4 py-2 text-sm border border-brand text-brand-hover hover:bg-brand-subtle rounded-md transition-colors disabled:opacity-50">
              Сохранить и закрыть
            </button>
            <button onClick={onClose} disabled={saving}
              className="px-4 py-2 text-sm text-fg2 hover:bg-muted rounded-md transition-colors disabled:opacity-50">
              Отмена
            </button>
            {savedFlash && <span className="text-sm text-success">Сохранено</span>}
          </>
        ) : (
          <span className="text-xs text-fg4">Изменения на этой вкладке применяются сразу</span>
        )}
      </div>

      {pendingTab && (
        <Modal
          open
          onOpenChange={o => { if (!o && !switching) setPendingTab(null); }}
          title="Документ не сохранён"
          footer={
            <div className="flex gap-2 justify-end flex-wrap">
              <button onClick={() => setPendingTab(null)} disabled={switching}
                className="px-3 py-1.5 text-sm rounded-md border border-stroke text-fg2 hover:bg-muted transition-colors disabled:opacity-50">
                Отмена
              </button>
              <button onClick={() => switchTo(pendingTab)} disabled={switching}
                className="px-3 py-1.5 text-sm rounded-md text-danger hover:bg-danger-subtle transition-colors disabled:opacity-50">
                Не сохранять
              </button>
              <button onClick={saveThenSwitch} disabled={switching}
                className="px-3 py-1.5 text-sm rounded-md bg-brand hover:bg-brand-hover text-white transition-colors disabled:opacity-50">
                {switching ? 'Сохранение...' : 'Сохранить'}
              </button>
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
