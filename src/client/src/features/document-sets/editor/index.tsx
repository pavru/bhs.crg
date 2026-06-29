import { useState, useEffect, useRef } from 'react';
import { Loader2, FileText, Download, Eye, Pencil, ChevronDown, ChevronUp, Bug, ShieldCheck, AlertTriangle, AlertCircle, CheckCircle2 } from 'lucide-react';
import { useListPrimitiveTypes } from '@/shared/api/primitiveTypes';
import {
  useUpdateRequisites, useUpdateEntityRefs, useGenerateDocument,
  useSetDocumentTemplate, useRenameDocumentInstance,
  downloadGeneratedFile, previewGeneratedFile, downloadDebugBundle,
  validateResolution, type ResolutionDiagnostic,
} from '@/shared/api/documentSets';
import { useListTemplates } from '@/shared/api/templates';
import { FUNCTIONAL_TAG } from '@/shared/api/tags';
import { useListCatalogEntities } from '@/shared/api/catalog';
import type { DocumentInstance, DocumentType, Template, PrimitiveTypeDef } from '@/shared/api/types';
import {
  groupEffectiveFields, resolveEffectiveFields, compositeFieldHasTag, type SchemaField,
} from '@/shared/api/schema';
import {
  STATUS_LABELS, STATUS_COLORS, tryPrettyJson, tryParseJson,
  validateConstraint, isMissing, PrimitiveInput, FileField, ImageField,
  DocRefField, DocArrayField, ArrayFieldEditor, ComplexFieldGroup,
} from '../fields';
import { DataSetsTab } from './DataSetsTab';
import { QualityLinksTab } from './QualityLinksTab';

type SaveRef = { current: (() => Promise<boolean>) | null };

