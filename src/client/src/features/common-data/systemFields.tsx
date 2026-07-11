import { useState, useEffect } from 'react';
import { DateInput } from '@/shared/ui/DateInput';
import { Plus, Trash2, Link2, Unlink, ChevronDown, ChevronUp, Image as ImageIcon, Upload, Eye, FileText, FileSpreadsheet, Download, Loader2 } from 'lucide-react';
import {
  type FileAttachment, isFileAttachment, getFileCategory,
  uploadAttachment, loadAttachmentObjectUrl, formatBytes,
} from '@/shared/api/attachments';
import { Modal } from '@/shared/ui/Modal';
import { useListCommonData } from '@/shared/api/commonData';
import type { CommonDataEntry, DocumentType, FieldRef, EnumTypeDef } from '@/shared/api/types';
import { isFieldRef } from '@/shared/api/types';
import { resolveEffectiveFields, getDefaultValues, isSubtypeOf, type SchemaField } from '@/shared/api/schema';
// ─── Primitive field input ─────────────────────────────────────────────────────

export function PrimitiveInput({ field, value, onChange, enumTypeDef }: {
  field: SchemaField; value: unknown; onChange: (v: unknown) => void; enumTypeDef?: EnumTypeDef;
}) {
  const cls = 'w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface text-fg1';
  const strVal = value == null ? '' : String(value);
  if (field.type === 'text')
    return <textarea value={strVal} onChange={e => onChange(e.target.value)} rows={3} className={cls + ' resize-y'} />;
  if (field.type === 'boolean')
    return (
      <label className="flex items-center gap-2 cursor-pointer">
        <input type="checkbox" checked={!!value} onChange={e => onChange(e.target.checked)} className="w-4 h-4 rounded border-stroke-strong text-brand" />
        <span className="text-sm text-fg2">{field.title}</span>
      </label>
    );
  if (field.type === 'enum') {
    if (enumTypeDef) {
      return (
        <select value={strVal} onChange={e => onChange(e.target.value)} className={cls}>
          <option value="">— выберите —</option>
          {enumTypeDef.values.map(v => <option key={v.code} value={v.code}>{v.label}</option>)}
        </select>
      );
    }
    const opts = (field.options ?? []).filter(o => o !== '');
    if (opts.length === 0)
      return <p className="text-xs text-fg4 italic py-1">Нет вариантов — добавьте их в схеме типа документа</p>;
    return (
      <select value={strVal} onChange={e => onChange(e.target.value)} className={cls}>
        <option value="">— выберите —</option>
        {opts.map(opt => <option key={opt} value={opt}>{opt}</option>)}
      </select>
    );
  }
  if (field.type === 'date')
    return <DateInput value={strVal} onChange={v => onChange(v)} className={cls} />;
  return (
    <input
      type={field.type === 'number' ? 'number' : 'text'}
      value={strVal}
      onChange={e => {
        const v = e.target.value;
        onChange(field.type === 'number' ? (v === '' ? '' : Number(v)) : v);
      }}
      className={cls}
    />
  );
}

// ─── File attachment field ────────────────────────────────────────────────────

function FileTypeIcon({ mimeType }: { mimeType: string }) {
  if (mimeType.startsWith('image/')) return <ImageIcon size={16} className="text-purple-500 shrink-0" />;
  if (mimeType === 'application/pdf') return <FileText size={16} className="text-danger shrink-0" />;
  if (mimeType.includes('word')) return <FileText size={16} className="text-brand shrink-0" />;
  if (mimeType.includes('sheet') || mimeType.includes('excel')) return <FileSpreadsheet size={16} className="text-success shrink-0" />;
  return <FileText size={16} className="text-fg4 shrink-0" />;
}

