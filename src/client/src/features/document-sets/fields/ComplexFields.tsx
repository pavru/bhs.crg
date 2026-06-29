import { useState, useEffect } from 'react';
import {
  Clipboard, ChevronDown, ChevronUp, FileSpreadsheet, Link2, Plus, Trash2, Unlink, X,
} from 'lucide-react';
import { DateInput } from '@/shared/ui/DateInput';
import { Modal } from '@/shared/ui/Modal';
import { useCommonDataForSet, useListCommonData } from '@/shared/api/commonData';
import type {
  CatalogScope, CommonDataEntry, DocumentInstance, DocumentType, FieldRef,
} from '@/shared/api/types';
import { isFieldRef, SCOPE_LABELS } from '@/shared/api/types';
import {
  resolveEffectiveFields, getDefaultValues, type SchemaField,
} from '@/shared/api/schema';
import {
  CELL_INPUT, SCOPE_COLORS, TABLE_SHOWN_TYPES, defaultColWidth,
} from './constants';
import { isMissing, PrimitiveInput } from './PrimitiveInput';
import { ImageField } from './ImageField';
import { FileField } from './FileField';
import { RefPickerModal } from './RefPickerModal';
import { DocRefCatalogPickerField } from './DocRefCatalogPickerField';
import { DocRefField, DocArrayField } from './DocRefField';
import { PasteMappingModal } from './PasteMappingModal';

// ─── Complex cell picker (inline table cell) ──────────────────────────────────

export function ComplexCellPicker({ value, onChange, compositeType, setId, allDocTypes, scope, scopeId }: {
  field: SchemaField; value: unknown; onChange: (v: unknown) => void;
  compositeType: DocumentType | null;
  setId?: string; allDocTypes: DocumentType[];
  scope?: CatalogScope; scopeId?: string | null;
}) {
  const [pickerOpen, setPickerOpen] = useState(false);
  const ref = isFieldRef(value) ? value : null;
  return (
    <div className="flex items-center w-full h-full">
      <button type="button" onClick={() => setPickerOpen(true)}
        className="flex-1 min-w-0 h-full flex items-center gap-1 px-1.5 focus:outline-none focus:bg-brand-subtle">
        {ref
          ? <><Link2 size={10} className="text-brand shrink-0" /><span className="text-xs truncate text-brand-hover">{ref.displayName}</span></>
          : <span className="text-xs text-fg4">—</span>
        }
      </button>
      {ref && (
        <button type="button" onClick={e => { e.stopPropagation(); onChange(undefined); }}
          className="shrink-0 p-0.5 mr-0.5 text-stroke-strong hover:text-danger transition-colors">
          <X size={9} />
        </button>
      )}
      <RefPickerModal open={pickerOpen} onOpenChange={setPickerOpen}
        compositeType={compositeType}
        setId={setId} scope={scope} scopeId={scopeId}
        allDocTypes={allDocTypes}
        onSelect={r => onChange(r)} />
    </div>
  );
}

// ─── Table cell ───────────────────────────────────────────────────────────────

export function TableCell({ field, value, onChange, compositeType, setId, allDocTypes, scope, scopeId }: {
  field: SchemaField; value: unknown; onChange: (v: unknown) => void;
  compositeType: DocumentType | null;
  setId?: string; allDocTypes: DocumentType[];
  scope?: CatalogScope; scopeId?: string | null;
}) {
  const strVal = value == null ? '' : String(value);
  if (field.type === 'complex') {
    return (
      <ComplexCellPicker field={field} value={value} onChange={onChange}
        compositeType={compositeType} setId={setId} allDocTypes={allDocTypes}
        scope={scope} scopeId={scopeId} />
    );
  }
  if (field.type === 'boolean') {
    return (
      <div className="flex items-center justify-center w-full h-full">
        <input type="checkbox" checked={!!value}
          onChange={e => onChange(e.target.checked)}
          className="w-3.5 h-3.5 rounded border-stroke-strong text-brand" />
      </div>
    );
  }
  if (field.type === 'enum') {
    const opts = (field.options ?? []).filter(o => o !== '');
    return (
      <select value={strVal} onChange={e => onChange(e.target.value)}
        className={CELL_INPUT + ' cursor-pointer'}>
        <option value="">—</option>
        {opts.map(opt => <option key={opt} value={opt}>{opt}</option>)}
      </select>
    );
  }
  if (field.type === 'date') {
    return <DateInput value={strVal} onChange={v => onChange(v)}
      className="w-full h-full flex items-center px-1.5 focus-within:bg-brand-subtle" />;
  }
  return (
    <input type={field.type === 'number' ? 'number' : 'text'}
      value={strVal}
      onChange={e => {
        const v = e.target.value;
        onChange(field.type === 'number' ? (v === '' ? '' : Number(v)) : v);
      }}
      className={CELL_INPUT}
    />
  );
}