function RequisitesTab({ instance, setId, schemaFields, allDocTypes, docType, otherInstances, onDirty, saveRef }: {
  instance: DocumentInstance; setId: string; schemaFields: SchemaField[];
  allDocTypes: DocumentType[]; docType: DocumentType | undefined;
  otherInstances: DocumentInstance[]; onClose: () => void;
  onDirty: (dirty: boolean) => void; saveRef: SaveRef;
}) {
  const { data: primitiveTypes = [] } = useListPrimitiveTypes();
  const [values, setValues] = useState<Record<string, unknown>>(() => ({ ...instance.requisites }));
  const [constraintErrors, setConstraintErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState('');
  const [showValidation, setShowValidation] = useState(false);
  const [expandedGroups, setExpandedGroups] = useState<Set<string>>(new Set());
  const mutation = useUpdateRequisites();

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
    const missingRequired = schemaFields.filter(f => isMissing(f, values[f.key]));
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

  const sections = groupEffectiveFields(schemaFields, docType?.schema ?? {});

  function renderFields(fields: SchemaField[]) {
    const isWide = (f: SchemaField) =>
      f.type === 'complex' || f.type === 'array' || f.type === 'doc-ref' ||
      f.type === 'doc-array' || f.type === 'image' || f.type === 'file' || f.type === 'text';

    return (
      <div className="grid grid-cols-2 gap-x-4 gap-y-2.5">
        {fields.map(field => {
          const raw = values[field.key];
          const missing = showValidation && isMissing(field, raw);
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
                  <DocRefField field={field} allDocTypes={allDocTypes} value={raw}
                    onChange={v => setValue(field.key, v)} otherInstances={otherInstances} setId={setId} />
                ) : field.type === 'doc-array' ? (
                  <DocArrayField field={field} allDocTypes={allDocTypes} value={raw}
                    onChange={v => setValue(field.key, v)} otherInstances={otherInstances} setId={setId} />
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
                  <PrimitiveInput field={field} value={raw}
                    onChange={v => setValue(field.key, v, primitiveDef)}
                    invalid={hasError} primitiveTypeDef={primitiveDef} />
                )}
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
              </label>
              <PrimitiveInput field={field} value={raw}
                onChange={v => setValue(field.key, v, primitiveDef)}
                invalid={hasError} primitiveTypeDef={primitiveDef} />
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
      {sections.map(section => {
        if (!section.title) {
          // Ungrouped fields — always visible, no header
          return <div key={section.key}>{renderFields(section.fields)}</div>;
        }
        const isExpanded = expandedGroups.has(section.key);
        const hasMissing = showValidation && section.fields.some(f => isMissing(f, values[f.key]));
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

// ─── Entity refs tab ──────────────────────────────────────────────────────────

function EntityRefsTab({ instance, setId, onDirty, saveRef }: {
  instance: DocumentInstance; setId: string; onClose: () => void;
  onDirty: (dirty: boolean) => void; saveRef: SaveRef;
}) {
  const [json, setJson] = useState(tryPrettyJson(instance.entityRefs));
  const [error, setError] = useState('');
  const [refKey, setRefKey] = useState('');
  const [entityType, setEntityType] = useState('');
  const mutation = useUpdateEntityRefs();
  const { data: entities = [] } = useListCatalogEntities(entityType || undefined);

  async function handleSaveCore(): Promise<boolean> {
    setError('');
    const parsed = tryParseJson(json);
    if (!parsed.ok) { setError('Неверный JSON: ' + parsed.error); return false; }
    try {
      await mutation.mutateAsync({ setId, instanceId: instance.id, entityRefs: parsed.value! });
      onDirty(false);
      return true;
    } catch (err: unknown) { setError(err instanceof Error ? err.message : 'Ошибка'); return false; }
  }

  useEffect(() => { saveRef.current = handleSaveCore; return () => { saveRef.current = null; }; });

  function addRef(entityId: string) {
    if (!refKey.trim()) return;
    const parsed = tryParseJson(json);
    const current = parsed.ok ? parsed.value! : {};
    current[refKey] = entityId;
    setJson(tryPrettyJson(current));
    setRefKey('');
    onDirty(true);
  }

  return (
    <div className="flex flex-col min-h-0 flex-1">
      <div className="flex-1 min-h-0 overflow-y-auto px-6 py-4 space-y-3">
      <p className="text-xs text-fg3">Формат: <code className="bg-muted px-1 rounded">{'{ "ключ": "id-записи" }'}</code></p>
      <div className="border border-stroke rounded-md p-3 bg-base space-y-2">
        <p className="text-xs font-medium text-fg2">Добавить из каталога</p>
        <div className="flex gap-2">
          <input value={refKey} onChange={e => setRefKey(e.target.value)} placeholder="Ключ связи"
            className="flex-1 border border-stroke-strong rounded px-2 py-1.5 text-xs focus:outline-none focus-visible:ring-2 focus-visible:ring-brand" />
          <input value={entityType} onChange={e => setEntityType(e.target.value)} placeholder="Тип сущности"
            className="w-40 border border-stroke-strong rounded px-2 py-1.5 text-xs focus:outline-none focus-visible:ring-2 focus-visible:ring-brand" />
        </div>
        {entityType && entities.length > 0 && (
          <div className="max-h-32 overflow-y-auto border border-stroke rounded bg-surface">
            {entities.map(e => (
              <button key={e.id} onClick={() => addRef(e.id)}
                className="w-full text-left px-3 py-1.5 text-xs hover:bg-brand-subtle flex items-center gap-2">
                <span className="text-brand font-medium truncate">{e.displayName}</span>
              </button>
            ))}
          </div>
        )}
      </div>
      <textarea value={json} onChange={e => { setJson(e.target.value); onDirty(true); }} rows={8} spellCheck={false}
        className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm font-mono focus:outline-none focus-visible:ring-2 focus-visible:ring-brand resize-y" />
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

function DiagnosticsPanel({ diagnostics }: { diagnostics: ResolutionDiagnostic[] }) {
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
        Диагностика ссылок: {errors.length} ошиб., {warnings.length} предупр.
      </div>
      <div className="divide-y divide-muted max-h-72 overflow-y-auto">
        {[...errors, ...warnings].map((d, i) => (
          <div key={i} className="flex items-start gap-2 px-3 py-2 text-xs">
            {d.severity === 'error'
              ? <AlertCircle size={14} className="text-danger shrink-0 mt-0.5" />
              : <AlertTriangle size={14} className="text-warning shrink-0 mt-0.5" />}
            <div className="min-w-0">
              <code className="text-fg3">{d.path}</code>
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
  const [error, setError] = useState('');
  const [debugBusy, setDebugBusy] = useState(false);
  const [validating, setValidating] = useState(false);
  const [diagnostics, setDiagnostics] = useState<ResolutionDiagnostic[] | null>(null);
  const mutation = useGenerateDocument();
  const setTemplateMutation = useSetDocumentTemplate();
  const { data: templates = [], isLoading: templatesLoading } = useListTemplates(instance.documentTypeId);
  const activeTemplates = templates.filter((t: Template) => t.isActive);
  const noTemplates = !templatesLoading && activeTemplates.length === 0;

  async function handleGenerate(format: 'Pdf' | 'Docx') {
    setError('');
    setDiagnostics(null);
    try { await mutation.mutateAsync({ instanceId: instance.id, setId, format }); }
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

  function handleTemplateChange(e: React.ChangeEvent<HTMLSelectElement>) {
    const val = e.target.value;
    setTemplateMutation.mutate({ setId, instanceId: instance.id, templateId: val === '' ? null : val });
  }

  const pdfFile = instance.generatedFiles.find(f => f.format === 'Pdf');
  const docxFile = instance.generatedFiles.find(f => f.format === 'Docx');

  return (
    <div className="space-y-5">
      <div className={`p-3 rounded-md text-sm ${STATUS_COLORS[instance.status] ?? 'bg-base text-fg2'}`}>
        Статус: <strong>{STATUS_LABELS[instance.status] ?? instance.status}</strong>
        {instance.status === 'Generating' && <Loader2 size={14} className="inline-block ml-2 animate-spin" />}
      </div>

      <div className="space-y-1">
        <label className="block text-xs font-medium text-fg2">Шаблон</label>
        <select
          value={instance.templateId ?? ''}
          onChange={handleTemplateChange}
          disabled={setTemplateMutation.isPending}
          className="w-full px-3 py-2 text-sm border border-stroke-strong rounded-md bg-surface text-fg1 focus:outline-none focus-visible:ring-1 focus-visible:ring-brand disabled:opacity-50"
        >
          <option value="">По умолчанию (шаблон типа документа)</option>
          {activeTemplates.map((t: Template) => (
            <option key={t.id} value={t.id}>
              {t.isDefault ? '★ ' : ''}{t.name} (v{t.version})
            </option>
          ))}
        </select>
        {noTemplates && (
          <p className="text-xs text-warning mt-1">
            Для этого типа документа нет шаблонов. Создайте шаблон в разделе «Шаблоны».
          </p>
        )}
      </div>

      <div className="flex gap-3">
        <button onClick={() => handleGenerate('Pdf')} disabled={mutation.isPending || noTemplates}
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
      {error && <p className="text-sm text-danger">{error}</p>}

      {diagnostics && <DiagnosticsPanel diagnostics={diagnostics} />}
      {(pdfFile || docxFile) && (
        <div className="space-y-2">
          <p className="text-xs font-medium text-fg2">Сгенерированные файлы</p>
          <div className="flex gap-2">
            {pdfFile && (
              <>
                <button onClick={() => previewGeneratedFile(instance.id, 'pdf')}
                  className="flex items-center gap-2 px-3 py-2 text-sm border border-stroke rounded-md hover:bg-base">
                  <Eye size={14} className="text-brand" /> Открыть PDF
                </button>
                <button onClick={() => downloadGeneratedFile(instance.id, 'pdf')}
                  className="flex items-center gap-2 px-3 py-2 text-sm border border-stroke rounded-md hover:bg-base">
                  <Download size={14} className="text-brand" /> Скачать PDF
                </button>
              </>
            )}
            {docxFile && (
              <button onClick={() => downloadGeneratedFile(instance.id, 'docx')}
                className="flex items-center gap-2 px-3 py-2 text-sm border border-stroke rounded-md hover:bg-base">
                <Download size={14} className="text-brand" /> Скачать DOCX
              </button>
            )}
          </div>
        </div>
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

type InstanceTab = 'requisites' | 'entity-refs' | 'datasets' | 'quality' | 'generation';

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
  const editable = tab === 'requisites' || tab === 'entity-refs';
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
    ['requisites', 'Реквизиты'], ['entity-refs', 'Связи'], ['datasets', 'Данные'],
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
          onClose={onClose} onDirty={setDirty} saveRef={saveRef} />
      )}
      {tab === 'entity-refs' && <EntityRefsTab instance={instance} setId={setId} onClose={onClose} onDirty={setDirty} saveRef={saveRef} />}
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

      {/* Основные действия диалога — всегда внизу, на всех вкладках */}
      <div className="shrink-0 px-6 py-3 bg-surface border-t border-stroke flex items-center gap-2 flex-wrap">
        <button onClick={() => void doSave()} disabled={!editable || saving}
          title={editable ? undefined : 'На этой вкладке изменения сохраняются автоматически'}
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
        {!editable && <span className="ml-auto text-xs text-fg4">Изменения на этой вкладке применяются сразу</span>}
      </div>

      {pendingTab && (
        <div className="fixed inset-0 z-[60] flex items-center justify-center bg-black/50 p-4">
          <div className="rounded-xl p-5 w-full max-w-sm bg-surface border border-stroke shadow-2xl">
            <p className="text-sm font-semibold mb-1 text-fg1">Документ не сохранён</p>
            <p className="text-xs mb-4 text-fg3">
              На текущей вкладке есть несохранённые изменения. Сохранить перед переходом?
              Иначе они будут потеряны.
            </p>
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
          </div>
        </div>
      )}
    </div>
  );
}