function FilePreviewModal({ open, onOpenChange, attachment }: {
  open: boolean; onOpenChange: (o: boolean) => void; attachment: FileAttachment;
}) {
  const [objectUrl, setObjectUrl] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const category = getFileCategory(attachment.mimeType);

  useEffect(() => {
    if (!open) return;
    let url: string | null = null;
    let cancelled = false;
    setLoading(true); setError(''); setObjectUrl(null);
    loadAttachmentObjectUrl(attachment.blobPath)
      .then(res => {
        if (cancelled) { URL.revokeObjectURL(res.url); return; }
        url = res.url; setObjectUrl(res.url);
      })
      .catch(e => { if (!cancelled) setError(e instanceof Error ? e.message : 'Ошибка загрузки'); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; if (url) URL.revokeObjectURL(url); };
  }, [open, attachment.blobPath]);

  return (
    <Modal open={open} onOpenChange={onOpenChange} title={attachment.fileName}>
      {loading && (
        <div className="flex items-center justify-center py-12">
          <Loader2 size={24} className="animate-spin text-brand" />
        </div>
      )}
      {error && <p className="text-sm text-danger py-4 text-center">{error}</p>}
      {objectUrl && category === 'image' && (
        <img src={objectUrl} alt={attachment.fileName}
          className="max-w-full max-h-[70vh] object-contain mx-auto block rounded" />
      )}
      {objectUrl && category === 'pdf' && (
        <iframe src={objectUrl} title={attachment.fileName}
          className="w-full border-0 rounded" style={{ height: '70vh' }} />
      )}
      {objectUrl && category === 'office' && (
        <div className="flex flex-col items-center gap-4 py-10">
          <FileSpreadsheet size={48} className="text-success" />
          <p className="text-sm text-fg3">Предпросмотр недоступен для этого формата.</p>
          <a href={objectUrl} download={attachment.fileName}
            className="flex items-center gap-2 px-4 py-2 bg-brand hover:bg-brand-hover text-white text-sm rounded-md transition-colors">
            <Download size={14} /> Скачать
          </a>
        </div>
      )}
    </Modal>
  );
}

export function FileField({ value, onChange }: {
  value: unknown; onChange: (val: FileAttachment | null) => void;
}) {
  const [uploading, setUploading] = useState(false);
  const [uploadError, setUploadError] = useState('');
  const [previewOpen, setPreviewOpen] = useState(false);
  const attachment = isFileAttachment(value) ? value : null;

  async function handleFile(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    if (!file) return;
    setUploading(true); setUploadError('');
    try {
      const result = await uploadAttachment(file);
      onChange(result);
    } catch (err) {
      setUploadError(err instanceof Error ? err.message : 'Ошибка загрузки');
    } finally {
      setUploading(false);
      e.target.value = '';
    }
  }

  if (uploading) {
    return (
      <div className="flex items-center gap-2 border border-stroke rounded-lg px-3 py-2.5">
        <Loader2 size={14} className="animate-spin text-brand shrink-0" />
        <span className="text-sm text-fg3">Загрузка файла...</span>
      </div>
    );
  }

  if (attachment) {
    return (
      <>
        <div className="flex items-center gap-2 border border-stroke rounded-lg px-3 py-2 bg-base">
          <FileTypeIcon mimeType={attachment.mimeType} />
          <span className="flex-1 text-sm text-fg1 font-medium truncate">{attachment.fileName}</span>
          <span className="text-xs text-fg4 shrink-0">{formatBytes(attachment.size)}</span>
          <button type="button" onClick={() => setPreviewOpen(true)} title="Предпросмотр"
            className="p-1 text-fg4 hover:text-brand transition-colors shrink-0">
            <Eye size={14} />
          </button>
          <button type="button" onClick={() => onChange(null)} title="Удалить"
            className="p-1 text-fg4 hover:text-danger transition-colors shrink-0">
            <Trash2 size={13} />
          </button>
        </div>
        {uploadError && <p className="text-xs text-danger mt-1">{uploadError}</p>}
        <FilePreviewModal open={previewOpen} onOpenChange={setPreviewOpen} attachment={attachment} />
      </>
    );
  }

  return (
    <>
      <label className="flex flex-col items-center justify-center gap-2 border-2 border-dashed border-stroke-strong rounded-lg py-5 cursor-pointer hover:border-brand hover:bg-brand-subtle transition-colors">
        <Upload size={18} className="text-fg4" />
        <span className="text-sm text-fg3">Нажмите для выбора файла</span>
        <span className="text-xs text-fg4">PDF, DOCX, XLSX, PNG, JPG, SVG (до 50 МБ)</span>
        <input type="file" accept=".pdf,.docx,.xlsx,.xls,.png,.jpg,.jpeg,.gif,.webp,.svg"
          className="hidden" onChange={handleFile} />
      </label>
      {uploadError && <p className="text-xs text-danger mt-1">{uploadError}</p>}
    </>
  );
}

// ─── Image field ──────────────────────────────────────────────────────────────

export function ImageField({ value, onChange }: {
  value: unknown; onChange: (val: string | null) => void;
}) {
  const dataUri = typeof value === 'string' && value.startsWith('data:image') ? value : null;

  function handleFile(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    if (!file) return;
    const reader = new FileReader();
    reader.onload = () => { if (typeof reader.result === 'string') onChange(reader.result); };
    reader.readAsDataURL(file);
    e.target.value = '';
  }

  if (dataUri) {
    return (
      <div className="space-y-2">
        <div className="border border-stroke rounded-lg overflow-hidden bg-base flex items-center justify-center p-2 max-h-52">
          <img src={dataUri} alt="" className="max-h-48 max-w-full object-contain" />
        </div>
        <button type="button" onClick={() => onChange(null)}
          className="flex items-center gap-1.5 text-xs text-danger hover:text-danger transition-colors">
          <Trash2 size={12} /> Удалить изображение
        </button>
      </div>
    );
  }

  return (
    <label className="flex flex-col items-center justify-center gap-2 border-2 border-dashed border-stroke-strong rounded-lg py-6 cursor-pointer hover:border-brand hover:bg-brand-subtle transition-colors">
      <ImageIcon size={20} className="text-fg4" />
      <span className="text-sm text-fg3">Нажмите для выбора изображения</span>
      <span className="text-xs text-fg4">PNG, JPG, SVG, WEBP</span>
      <input type="file" accept="image/*" className="hidden" onChange={handleFile} />
    </label>
  );
}

// ─── Base instance picker ─────────────────────────────────────────────────────

export function BaseEntryPickerModal({ open, onOpenChange, parentType, onSelect }: {
  open: boolean;
  onOpenChange: (o: boolean) => void;
  parentType: DocumentType;
  onSelect: (entry: CommonDataEntry) => void;
}) {
  const [search, setSearch] = useState('');
  const { data: entries = [] } = useListCommonData({ scope: 'System', typeId: parentType.id, enabled: open });
  const filtered = entries.filter(e => e.displayName.toLowerCase().includes(search.toLowerCase()));

  return (
    <Modal open={open} onOpenChange={onOpenChange} title={`Базовый экземпляр: ${parentType.name}`}>
      <div className="space-y-4">
        <input value={search} onChange={e => setSearch(e.target.value)} placeholder="Поиск..." autoFocus
          className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface" />
        {filtered.length === 0 ? (
          <p className="text-sm text-fg4 text-center py-4">
            Нет записей типа «{parentType.name}» в системном каталоге.
          </p>
        ) : (
          <div className="space-y-1 max-h-72 overflow-y-auto">
            {filtered.map(entry => (
              <button key={entry.id} type="button" onClick={() => { onSelect(entry); onOpenChange(false); }}
                className="w-full flex items-center gap-3 px-3 py-2 text-sm text-left rounded-md hover:bg-brand-subtle transition-colors">
                <Link2 size={13} className="text-brand shrink-0" />
                <span className="flex-1 font-medium text-fg1 truncate">{entry.displayName}</span>
              </button>
            ))}
          </div>
        )}
      </div>
    </Modal>
  );
}

// ─── System-scope ref picker ──────────────────────────────────────────────────

function SystemRefPickerModal({ open, onOpenChange, compositeType, allDocTypes, onSelect }: {
  open: boolean;
  onOpenChange: (o: boolean) => void;
  compositeType: DocumentType | null;
  allDocTypes: DocumentType[];
  onSelect: (ref: FieldRef) => void;
}) {
  const [search, setSearch] = useState('');
  const { data: entries = [] } = useListCommonData({
    scope: 'System', enabled: open,
  });
  const filtered = entries.filter(e => {
    if (compositeType && !isSubtypeOf(e.compositeTypeId, compositeType.id, allDocTypes)) return false;
    return e.displayName.toLowerCase().includes(search.toLowerCase());
  });

  function select(entry: CommonDataEntry) {
    onSelect({ $ref: 'catalog', entryId: entry.id, displayName: entry.displayName, scope: 'System' });
    onOpenChange(false);
  }

  return (
    <Modal open={open} onOpenChange={onOpenChange} title="Выбрать объект">
      <div className="space-y-4">
        <input value={search} onChange={e => setSearch(e.target.value)} placeholder="Поиск..." autoFocus
          className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface" />
        {filtered.length === 0 ? (
          <p className="text-sm text-fg4 text-center py-4">Нет записей в системном каталоге для этого типа.</p>
        ) : (
          <div className="space-y-1 max-h-72 overflow-y-auto">
            {filtered.map(entry => (
              <button key={entry.id} onClick={() => select(entry)}
                className="w-full flex items-center gap-3 px-3 py-2 text-sm text-left rounded-md hover:bg-brand-subtle transition-colors">
                <span className="flex-1 font-medium text-fg1 truncate">{entry.displayName}</span>
              </button>
            ))}
          </div>
        )}
      </div>
    </Modal>
  );
}

// ─── Array (repeating rows) field editor ─────────────────────────────────────

export function SystemArrayFieldEditor({ field, allDocTypes, enumTypes, value, onChange }: {
  field: SchemaField; allDocTypes: DocumentType[]; enumTypes: EnumTypeDef[];
  value: unknown; onChange: (v: unknown[]) => void;
}) {
  const compositeType = allDocTypes.find(dt => dt.id === field.typeId) ?? null;
  const items = Array.isArray(value) ? value as Record<string, unknown>[] : [];
  const subFields = compositeType ? resolveEffectiveFields(compositeType, allDocTypes) : [];
  const [expandedRows, setExpandedRows] = useState<Set<number>>(new Set());

  function addRow() {
    const newRow = getDefaultValues(subFields);
    onChange([...items, newRow]);
    setExpandedRows(prev => new Set([...prev, items.length]));
  }

  function removeRow(i: number) {
    const next = items.filter((_, idx) => idx !== i);
    onChange(next);
    setExpandedRows(prev => {
      const n = new Set<number>();
      prev.forEach(r => { if (r < i) n.add(r); else if (r > i) n.add(r - 1); });
      return n;
    });
  }

  function updateRow(i: number, row: Record<string, unknown>) {
    onChange(items.map((it, idx) => idx === i ? row : it));
  }

  function toggleRow(i: number) {
    setExpandedRows(prev => { const n = new Set(prev); n.has(i) ? n.delete(i) : n.add(i); return n; });
  }

  function rowSummary(row: Record<string, unknown>) {
    return subFields.slice(0, 3).map(f => row[f.key]).filter(v => v != null && v !== '').join(' · ') || '(пусто)';
  }

  return (
    <div className="border border-stroke rounded-lg overflow-hidden">
      <div className="flex items-center justify-between px-3 py-2 bg-base border-b border-stroke">
        <span className="text-xs font-medium text-fg3">
          {compositeType ? compositeType.name : 'Массив'}
          <span className="ml-2 text-fg4">{items.length} стр.</span>
        </span>
        <button type="button" onClick={addRow}
          className="flex items-center gap-1 text-xs text-brand hover:text-brand-hover px-2 py-0.5 rounded hover:bg-brand-subtle transition-colors">
          <Plus size={11} /> Добавить строку
        </button>
      </div>
      {items.length === 0 ? (
        <p className="text-xs text-fg4 text-center py-3">Нет строк — нажмите «Добавить строку»</p>
      ) : (
        <div className="divide-y divide-muted">
          {items.map((row, i) => {
            const isOpen = expandedRows.has(i);
            return (
              <div key={i}>
                <div className="flex items-center gap-2 px-3 py-2 hover:bg-base">
                  <span className="text-xs text-fg4 font-mono w-5 text-right shrink-0">{i + 1}</span>
                  <button type="button" onClick={() => toggleRow(i)}
                    className="flex-1 text-left text-sm text-fg2 truncate">{rowSummary(row)}</button>
                  <button type="button" onClick={() => toggleRow(i)}
                    className="p-1 text-fg4 hover:text-fg2 shrink-0">
                    {isOpen ? <ChevronUp size={13} /> : <ChevronDown size={13} />}
                  </button>
                  <button type="button" onClick={() => removeRow(i)}
                    className="p-1 text-fg4 hover:text-danger shrink-0"><Trash2 size={13} /></button>
                </div>
                {isOpen && (
                  <div className="px-4 py-3 space-y-3 bg-base/50 border-t border-muted">
                    {subFields.map(sf => (
                      <div key={sf.key}>
                        {sf.type !== 'boolean' && (
                          <label className="block text-xs font-medium text-fg2 mb-1">
                            {sf.title}{sf.required && <span className="ml-0.5 text-danger">*</span>}
                          </label>
                        )}
                        {sf.type === 'complex' ? (
                          <SystemComplexField field={sf} allDocTypes={allDocTypes} enumTypes={enumTypes}
                            value={row[sf.key]} onChange={v => updateRow(i, { ...row, [sf.key]: v })} />
                        ) : sf.type === 'doc-ref' ? (
                          <DocRefCatalogField field={sf} allDocTypes={allDocTypes}
                            value={row[sf.key]} onChange={v => updateRow(i, { ...row, [sf.key]: v ?? undefined })} />
                        ) : (
                          <PrimitiveInput field={sf} value={row[sf.key]}
                            enumTypeDef={sf.type === 'enum' ? enumTypes.find(et => et.id === sf.typeId) : undefined}
                            onChange={v => updateRow(i, { ...row, [sf.key]: v })} />
                        )}
                      </div>
                    ))}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}

// ─── Complex field for system catalog entries ──────────────────────────────────

export function SystemComplexField({ field, allDocTypes, enumTypes, value, onChange }: {
  field: SchemaField; allDocTypes: DocumentType[]; enumTypes: EnumTypeDef[];
  value: unknown; onChange: (v: Record<string, unknown> | FieldRef) => void;
}) {
  const [pickerOpen, setPickerOpen] = useState(false);
  const [collapsed, setCollapsed] = useState(true);
  const compositeType = allDocTypes.find(dt => dt.id === field.typeId) ?? null;

  if (isFieldRef(value)) {
    return (
      <div className="flex items-center gap-2 border border-brand-subtle rounded-lg px-3 py-2 bg-brand-subtle">
        <Link2 size={14} className="text-brand shrink-0" />
        <span className="flex-1 text-sm text-brand-hover font-medium">{value.displayName}</span>
        <button type="button" onClick={() => onChange({})} className="p-1 text-brand hover:text-danger" title="Снять ссылку">
          <Unlink size={13} />
        </button>
      </div>
    );
  }

  const subValues = (value != null && typeof value === 'object' && !isFieldRef(value) ? value : {}) as Record<string, unknown>;
  const subFields = compositeType ? resolveEffectiveFields(compositeType, allDocTypes) : [];

  return (
    <div className="border border-stroke rounded-lg overflow-hidden">
      <div className="flex items-center justify-between px-3 py-2 bg-base border-b border-stroke">
        <button type="button" onClick={() => setCollapsed(v => !v)}
          className="flex items-center gap-1.5 text-xs font-medium text-fg3 hover:text-fg2 transition-colors">
          {collapsed ? <ChevronDown size={12} className="shrink-0" /> : <ChevronUp size={12} className="shrink-0" />}
          {compositeType ? `${compositeType.name} (${compositeType.code})` : 'Составной тип'}
        </button>
        <button type="button" onClick={() => setPickerOpen(true)}
          className="flex items-center gap-1.5 text-xs text-brand hover:text-brand-hover px-2 py-0.5 rounded hover:bg-brand-subtle transition-colors">
          <Link2 size={11} /> Выбрать из каталога
        </button>
      </div>
      {!collapsed && (
        <div className="px-3 py-3 space-y-3">
          {subFields.length === 0 ? (
            <p className="text-xs text-fg4">Поля не заданы</p>
          ) : subFields.map(sf => (
            <div key={sf.key}>
              {sf.type !== 'boolean' && (
                <label className="block text-sm font-medium text-fg2 mb-1">
                  {sf.title}{sf.required && <span className="ml-0.5 text-danger">*</span>}
                </label>
              )}
              {sf.type === 'complex' ? (
                <SystemComplexField field={sf} allDocTypes={allDocTypes} enumTypes={enumTypes}
                  value={subValues[sf.key]} onChange={v => onChange({ ...subValues, [sf.key]: v })} />
              ) : sf.type === 'doc-ref' ? (
                <DocRefCatalogField field={sf} allDocTypes={allDocTypes}
                  value={subValues[sf.key]} onChange={v => onChange({ ...subValues, [sf.key]: v ?? undefined })} />
              ) : (
                <PrimitiveInput field={sf} value={subValues[sf.key]}
                  enumTypeDef={sf.type === 'enum' ? enumTypes.find(et => et.id === sf.typeId) : undefined}
                  onChange={v => onChange({ ...subValues, [sf.key]: v })} />
              )}
            </div>
          ))}
        </div>
      )}
      <SystemRefPickerModal open={pickerOpen} onOpenChange={setPickerOpen}
        compositeType={compositeType} allDocTypes={allDocTypes}
        onSelect={ref => onChange(ref)} />
    </div>
  );
}

// ─── Doc-ref field (links to a CommonDataEntry of a Document kind) ────────────

export function DocRefCatalogField({ field, allDocTypes, value, onChange }: {
  field: SchemaField; allDocTypes: DocumentType[];
  value: unknown; onChange: (v: FieldRef | null) => void;
}) {
  const [pickerOpen, setPickerOpen] = useState(false);
  const ref = isFieldRef(value) ? value : null;
  const docType = allDocTypes.find(dt => dt.id === field.typeId) ?? null;

  return (
    <div>
      {ref ? (
        <div className="flex items-center gap-2 border border-warning-border rounded-lg px-3 py-2 bg-warning-subtle">
          <FileText size={14} className="text-warning shrink-0" />
          <span className="flex-1 text-sm text-warning font-medium">{ref.displayName}</span>
          <button type="button" onClick={() => onChange(null)} title="Снять ссылку"
            className="p-1 text-warning hover:text-danger transition-colors">
            <Unlink size={13} />
          </button>
        </div>
      ) : (
        <button type="button" onClick={() => setPickerOpen(true)}
          className="flex items-center gap-2 px-3 py-2 text-sm text-warning hover:text-warning border border-dashed border-warning-border rounded-lg hover:bg-warning-subtle transition-colors w-full">
          <FileText size={13} /> {docType ? `Выбрать: ${docType.name}...` : 'Выбрать документ...'}
        </button>
      )}
      <SystemRefPickerModal
        open={pickerOpen}
        onOpenChange={setPickerOpen}
        compositeType={docType}
        allDocTypes={allDocTypes}
        onSelect={r => onChange(r)}
      />
    </div>
  );
}