// ─── Array table modal ────────────────────────────────────────────────────────

export function ArrayTableModal({
  open, onOpenChange, field, compositeType, allDocTypes, items, onSave,
  setId, scope, scopeId,
}: {
  open: boolean; onOpenChange: (v: boolean) => void;
  field: SchemaField; compositeType: DocumentType | null; allDocTypes: DocumentType[];
  items: Record<string, unknown>[];
  onSave: (rows: Record<string, unknown>[]) => void;
  setId?: string; scope?: CatalogScope; scopeId?: string | null;
}) {
  const [rows, setRows] = useState<Record<string, unknown>[]>([]);
  const [colWidths, setColWidths] = useState<Record<string, number>>({});
  const [pasteOpen, setPasteOpen] = useState(false);
  const [pasteText, setPasteText] = useState('');

  useEffect(() => {
    if (open) setRows(items.map(r => ({ ...r })));
  }, [open]); // eslint-disable-line react-hooks/exhaustive-deps

  const { data: cdForSet = [] } = useCommonDataForSet({ setId: setId ?? '', enabled: open && !!setId });
  const { data: cdSystem = [] } = useListCommonData({ scope: 'System', enabled: open && !setId });
  const commonDataEntries = (setId ? cdForSet : cdSystem) as CommonDataEntry[];

  const subFields = compositeType ? resolveEffectiveFields(compositeType, allDocTypes) : [];
  const tableFields = subFields.filter(f => TABLE_SHOWN_TYPES.has(f.type));
  const hiddenFields = subFields.filter(f => !TABLE_SHOWN_TYPES.has(f.type));

  function getW(f: SchemaField) { return colWidths[f.key] ?? defaultColWidth(f); }

  function startResize(e: React.MouseEvent, key: string, curW: number) {
    e.preventDefault();
    const startX = e.clientX;
    function onMove(ev: MouseEvent) {
      setColWidths(prev => ({ ...prev, [key]: Math.max(44, curW + ev.clientX - startX) }));
    }
    function onUp() {
      document.removeEventListener('mousemove', onMove);
      document.removeEventListener('mouseup', onUp);
    }
    document.addEventListener('mousemove', onMove);
    document.addEventListener('mouseup', onUp);
  }

  function updateCell(ri: number, key: string, val: unknown) {
    setRows(prev => prev.map((r, i) => i === ri ? { ...r, [key]: val } : r));
  }
  function addRow() { setRows(prev => [...prev, getDefaultValues(subFields)]); }
  function removeRow(idx: number) { setRows(prev => prev.filter((_, i) => i !== idx)); }
  function handleSave() { onSave(rows); onOpenChange(false); }

  async function handlePasteClick() {
    let text = '';
    try { text = await navigator.clipboard.readText(); } catch { /* permission denied */ }
    setPasteText(text);
    setPasteOpen(true);
  }

  const BORDER = '1px solid #d1d5db';
  const TH_BG = '#f3f4f6';
  const IDX_BG = '#f9fafb';

  return (
    <Modal open={open} onOpenChange={onOpenChange}
      title={`${compositeType?.name ?? field.title} — таблица`}
      extraWide
      footer={
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-2">
            <button type="button" onClick={addRow}
              className="flex items-center gap-1.5 text-sm text-brand hover:text-brand-hover transition-colors">
              <Plus size={13} /> Добавить строку
            </button>
            <span className="text-stroke-strong">·</span>
            <button type="button" onClick={handlePasteClick}
              className="flex items-center gap-1.5 text-sm text-fg3 hover:text-fg2 transition-colors">
              <Clipboard size={13} /> Вставить из Excel
            </button>
          </div>
          <div className="flex gap-3">
            <button type="button" onClick={() => onOpenChange(false)}
              className="px-4 py-2 text-sm text-fg2 hover:bg-muted rounded-md transition-colors">
              Отмена
            </button>
            <button type="button" onClick={handleSave}
              className="px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md transition-colors">
              Применить
            </button>
          </div>
        </div>
      }>
      <div className="overflow-x-auto -mx-6 px-6">
        <table style={{ tableLayout: 'fixed', borderCollapse: 'collapse', width: 'max-content', minWidth: '100%' }}>
          <colgroup>
            <col style={{ width: 32 }} />
            {tableFields.map(f => <col key={f.key} style={{ width: getW(f) }} />)}
            <col style={{ width: 26 }} />
          </colgroup>
          <thead>
            <tr>
              <th style={{ border: BORDER, background: TH_BG, padding: 0, width: 32 }}>
                <span className="flex items-center justify-center text-xs text-fg4 font-normal" style={{ height: 28 }}>#</span>
              </th>
              {tableFields.map(f => (
                <th key={f.key}
                  style={{ border: BORDER, background: TH_BG, padding: 0, position: 'relative', userSelect: 'none' }}>
                  <span className="flex items-center px-2 text-left text-xs font-semibold text-fg2 truncate" style={{ height: 28 }}>
                    {f.title}{f.required && <span className="text-danger ml-0.5">*</span>}
                  </span>
                  <div onMouseDown={e => startResize(e, f.key, getW(f))}
                    className="hover:bg-brand-subtle/40 transition-colors"
                    style={{ position: 'absolute', right: 0, top: 0, bottom: 0, width: 5, cursor: 'col-resize', zIndex: 1 }} />
                </th>
              ))}
              <th style={{ border: BORDER, background: TH_BG, padding: 0, width: 26 }} />
            </tr>
          </thead>
          <tbody>
            {rows.map((row, i) => (
              <tr key={i}>
                <td style={{ border: BORDER, background: IDX_BG, padding: 0, textAlign: 'center' }}>
                  <span className="flex items-center justify-center text-xs text-fg4 font-mono" style={{ height: 26 }}>{i + 1}</span>
                </td>
                {tableFields.map(f => {
                  const compositeForField = f.type === 'complex'
                    ? allDocTypes.find(dt => dt.id === f.typeId) ?? null : null;
                  return (
                    <td key={f.key}
                      className="focus-within:bg-brand-subtle transition-colors"
                      style={{ border: BORDER, padding: 0, height: 26 }}>
                      <TableCell field={f} value={row[f.key]} onChange={v => updateCell(i, f.key, v)}
                        compositeType={compositeForField} setId={setId} allDocTypes={allDocTypes}
                        scope={scope} scopeId={scopeId} />
                    </td>
                  );
                })}
                <td style={{ border: BORDER, padding: 0, width: 26 }}>
                  <button type="button" onClick={() => removeRow(i)}
                    className="w-full h-full flex items-center justify-center text-stroke-strong hover:text-danger transition-colors"
                    style={{ height: 26 }}>
                    <Trash2 size={11} />
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {rows.length === 0 && (
          <p className="text-center text-xs text-fg4 py-6">Нет строк — нажмите «Добавить строку»</p>
        )}
      </div>
      {hiddenFields.length > 0 && (
        <p className="text-xs text-fg4 mt-3">
          {hiddenFields.length === 1
            ? `Поле «${hiddenFields[0].title}» скрыто`
            : `${hiddenFields.length} полей скрыто`} — редактируйте в режиме аккордеона
        </p>
      )}
      <PasteMappingModal
        open={pasteOpen} onOpenChange={setPasteOpen}
        initialText={pasteText}
        tableFields={tableFields}
        allDocTypes={allDocTypes}
        commonDataEntries={commonDataEntries}
        onApply={newRows => setRows(prev => [...prev, ...newRows])}
      />
    </Modal>
  );
}

// ─── Array field editor ───────────────────────────────────────────────────────

export function ArrayFieldEditor({ field, allDocTypes, value, onChange, showValidation,
  setId, otherInstances = [], scope, scopeId, docRefMode = 'catalog',
}: {
  field: SchemaField; allDocTypes: DocumentType[]; value: unknown;
  onChange: (val: unknown[]) => void; showValidation: boolean;
  setId?: string; otherInstances?: DocumentInstance[];
  scope?: CatalogScope; scopeId?: string | null;
  docRefMode?: 'catalog' | 'instance';
}) {
  const compositeType = allDocTypes.find(dt => dt.id === field.typeId) ?? null;
  const allItems = Array.isArray(value) ? value as unknown[] : [];
  const inlineItems = allItems.filter(item => !isFieldRef(item)) as Record<string, unknown>[];
  const subFields = compositeType ? resolveEffectiveFields(compositeType, allDocTypes) : [];
  const [expandedRows, setExpandedRows] = useState<Set<number>>(new Set());
  const [tableOpen, setTableOpen] = useState(false);
  const [collapsed, setCollapsed] = useState(true);
  const [catalogPickerOpen, setCatalogPickerOpen] = useState(false);

  const hasTableFields = subFields.some(f => TABLE_SHOWN_TYPES.has(f.type));

  function addRow() {
    const newRow = getDefaultValues(subFields);
    onChange([...allItems, newRow]);
    setExpandedRows(prev => new Set([...prev, allItems.length]));
  }

  function addFromCatalog(ref: FieldRef) {
    onChange([...allItems, ref]);
  }

  function removeItem(i: number) {
    onChange(allItems.filter((_, idx) => idx !== i));
    setExpandedRows(prev => {
      const n = new Set<number>();
      prev.forEach(r => { if (r < i) n.add(r); else if (r > i) n.add(r - 1); });
      return n;
    });
  }

  function updateRow(i: number, row: Record<string, unknown>) {
    onChange(allItems.map((it, idx) => idx === i ? row : it));
  }

  function toggleRow(i: number) {
    setExpandedRows(prev => { const n = new Set(prev); n.has(i) ? n.delete(i) : n.add(i); return n; });
  }

  function rowSummary(row: Record<string, unknown>) {
    return subFields.slice(0, 3)
      .map(f => row[f.key])
      .filter(v => v != null && v !== '')
      .join(' · ') || '(пусто)';
  }

  return (
    <div className="border border-stroke rounded-lg overflow-hidden">
      <div className={`flex items-center justify-between px-3 py-2 bg-base ${collapsed ? '' : 'border-b border-stroke'}`}>
        <button type="button" onClick={() => setCollapsed(v => !v)}
          className="flex items-center gap-1.5 min-w-0 text-sm font-medium text-fg2 hover:text-fg1 transition-colors">
          {collapsed
            ? <ChevronDown size={12} className="shrink-0 text-fg4" />
            : <ChevronUp size={12} className="shrink-0 text-fg4" />}
          <span className="truncate">{field.title}</span>
          {compositeType && compositeType.name !== field.title && (
            <span className="text-xs text-fg4 font-normal shrink-0">({compositeType.name})</span>
          )}
          <span className="text-xs text-fg4 font-normal ml-1 shrink-0">{allItems.length} стр.</span>
        </button>
        <div className="flex items-center gap-1">
          {hasTableFields && (
            <button type="button" onClick={() => setTableOpen(true)}
              className="flex items-center gap-1 text-xs text-fg3 hover:text-fg2 px-2 py-0.5 rounded hover:bg-stroke transition-colors">
              <FileSpreadsheet size={11} /> Таблица
            </button>
          )}
          {compositeType && (
            <button type="button" onClick={() => setCatalogPickerOpen(true)}
              className="flex items-center gap-1 text-xs text-warning hover:text-warning px-2 py-0.5 rounded hover:bg-warning-subtle transition-colors">
              <Link2 size={11} /> Из каталога
            </button>
          )}
          <button type="button" onClick={addRow}
            className="flex items-center gap-1 text-xs text-brand hover:text-brand-hover px-2 py-0.5 rounded hover:bg-brand-subtle transition-colors">
            <Plus size={11} /> Добавить строку
          </button>
        </div>
      </div>
      {!collapsed && allItems.length === 0 && (
        <p className="text-xs text-fg4 text-center py-3">Нет строк — нажмите «Добавить строку» или «Из каталога»</p>
      )}
      {!collapsed && allItems.length > 0 && (
        <div className="divide-y divide-muted">
          {allItems.map((item, i) => {
            if (isFieldRef(item)) {
              return (
                <div key={i} className="flex items-center gap-2 px-3 py-2 hover:bg-base">
                  <span className="text-xs text-fg4 font-mono w-5 text-right shrink-0">{i + 1}</span>
                  <Link2 size={12} className="text-warning shrink-0" />
                  <span className="flex-1 text-sm text-warning truncate">{item.displayName}</span>
                  <button type="button" onClick={() => removeItem(i)}
                    className="p-1 text-fg4 hover:text-danger shrink-0">
                    <Trash2 size={13} />
                  </button>
                </div>
              );
            }
            const row = item as Record<string, unknown>;
            const isOpen = expandedRows.has(i);
            return (
              <div key={i}>
                <div className="flex items-center gap-2 px-3 py-2 hover:bg-base">
                  <span className="text-xs text-fg4 font-mono w-5 text-right shrink-0">{i + 1}</span>
                  <button type="button" onClick={() => toggleRow(i)}
                    className="flex-1 text-left text-sm text-fg2 truncate">
                    {rowSummary(row)}
                  </button>
                  <button type="button" onClick={() => toggleRow(i)}
                    className="p-1 text-fg4 hover:text-fg2 shrink-0">
                    {isOpen ? <ChevronUp size={13} /> : <ChevronDown size={13} />}
                  </button>
                  <button type="button" onClick={() => removeItem(i)}
                    className="p-1 text-fg4 hover:text-danger shrink-0">
                    <Trash2 size={13} />
                  </button>
                </div>
                {isOpen && (
                  <div className="px-4 py-3 space-y-3 bg-base/50 border-t border-muted">
                    {subFields.map(sf => {
                      const subVal = row[sf.key];
                      const invalid = showValidation && isMissing(sf, subVal);
                      return (
                        <div key={sf.key}>
                          {sf.type !== 'boolean' && (
                            <label className="block text-xs font-medium text-fg2 mb-1">
                              {sf.title}{sf.required && <span className="ml-0.5 text-danger">*</span>}
                            </label>
                          )}
                          {sf.type === 'complex' ? (
                            <ComplexFieldGroup field={sf} allDocTypes={allDocTypes} value={subVal}
                              onChange={v => updateRow(i, { ...row, [sf.key]: v })}
                              showValidation={showValidation} setId={setId}
                              otherInstances={otherInstances} scope={scope} scopeId={scopeId}
                              docRefMode={docRefMode} />
                          ) : sf.type === 'doc-ref' ? (
                            docRefMode === 'instance' ? (
                              <DocRefField field={sf} allDocTypes={allDocTypes} value={subVal}
                                onChange={v => updateRow(i, { ...row, [sf.key]: v ?? undefined })}
                                otherInstances={otherInstances} setId={setId} />
                            ) : (
                              <DocRefCatalogPickerField field={sf} allDocTypes={allDocTypes} value={subVal}
                                onChange={v => updateRow(i, { ...row, [sf.key]: v ?? undefined })}
                                setId={setId} scope={scope ?? 'System'} scopeId={scopeId ?? null} />
                            )
                          ) : sf.type === 'doc-array' && docRefMode === 'instance' ? (
                            <DocArrayField field={sf} allDocTypes={allDocTypes} value={subVal}
                              onChange={v => updateRow(i, { ...row, [sf.key]: v })}
                              otherInstances={otherInstances} setId={setId} />
                          ) : (
                            <PrimitiveInput field={sf} value={subVal}
                              onChange={v => updateRow(i, { ...row, [sf.key]: v })} invalid={invalid} />
                          )}
                          {invalid && <p className="text-xs text-danger mt-0.5">Обязательное поле</p>}
                        </div>
                      );
                    })}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      )}
      <ArrayTableModal
        open={tableOpen} onOpenChange={setTableOpen}
        field={field} compositeType={compositeType} allDocTypes={allDocTypes}
        items={inlineItems}
        onSave={rows => onChange([...allItems.filter(isFieldRef), ...rows])}
        setId={setId} scope={scope} scopeId={scopeId}
      />
      {compositeType && (
        <RefPickerModal
          open={catalogPickerOpen} onOpenChange={setCatalogPickerOpen}
          compositeType={compositeType}
          setId={setId} scope={scope} scopeId={scopeId}
          otherInstances={[]}
          allDocTypes={allDocTypes}
          onSelect={addFromCatalog}
        />
      )}
    </div>
  );
}

// ─── Complex field group ──────────────────────────────────────────────────────

export function ComplexFieldGroup({ field, allDocTypes, value, onChange, showValidation,
  setId, otherInstances = [],
  scope, scopeId, docRefMode = 'catalog',
}: {
  field: SchemaField; allDocTypes: DocumentType[]; value: unknown;
  onChange: (val: Record<string, unknown> | FieldRef) => void;
  showValidation: boolean;
  setId?: string; otherInstances?: DocumentInstance[];
  scope?: CatalogScope; scopeId?: string | null;
  docRefMode?: 'catalog' | 'instance';
}) {
  const [pickerOpen, setPickerOpen] = useState(false);
  const [collapsed, setCollapsed] = useState(true);
  const compositeType = allDocTypes.find(dt => dt.id === field.typeId) ?? null;

  if (isFieldRef(value)) {
    return (
      <div className="flex items-center gap-2 border border-brand-subtle rounded-lg px-3 py-2 bg-brand-subtle">
        <Link2 size={14} className="text-brand shrink-0" />
        <span className="flex-1 text-sm text-brand-hover font-medium">{value.displayName}</span>
        {value.scope && (
          <span className={`text-xs px-1.5 py-0.5 rounded font-medium ${SCOPE_COLORS[value.scope]}`}>
            {SCOPE_LABELS[value.scope]}
          </span>
        )}
        <button type="button" onClick={() => onChange({})}
          className="p-1 text-brand hover:text-danger transition-colors" title="Снять ссылку">
          <Unlink size={13} />
        </button>
      </div>
    );
  }

  const subValues = (value != null && typeof value === 'object' && !isFieldRef(value)
    ? value : {}) as Record<string, unknown>;
  const subFields = compositeType ? resolveEffectiveFields(compositeType, allDocTypes) : [];

  function setSubValue(key: string, val: unknown) {
    onChange({ ...subValues, [key]: val });
  }

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
          ) : subFields.map(sf => {
            const subVal = subValues[sf.key];
            const invalid = showValidation && isMissing(sf, subVal);
            return (
              <div key={sf.key}>
                {sf.type !== 'boolean' && (
                  <label className="block text-sm font-medium text-fg2 mb-1">
                    {sf.title}
                    {sf.required && <span className="ml-0.5 text-danger">*</span>}
                  </label>
                )}
                {sf.type === 'complex' ? (
                  <ComplexFieldGroup field={sf} allDocTypes={allDocTypes} value={subVal}
                    onChange={v => setSubValue(sf.key, v)} showValidation={showValidation}
                    setId={setId} otherInstances={otherInstances}
                    scope={scope} scopeId={scopeId} docRefMode={docRefMode} />
                ) : sf.type === 'doc-ref' ? (
                  docRefMode === 'instance' ? (
                    <DocRefField field={sf} allDocTypes={allDocTypes} value={subVal}
                      onChange={v => setSubValue(sf.key, v ?? undefined)}
                      otherInstances={otherInstances} setId={setId} />
                  ) : (
                    <DocRefCatalogPickerField field={sf} allDocTypes={allDocTypes} value={subVal}
                      onChange={v => setSubValue(sf.key, v ?? undefined)}
                      setId={setId} scope={scope ?? 'System'} scopeId={scopeId ?? null} />
                  )
                ) : sf.type === 'doc-array' && docRefMode === 'instance' ? (
                  <DocArrayField field={sf} allDocTypes={allDocTypes} value={subVal}
                    onChange={v => setSubValue(sf.key, v)}
                    otherInstances={otherInstances} setId={setId} />
                ) : sf.type === 'image' ? (
                  <ImageField value={subVal} onChange={v => setSubValue(sf.key, v)} />
                ) : sf.type === 'file' ? (
                  <FileField value={subVal} onChange={v => setSubValue(sf.key, v ?? undefined)} />
                ) : sf.type === 'array' ? (
                  <ArrayFieldEditor field={sf} allDocTypes={allDocTypes} value={subVal}
                    onChange={v => setSubValue(sf.key, v)} showValidation={showValidation}
                    setId={setId} otherInstances={otherInstances}
                    scope={scope} scopeId={scopeId} docRefMode={docRefMode} />
                ) : (
                  <PrimitiveInput field={sf} value={subVal} onChange={v => setSubValue(sf.key, v)} invalid={invalid} />
                )}
                {invalid && <p className="text-xs text-danger mt-1">Обязательное поле</p>}
              </div>
            );
          })}
        </div>
      )}
      <RefPickerModal
        open={pickerOpen} onOpenChange={setPickerOpen}
        compositeType={compositeType}
        setId={setId} scope={scope} scopeId={scopeId}
        otherInstances={otherInstances}
        allDocTypes={allDocTypes}
        onSelect={ref => onChange(ref)}
      />
    </div>
  );
}
