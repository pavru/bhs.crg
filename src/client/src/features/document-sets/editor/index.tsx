import { useState, useEffect, useRef } from 'react';
import { Loader2, FileText, Download, Eye, Pencil, ChevronDown, ChevronUp, Bug, ShieldCheck, AlertTriangle, AlertCircle, CheckCircle2, Mail } from 'lucide-react';
import { useAuth } from '@/shared/hooks/useAuth';
import { useEmailDocumentToSubscribers } from '@/shared/api/documentSets';
import { EmailSendDialog } from '../EmailSendDialog';
import { useListPrimitiveTypes } from '@/shared/api/primitiveTypes';
import {
  useUpdateRequisites, useGenerateDocument,
  useSetDocumentTemplates, useRenameDocumentInstance,
  downloadGeneratedFile, previewGeneratedFile, downloadDebugBundle,
  validateResolution, type ResolutionDiagnostic,
} from '@/shared/api/documentSets';
import { useListTemplates } from '@/shared/api/templates';
import { FUNCTIONAL_TAG } from '@/shared/api/tags';
import type { DocumentInstance, DocumentType, Template, PrimitiveTypeDef } from '@/shared/api/types';
import {
  groupEffectiveFields, resolveEffectiveFields, compositeFieldHasTag, type SchemaField,
} from '@/shared/api/schema';
import {
  STATUS_LABELS, STATUS_COLORS,
  validateConstraint, isMissing, PrimitiveInput, FileField, ImageField,
  DocRefField, DocArrayField, ArrayFieldEditor, ComplexFieldGroup,
} from '../fields';
import { DataSetsTab } from './DataSetsTab';
import { QualityLinksTab } from './QualityLinksTab';
import { DocumentTemplateParams } from './DocumentTemplateParams';
import { Modal } from '@/shared/ui/Modal';

type SaveRef = { current: (() => Promise<boolean>) | null };

/** Парсит JSON-строку массива id (templateIds) в массив; безопасно к битому/пустому значению. */
function parseIdArray(json: string | null): string[] {
  if (!json) return [];
  try { const a = JSON.parse(json); return Array.isArray(a) ? a as string[] : []; } catch { return []; }
}

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
  const emailDoc = useEmailDocumentToSubscribers();
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
                title="Отправить сгенерированные PDF документа подписчикам">
                <Mail size={13} className="text-brand" /> Отправить подписчикам
              </button>
            )}
          </div>
          <div className="space-y-1.5">
            {pdfFiles.map(f => {
              const tpl = templates.find((t: Template) => t.id === f.templateId);
              return (
                <div key={f.id} className="flex items-center gap-2">
                  <span className="text-xs text-fg2 flex-1 min-w-0 truncate" title={tpl?.name}>{tpl ? tpl.name : 'PDF'}</span>
                  <button onClick={() => previewGeneratedFile(instance.id, f.templateId)}
                    className="flex items-center gap-1 px-2.5 py-1.5 text-xs border border-stroke rounded-md hover:bg-base">
                    <Eye size={13} className="text-brand" /> Открыть
                  </button>
                  <button onClick={() => downloadGeneratedFile(instance.id, f.templateId)}
                    className="flex items-center gap-1 px-2.5 py-1.5 text-xs border border-stroke rounded-md hover:bg-base">
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
          onSend={(subject, body) => emailDoc.mutateAsync({ setId, instanceId: instance.id, subject, body })} />
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
          onClose={onClose} onDirty={setDirty} saveRef={saveRef} />
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
