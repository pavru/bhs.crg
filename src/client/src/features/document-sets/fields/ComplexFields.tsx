import { useState, useEffect, useRef, type ReactNode } from 'react';
import {
  Clipboard, ChevronDown, ChevronUp, Database, FileSpreadsheet, Link2, Pencil, Plus, Trash2, Unlink, X,
} from 'lucide-react';
import { DateInput } from '@/shared/ui/DateInput';
import { Modal } from '@/shared/ui/Modal';
import { useCommonDataForScope } from '@/shared/api/commonData';
import type {
  CatalogScope, DocumentInstance, DocumentType, FieldRef, PrimitiveTypeDef,
} from '@/shared/api/types';
import { isFieldRef, SCOPE_LABELS } from '@/shared/api/types';
import { useListPrimitiveTypes } from '@/shared/api/primitiveTypes';
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

export function TableCell({ field, value, onChange, compositeType, setId, allDocTypes, scope, scopeId, primitiveTypeDef }: {
  field: SchemaField; value: unknown; onChange: (v: unknown) => void;
  compositeType: DocumentType | null;
  setId?: string; allDocTypes: DocumentType[];
  scope?: CatalogScope; scopeId?: string | null;
  primitiveTypeDef?: PrimitiveTypeDef;
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
  // primitive-тип на базе date (issue #60) — иначе рендерился обычным текст-инпутом без DateInput/точности
  if (field.type === 'primitive' && primitiveTypeDef?.baseType === 'date') {
    return <DateInput value={strVal} onChange={v => onChange(v)}
      precision={primitiveTypeDef.constraints.datePrecision ?? 'day'}
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

  // Единая цепочка скопов (issue #82): комплект → (Set, setId), иначе (scope, scopeId) — с подъёмом по родителям.
  const { data: commonDataEntries = [] } = useCommonDataForScope({
    scope: setId ? 'Set' : scope, scopeId: setId ?? scopeId, enabled: open && (!!setId || !!scope),
  });
  const { data: primitiveTypes = [] } = useListPrimitiveTypes();
  const primDef = (f: SchemaField) => f.type === 'primitive' ? primitiveTypes.find(pt => pt.id === f.typeId) : undefined;

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

  // ↑↓-навигация между ячейками ОДНОЙ колонки (issue #107, F8a). Полный APG grid (←→, роли,
  // выделение строк, ресайз с клавиатуры) отложен в фазу таблиц MD3. <select> не трогаем — там
  // ↑↓ выбирают опцию; для остальных (text/number/date/checkbox/пикер) нативное ↑↓ — no-op либо
  // нежелательный инкремент, так что перехват безопасен и полезен при вводе столбца сверху вниз.
  const tableRef = useRef<HTMLTableElement>(null);
  // Фокус на контрол ячейки (r,c). true — удалось.
  function focusCell(r: number, c: number): boolean {
    const target = tableRef.current?.querySelector<HTMLElement>(`td[data-r="${r}"][data-c="${c}"]`);
    const f = target?.querySelector<HTMLElement>('input, select, textarea, button');
    if (!f) return false;
    f.focus();
    if (f instanceof HTMLInputElement && f.type !== 'checkbox') f.select();
    return true;
  }
  // APG grid-навигация (issue #107 F8b): ↑↓ — строки; ←→ — колонки, но для текст-инпута только
  // когда каретка на краю (иначе стрелка двигает курсор). <select> хранит ↑↓ за собой (опции).
  function onGridKey(e: React.KeyboardEvent) {
    const el = e.target as HTMLElement;
    const td = el.closest('td[data-r]') as HTMLElement | null;
    if (!td) return;
    const r = Number(td.dataset.r), c = Number(td.dataset.c);
    const input = el instanceof HTMLInputElement ? el : null;
    const isText = !!input && input.type !== 'checkbox';
    const atStart = !isText || (input!.selectionStart === 0 && input!.selectionEnd === 0);
    const atEnd = !isText || (input!.selectionStart === input!.value.length && input!.selectionEnd === input!.value.length);

    if (e.key === 'ArrowUp' || e.key === 'ArrowDown') {
      if (el.tagName === 'SELECT') return;
      const nr = e.key === 'ArrowUp' ? r - 1 : r + 1;
      if (nr < 0 || nr >= rows.length) return;
      if (focusCell(nr, c)) e.preventDefault();
    } else if (e.key === 'ArrowLeft') {
      if (!atStart || c - 1 < 0) return;
      if (focusCell(r, c - 1)) e.preventDefault();
    } else if (e.key === 'ArrowRight') {
      if (!atEnd || c + 1 >= tableFields.length) return;
      if (focusCell(r, c + 1)) e.preventDefault();
    }
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
        <table ref={tableRef} onKeyDown={onGridKey} role="grid" aria-label={`Строки: ${compositeType?.name ?? field.title}`}
          style={{ tableLayout: 'fixed', borderCollapse: 'collapse', width: 'max-content', minWidth: '100%' }}>
          <colgroup>
            <col style={{ width: 32 }} />
            {tableFields.map(f => <col key={f.key} style={{ width: getW(f) }} />)}
            <col style={{ width: 26 }} />
          </colgroup>
          <thead>
            <tr role="row">
              <th role="columnheader" style={{ border: BORDER, background: TH_BG, padding: 0, width: 32 }}>
                <span className="flex items-center justify-center text-xs text-fg4 font-normal" style={{ height: 28 }}>#</span>
              </th>
              {tableFields.map(f => (
                <th key={f.key} role="columnheader"
                  style={{ border: BORDER, background: TH_BG, padding: 0, position: 'relative', userSelect: 'none' }}>
                  <span className="flex items-center px-2 text-left text-xs font-semibold text-fg2 truncate" style={{ height: 28 }}>
                    {f.title}{f.required && <span className="text-danger ml-0.5">*</span>}
                  </span>
                  <div role="separator" aria-orientation="vertical" tabIndex={0}
                    aria-label={`Ширина колонки «${f.title}» — стрелки ←→`}
                    onMouseDown={e => startResize(e, f.key, getW(f))}
                    onKeyDown={e => {
                      if (e.key === 'ArrowLeft') { e.preventDefault(); setColWidths(p => ({ ...p, [f.key]: Math.max(44, getW(f) - 16) })); }
                      else if (e.key === 'ArrowRight') { e.preventDefault(); setColWidths(p => ({ ...p, [f.key]: getW(f) + 16 })); }
                    }}
                    className="hover:bg-brand-subtle/40 focus-visible:bg-brand focus-visible:outline-none transition-colors"
                    style={{ position: 'absolute', right: 0, top: 0, bottom: 0, width: 5, cursor: 'col-resize', zIndex: 1 }} />
                </th>
              ))}
              <th style={{ border: BORDER, background: TH_BG, padding: 0, width: 26 }} />
            </tr>
          </thead>
          <tbody>
            {rows.map((row, i) => (
              <tr key={i} role="row">
                <td role="rowheader" style={{ border: BORDER, background: IDX_BG, padding: 0, textAlign: 'center' }}>
                  <span className="flex items-center justify-center text-xs text-fg4 font-mono" style={{ height: 26 }}>{i + 1}</span>
                </td>
                {tableFields.map((f, ci) => {
                  const compositeForField = f.type === 'complex'
                    ? allDocTypes.find(dt => dt.id === f.typeId) ?? null : null;
                  return (
                    <td key={f.key} data-r={i} data-c={ci} role="gridcell"
                      className="focus-within:bg-brand-subtle transition-colors"
                      style={{ border: BORDER, padding: 0, height: 26 }}>
                      <TableCell field={f} value={row[f.key]} onChange={v => updateCell(i, f.key, v)}
                        compositeType={compositeForField} setId={setId} allDocTypes={allDocTypes}
                        scope={scope} scopeId={scopeId} primitiveTypeDef={primDef(f)} />
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
  const [rowModal, setRowModal] = useState<number | null>(null); // issue #102: строка массива правится в модалке, не инлайн
  const [tableOpen, setTableOpen] = useState(false);
  const [collapsed, setCollapsed] = useState(true);
  const [catalogPickerOpen, setCatalogPickerOpen] = useState(false);

  const hasTableFields = subFields.some(f => TABLE_SHOWN_TYPES.has(f.type));
  const { data: primitiveTypes = [] } = useListPrimitiveTypes();
  const primDef = (f: SchemaField) => f.type === 'primitive' ? primitiveTypes.find(pt => pt.id === f.typeId) : undefined;

  function addRow() {
    const newRow = getDefaultValues(subFields);
    onChange([...allItems, newRow]);
    setRowModal(allItems.length); // сразу открыть модалку новой строки
  }

  function addFromCatalog(ref: FieldRef) {
    onChange([...allItems, ref]);
  }

  function removeItem(i: number) {
    onChange(allItems.filter((_, idx) => idx !== i));
    setRowModal(null);
  }

  function updateRow(i: number, row: Record<string, unknown>) {
    onChange(allItems.map((it, idx) => idx === i ? row : it));
  }

  function rowSummary(row: Record<string, unknown>) {
    return objectSummary(row, subFields);
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
            // issue #102: строка — компактная сводка + ✎ (модалка), без инлайн-раскрытия (источник «портянки»).
            return (
              <div key={i} className="flex items-center gap-2 px-3 py-2 hover:bg-base">
                <span className="text-xs text-fg4 font-mono w-5 text-right shrink-0">{i + 1}</span>
                <button type="button" onClick={() => setRowModal(i)}
                  className="flex-1 text-left text-sm text-fg2 hover:text-fg1 truncate">
                  {rowSummary(row)}
                </button>
                <button type="button" onClick={() => setRowModal(i)} title="Редактировать"
                  className="p-1 text-fg4 hover:text-fg2 shrink-0">
                  <Pencil size={13} />
                </button>
                <button type="button" onClick={() => removeItem(i)}
                  className="p-1 text-fg4 hover:text-danger shrink-0">
                  <Trash2 size={13} />
                </button>
              </div>
            );
          })}
        </div>
      )}
      {rowModal !== null && allItems[rowModal] != null && !isFieldRef(allItems[rowModal]) && (
        <Modal open onOpenChange={o => { if (!o) setRowModal(null); }} wide
          title={`${compositeType?.name ?? field.title} — строка ${rowModal + 1}`}
          footer={
            <div className="flex justify-end">
              <button type="button" onClick={() => setRowModal(null)}
                className="px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md transition-colors">Готово</button>
            </div>
          }>
          <div className="px-6 py-4 space-y-3">
            {subFields.map(sf => {
              const rowObj = allItems[rowModal] as Record<string, unknown>;
              const subVal = rowObj[sf.key];
              const invalid = showValidation && isMissing(sf, subVal);
              return (
                <div key={sf.key}>
                  {['complex', 'array', 'doc-ref', 'doc-array', 'image', 'file'].includes(sf.type) && (
                    <label className="block text-sm font-medium text-fg2 mb-1">
                      {sf.title}{sf.required && <span className="ml-0.5 text-danger">*</span>}
                    </label>
                  )}
                  {sf.type === 'complex' ? (
                    <ComplexFieldGroup field={sf} allDocTypes={allDocTypes} value={subVal}
                      onChange={v => updateRow(rowModal, { ...rowObj, [sf.key]: v })}
                      showValidation={showValidation} setId={setId}
                      otherInstances={otherInstances} scope={scope} scopeId={scopeId}
                      docRefMode={docRefMode} nested />
                  ) : sf.type === 'doc-ref' ? (
                    docRefMode === 'instance' ? (
                      <DocRefField field={sf} allDocTypes={allDocTypes} value={subVal}
                        onChange={v => updateRow(rowModal, { ...rowObj, [sf.key]: v ?? undefined })}
                        otherInstances={otherInstances} setId={setId} />
                    ) : (
                      <DocRefCatalogPickerField field={sf} allDocTypes={allDocTypes} value={subVal}
                        onChange={v => updateRow(rowModal, { ...rowObj, [sf.key]: v ?? undefined })}
                        setId={setId} scope={scope ?? 'System'} scopeId={scopeId ?? null} />
                    )
                  ) : sf.type === 'doc-array' && docRefMode === 'instance' ? (
                    <DocArrayField field={sf} allDocTypes={allDocTypes} value={subVal}
                      onChange={v => updateRow(rowModal, { ...rowObj, [sf.key]: v })}
                      otherInstances={otherInstances} setId={setId} />
                  ) : (
                    <PrimitiveInput field={sf} value={subVal} label={sf.title}
                      onChange={v => updateRow(rowModal, { ...rowObj, [sf.key]: v })} invalid={invalid}
                      primitiveTypeDef={primDef(sf)} />
                  )}
                  {invalid && <p className="text-xs text-danger mt-0.5">Обязательное поле</p>}
                </div>
              );
            })}
          </div>
        </Modal>
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

/** Сводка первых заполненных полей объекта — для свёрнутого/строкового вида составного (issue #102). */
export function objectSummary(values: Record<string, unknown>, fields: SchemaField[]): string {
  const parts = fields
    .map(f => {
      const v = values[f.key];
      if (v == null || v === '') return null;
      if (isFieldRef(v)) return v.displayName;
      if (typeof v === 'object') return null; // вложенные объекты/массивы — не в сводку
      return String(v);
    })
    .filter((s): s is string => !!s)
    .slice(0, 3);
  return parts.length ? parts.join(' · ') : '(пусто)';
}

/** Сворачиваемая секция «Заполняются автоматически» (issue #102, P2): read-only поля из источника
 *  прячем по умолчанию, чтобы длинная форма не выглядела «портянкой» одинаковых боксов. */
export function AutoFieldsSection({ count, children }: { count: number; children: ReactNode }) {
  const [open, setOpen] = useState(false);
  return (
    <div className="border border-dashed border-stroke rounded-lg overflow-hidden">
      <button type="button" onClick={() => setOpen(v => !v)} aria-expanded={open}
        className="w-full flex items-center gap-2 px-3 py-2 bg-base/40 hover:bg-base transition-colors text-left">
        {open ? <ChevronUp size={12} className="text-fg4 shrink-0" /> : <ChevronDown size={12} className="text-fg4 shrink-0" />}
        <Database size={11} className="text-brand shrink-0" />
        <span className="text-xs text-fg3 flex-1">Заполняются автоматически</span>
        <span className="text-xs text-fg4">{count} п.</span>
      </button>
      {open && <div className="px-3 py-3 border-t border-stroke">{children}</div>}
    </div>
  );
}

export function ComplexFieldGroup({ field, allDocTypes, value, onChange, showValidation,
  setId, otherInstances = [],
  scope, scopeId, docRefMode = 'catalog', nested = false,
}: {
  field: SchemaField; allDocTypes: DocumentType[]; value: unknown;
  onChange: (val: Record<string, unknown> | FieldRef) => void;
  showValidation: boolean;
  setId?: string; otherInstances?: DocumentInstance[];
  scope?: CatalogScope; scopeId?: string | null;
  docRefMode?: 'catalog' | 'instance';
  // issue #102: вложенное составное (глубина ≥1) правится в МОДАЛКЕ, а не инлайн — защита от «портянки»/матрёшки.
  nested?: boolean;
}) {
  const [pickerOpen, setPickerOpen] = useState(false);
  const [collapsed, setCollapsed] = useState(true);
  const [modalOpen, setModalOpen] = useState(false);
  const { data: primitiveTypes = [] } = useListPrimitiveTypes();
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
  const primDef = (f: SchemaField) => f.type === 'primitive' ? primitiveTypes.find(pt => pt.id === f.typeId) : undefined;
  const isEmpty = subFields.every(f => { const v = subValues[f.key]; return v == null || v === ''; });

  function setSubValue(key: string, val: unknown) {
    onChange({ ...subValues, [key]: val });
  }

  // Тело редактора подполей. Вложенные complex → nested (модалка), массивы → ArrayFieldEditor.
  const subfieldsBody = (
    <div className="space-y-3">
      {subFields.length === 0 ? (
        <p className="text-xs text-fg4">Поля не заданы</p>
      ) : subFields.map(sf => {
        const subVal = subValues[sf.key];
        const invalid = showValidation && isMissing(sf, subVal);
        return (
          <div key={sf.key}>
            {['complex', 'array', 'doc-ref', 'doc-array', 'image', 'file'].includes(sf.type) && (
              <label className="block text-sm font-medium text-fg2 mb-1">
                {sf.title}
                {sf.required && <span className="ml-0.5 text-danger">*</span>}
              </label>
            )}
            {sf.type === 'complex' ? (
              <ComplexFieldGroup field={sf} allDocTypes={allDocTypes} value={subVal}
                onChange={v => setSubValue(sf.key, v)} showValidation={showValidation}
                setId={setId} otherInstances={otherInstances}
                scope={scope} scopeId={scopeId} docRefMode={docRefMode} nested />
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
              <PrimitiveInput field={sf} value={subVal} label={sf.title} onChange={v => setSubValue(sf.key, v)} invalid={invalid}
                primitiveTypeDef={primDef(sf)} />
            )}
            {invalid && <p className="text-xs text-danger mt-1">Обязательное поле</p>}
          </div>
        );
      })}
    </div>
  );

  const picker = (
    <RefPickerModal
      open={pickerOpen} onOpenChange={setPickerOpen}
      compositeType={compositeType}
      setId={setId} scope={scope} scopeId={scopeId}
      otherInstances={otherInstances}
      allDocTypes={allDocTypes}
      onSelect={ref => onChange(ref)}
    />
  );

  // Вложенное составное (глубина ≥1): строка-сводка + правка в модалке — глубина формы не растёт.
  if (nested) {
    return (
      <>
        <div className="flex items-center gap-2 border border-stroke rounded-lg px-3 py-2 bg-base">
          <button type="button" onClick={() => setModalOpen(true)}
            className="flex-1 min-w-0 text-left text-sm text-fg2 hover:text-fg1 truncate">
            {isEmpty ? <span className="text-fg4">Заполнить…</span> : objectSummary(subValues, subFields)}
          </button>
          <button type="button" onClick={() => setPickerOpen(true)}
            className="flex items-center gap-1 text-xs text-brand hover:text-brand-hover px-2 py-0.5 rounded hover:bg-brand-subtle transition-colors shrink-0">
            <Link2 size={11} /> Из каталога
          </button>
          <button type="button" onClick={() => setModalOpen(true)} title="Редактировать"
            className="p-1 text-fg4 hover:text-fg2 transition-colors shrink-0">
            <Pencil size={13} />
          </button>
        </div>
        <Modal open={modalOpen} onOpenChange={setModalOpen} wide
          title={compositeType ? `${compositeType.name}${field.title !== compositeType.name ? ` — ${field.title}` : ''}` : field.title}
          footer={
            <div className="flex justify-end">
              <button type="button" onClick={() => setModalOpen(false)}
                className="px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md transition-colors">Готово</button>
            </div>
          }>
          <div className="px-6 py-4">{subfieldsBody}</div>
        </Modal>
        {picker}
      </>
    );
  }

  // Пустое составное (верхний уровень): продвигаем ВЫБОР между «из каталога» (ссылка-объект) и ручным
  // заполнением как равноправные действия, а не прячем «из каталога» в мелкую ссылку (issue #102, P2).
  if (isEmpty && collapsed) {
    return (
      <div className="border border-dashed border-stroke rounded-lg px-3 py-3 bg-base/40">
        <div className="text-sm text-fg3 mb-2">{compositeType ? compositeType.name : 'Составной тип'}</div>
        <div className="flex flex-wrap gap-2">
          <button type="button" onClick={() => setPickerOpen(true)}
            className="flex items-center gap-1.5 text-sm bg-brand hover:bg-brand-hover text-white px-3 py-1.5 rounded-md transition-colors">
            <Link2 size={13} /> Выбрать из каталога
          </button>
          <button type="button" onClick={() => setCollapsed(false)}
            className="flex items-center gap-1.5 text-sm text-fg2 hover:text-fg1 border border-stroke hover:border-fg4 px-3 py-1.5 rounded-md transition-colors">
            <Pencil size={13} /> Заполнить вручную
          </button>
        </div>
        {picker}
      </div>
    );
  }

  // Верхний уровень (инлайн, глубина 0): свёрнуто по умолчанию, заголовок — сводка значений (не «Тип (код)»).
  return (
    <div className="border border-stroke rounded-lg overflow-hidden">
      <div className={`flex items-center justify-between px-3 py-2 bg-base ${collapsed ? '' : 'border-b border-stroke'}`}>
        <button type="button" onClick={() => setCollapsed(v => !v)}
          className="flex items-center gap-1.5 min-w-0 text-sm font-medium text-fg2 hover:text-fg1 transition-colors">
          {collapsed ? <ChevronDown size={12} className="shrink-0 text-fg4" /> : <ChevronUp size={12} className="shrink-0 text-fg4" />}
          <span className="truncate">
            {isEmpty
              ? <span className="text-fg4 font-normal">{compositeType ? compositeType.name : 'Составной тип'}</span>
              : objectSummary(subValues, subFields)}
          </span>
        </button>
        <button type="button" onClick={() => setPickerOpen(true)}
          className="flex items-center gap-1.5 text-xs text-brand hover:text-brand-hover px-2 py-0.5 rounded hover:bg-brand-subtle transition-colors shrink-0">
          <Link2 size={11} /> Выбрать из каталога
        </button>
      </div>
      {!collapsed && <div className="px-3 py-3">{subfieldsBody}</div>}
      {picker}
    </div>
  );
}
