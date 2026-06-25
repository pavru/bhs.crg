import { useState } from 'react';
import { Loader2, FileText, Download, Eye, Pencil, ChevronDown, ChevronUp } from 'lucide-react';
import { useListPrimitiveTypes } from '@/shared/api/primitiveTypes';
import {
  useUpdateRequisites, useUpdateEntityRefs, useGenerateDocument,
  useSetDocumentTemplate, useRenameDocumentInstance,
  downloadGeneratedFile, previewGeneratedFile,
} from '@/shared/api/documentSets';
import { useListTemplates } from '@/shared/api/templates';
import { useListCatalogEntities } from '@/shared/api/catalog';
import type { DocumentInstance, DocumentType, Template, PrimitiveTypeDef } from '@/shared/api/types';
import {
  groupEffectiveFields, resolveEffectiveFields, type SchemaField,
} from '@/shared/api/schema';
import {
  STATUS_LABELS, STATUS_COLORS, tryPrettyJson, tryParseJson,
  validateConstraint, isMissing, PrimitiveInput, FileField, ImageField,
  DocRefField, DocArrayField, ArrayFieldEditor, ComplexFieldGroup,
} from '../fields';
import { DataSetsTab } from './DataSetsTab';

function RequisitesTab({ instance, setId, schemaFields, allDocTypes, docType, otherInstances, onClose }: {
  instance: DocumentInstance; setId: string; schemaFields: SchemaField[];
  allDocTypes: DocumentType[]; docType: DocumentType | undefined;
  otherInstances: DocumentInstance[]; onClose: () => void;
}) {
  const { data: primitiveTypes = [] } = useListPrimitiveTypes();
  const [values, setValues] = useState<Record<string, unknown>>(() => ({ ...instance.requisites }));
  const [constraintErrors, setConstraintErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState('');
  const [saved, setSaved] = useState(false);
  const [showValidation, setShowValidation] = useState(false);
  const [expandedGroups, setExpandedGroups] = useState<Set<string>>(new Set());
  const mutation = useUpdateRequisites();

  function getPrimitiveDef(field: SchemaField): PrimitiveTypeDef | undefined {
    if (field.type !== 'primitive') return undefined;
    return primitiveTypes.find(pt => pt.id === field.typeId);
  }

  function setValue(key: string, val: unknown, primitiveDef?: PrimitiveTypeDef) {
    setValues(p => ({ ...p, [key]: val }));
    setSaved(false);
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

  async function handleSave() {
    setError('');
    const missingRequired = schemaFields.filter(f => isMissing(f, values[f.key]));
    if (missingRequired.length > 0) {
      setShowValidation(true);
      setError(`Заполните обязательные поля: ${missingRequired.map(f => f.title).join(', ')}`);
      return;
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
      return;
    }
    try {
      await mutation.mutateAsync({ setId, instanceId: instance.id, requisites: values });
      setSaved(true);
      setTimeout(() => { setSaved(false); onClose(); }, 800);
    } catch (err: unknown) { setError(err instanceof Error ? err.message : 'Ошибка'); }
  }

  if (schemaFields.length === 0)
    return <div className="text-sm text-fg4 py-4 text-center">Схема полей не задана.</div>;

  const sections = groupEffectiveFields(schemaFields, docType?.schema ?? {});

  function renderFields(fields: SchemaField[]) {
    const isWide = (f: SchemaField) =>
      f.type === 'complex' || f.type === 'array' || f.type === 'doc-ref' ||
      f.type === 'doc-array' || f.type === 'image' || f.type === 'file' || f.type === 'text';

    return (
      <div className="space-y-3">
        {fields.map(field => {
          const raw = values[field.key];
          const missing = showValidation && isMissing(field, raw);
          const primitiveDef = getPrimitiveDef(field);
          const constraintError = constraintErrors[field.key];
          const hasError = missing || !!constraintError;
          const wide = isWide(field);

          if (wide) {
            return (
              <div key={field.key}>
                {field.type !== 'boolean' && field.type !== 'complex' && field.type !== 'array' && (
                  <label className="block text-sm font-medium text-fg2 mb-1">
                    {field.title}
                    {field.required && <span className="ml-0.5 text-danger">*</span>}
                    <span className="ml-2 text-xs text-fg4 font-mono font-normal">{field.key}</span>
                    {!field.required && <span className="ml-1 text-xs text-fg4">(опц.)</span>}
                  </label>
                )}
                {field.type === 'complex' ? (
                  <div>
                    <div className="flex items-center justify-between mb-1">
                      <label className="block text-sm font-medium text-fg2">
                        {field.title}
                        {field.required && <span className="ml-0.5 text-danger">*</span>}
                        <span className="ml-2 text-xs text-fg4 font-mono font-normal">{field.key}</span>
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
                    printForm={field.metaTag === 'printForm' ? {
                      setId, instanceId: instance.id, fieldKey: field.key,
                      onMetaUpdated: updates => {
                        setValues(prev => ({ ...prev, ...updates }));
                        setSaved(true);
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

          // Horizontal layout for simple fields (string, number, date, enum, boolean, primitive)
          return (
            <div key={field.key} className="flex items-start gap-3">
              <label className="w-2/5 shrink-0 text-sm font-medium text-fg2 pt-2 leading-tight">
                {field.title}
                {field.required && <span className="ml-0.5 text-danger">*</span>}
                <span className="block text-xs text-fg4 font-mono font-normal mt-0.5">{field.key}</span>
                {primitiveDef && (
                  <span className="block text-xs text-fg4 mt-0.5">[{primitiveDef.name}]</span>
                )}
              </label>
              <div className="flex-1 min-w-0">
                <PrimitiveInput field={field} value={raw}
                  onChange={v => setValue(field.key, v, primitiveDef)}
                  invalid={hasError} primitiveTypeDef={primitiveDef} />
                {missing && <p className="text-xs text-danger mt-1">Обязательное поле</p>}
                {!missing && constraintError && <p className="text-xs text-danger mt-1">{constraintError}</p>}
              </div>
            </div>
          );
        })}
      </div>
    );
  }

  return (
    <div className="space-y-4">
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
      {error && <p className="text-sm text-danger">{error}</p>}
      <div className="flex items-center gap-3 pt-1">
        <button onClick={handleSave} disabled={mutation.isPending}
          className="px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md transition-colors disabled:opacity-50">
          {mutation.isPending ? 'Сохранение...' : 'Сохранить'}
        </button>
        {saved && <span className="text-sm text-success">Сохранено</span>}
      </div>
    </div>
  );
}

// ─── Entity refs tab ──────────────────────────────────────────────────────────

function EntityRefsTab({ instance, setId, onClose }: {
  instance: DocumentInstance; setId: string; onClose: () => void;
}) {
  const [json, setJson] = useState(tryPrettyJson(instance.entityRefs));
  const [error, setError] = useState('');
  const [saved, setSaved] = useState(false);
  const [refKey, setRefKey] = useState('');
  const [entityType, setEntityType] = useState('');
  const mutation = useUpdateEntityRefs();
  const { data: entities = [] } = useListCatalogEntities(entityType || undefined);

  async function handleSave() {
    setError('');
    const parsed = tryParseJson(json);
    if (!parsed.ok) { setError('Неверный JSON: ' + parsed.error); return; }
    try {
      await mutation.mutateAsync({ setId, instanceId: instance.id, entityRefs: parsed.value! });
      setSaved(true);
      setTimeout(() => { setSaved(false); onClose(); }, 800);
    } catch (err: unknown) { setError(err instanceof Error ? err.message : 'Ошибка'); }
  }

  function addRef(entityId: string) {
    if (!refKey.trim()) return;
    const parsed = tryParseJson(json);
    const current = parsed.ok ? parsed.value! : {};
    current[refKey] = entityId;
    setJson(tryPrettyJson(current));
    setRefKey('');
  }

  return (
    <div className="space-y-3">
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
      <textarea value={json} onChange={e => { setJson(e.target.value); setSaved(false); }} rows={8} spellCheck={false}
        className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm font-mono focus:outline-none focus-visible:ring-2 focus-visible:ring-brand resize-y" />
      {error && <p className="text-sm text-danger">{error}</p>}
      <div className="flex items-center gap-3">
        <button onClick={handleSave} disabled={mutation.isPending}
          className="px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md transition-colors disabled:opacity-50">
          {mutation.isPending ? 'Сохранение...' : 'Сохранить'}
        </button>
        {saved && <span className="text-sm text-success">Сохранено</span>}
      </div>
    </div>
  );
}

// ─── Generation tab ───────────────────────────────────────────────────────────

function GenerationTab({ instance, setId }: { instance: DocumentInstance; setId: string }) {
  const [error, setError] = useState('');
  const mutation = useGenerateDocument();
  const setTemplateMutation = useSetDocumentTemplate();
  const { data: templates = [], isLoading: templatesLoading } = useListTemplates(instance.documentTypeId);
  const activeTemplates = templates.filter((t: Template) => t.isActive);
  const noTemplates = !templatesLoading && activeTemplates.length === 0;

  async function handleGenerate(format: 'Pdf' | 'Docx') {
    setError('');
    try { await mutation.mutateAsync({ instanceId: instance.id, setId, format }); }
    catch (err: unknown) { setError(err instanceof Error ? err.message : 'Ошибка'); }
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
      </div>
      {error && <p className="text-sm text-danger">{error}</p>}
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

type InstanceTab = 'requisites' | 'entity-refs' | 'datasets' | 'generation';

export function InstanceEditor({ instance, setId, docType, allDocTypes, otherInstances, onClose }: {
  instance: DocumentInstance; setId: string; docType: DocumentType | undefined;
  allDocTypes: DocumentType[]; otherInstances: DocumentInstance[]; onClose: () => void;
}) {
  const schemaFields = docType ? resolveEffectiveFields(docType, allDocTypes) : [];
  const [tab, setTab] = useState<InstanceTab>('requisites');
  const tabs: [InstanceTab, string][] = [
    ['requisites', 'Реквизиты'], ['entity-refs', 'Связи'], ['datasets', 'Данные'], ['generation', 'Генерация'],
  ];

  return (
    <div className="flex flex-col">
      <div className="sticky top-0 z-10 -mx-6 px-6 pb-0 bg-surface">
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
        <div className="flex border-b border-stroke mb-4 gap-0">
          {tabs.map(([key, label]) => (
            <button key={key} onClick={() => setTab(key)}
              className={`px-4 py-2 text-sm font-medium border-b-2 transition-colors ${tab === key ? 'border-brand text-brand-hover' : 'border-transparent text-fg3 hover:text-fg2'}`}>
              {label}
            </button>
          ))}
        </div>
      </div>
      {tab === 'requisites' && (
        <RequisitesTab instance={instance} setId={setId} schemaFields={schemaFields}
          allDocTypes={allDocTypes} docType={docType} otherInstances={otherInstances} onClose={onClose} />
      )}
      {tab === 'entity-refs' && <EntityRefsTab instance={instance} setId={setId} onClose={onClose} />}
      {tab === 'datasets' && <DataSetsTab instance={instance} setId={setId} schemaFields={schemaFields} allDocTypes={allDocTypes} docType={docType} />}
      {tab === 'generation' && <GenerationTab instance={instance} setId={setId} />}
    </div>
  );
}
