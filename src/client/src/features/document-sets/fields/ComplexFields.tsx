import { useState, useEffect, useRef, type ReactNode } from 'react';
import {
  Clipboard, ChevronDown, ChevronUp, Database, FileSpreadsheet, GripVertical, Info, Link2, Pencil, Plus, RefreshCw, Trash2, Unlink, X,
} from 'lucide-react';
import { FUNCTIONAL_TAG } from '@/shared/api/tags';
import { DateInput } from '@/shared/ui/DateInput';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
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
import { BROKEN_PLATE, BROKEN_LABEL, BrokenRefNote } from './BrokenRef';

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
    return <DateInput value={strVal} onChange={v => onChange(v)} compact
      className="w-full h-full flex items-center px-1.5 focus-within:bg-brand-subtle" />;
  }
  // primitive-тип на базе date (issue #60) — иначе рендерился обычным текст-инпутом без DateInput/точности
  if (field.type === 'primitive' && primitiveTypeDef?.baseType === 'date') {
    return <DateInput value={strVal} onChange={v => onChange(v)} compact
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
  // Стабильные id строк (issue #171): переживают reorder/удаление, служат ключом выбора.
  const [rowIds, setRowIds] = useState<string[]>([]);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [dragIdx, setDragIdx] = useState<number | null>(null);
  const [dropIdx, setDropIdx] = useState<number | null>(null);
  const [colWidths, setColWidths] = useState<Record<string, number>>({});
  const [pasteOpen, setPasteOpen] = useState(false);
  const [pasteText, setPasteText] = useState('');

  useEffect(() => {
    if (open) {
      setRows(items.map(r => ({ ...r })));
      setRowIds(items.map(() => crypto.randomUUID()));
      setSelected(new Set());
    }
  }, [open]); // eslint-disable-line react-hooks/exhaustive-deps

  // Единый scope-контекст владельца (issue #82): комплект → (Set, setId), иначе (scope, scopeId).
  const resolveScope = setId ? 'Set' as const : scope;
  const resolveScopeId = setId ?? scopeId;
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
  function addRow() {
    setRows(prev => [...prev, getDefaultValues(subFields)]);
    setRowIds(prev => [...prev, crypto.randomUUID()]);
  }
  function removeRow(idx: number) {
    const id = rowIds[idx];
    setRows(prev => prev.filter((_, i) => i !== idx));
    setRowIds(prev => prev.filter((_, i) => i !== idx));
    if (id) setSelected(prev => { const n = new Set(prev); n.delete(id); return n; });
  }
  function handleSave() { onSave(rows); onOpenChange(false); }

  // ── Выбор строк (issue #171) ────────────────────────────────────────────
  function toggleSelect(id: string) {
    setSelected(prev => { const n = new Set(prev); n.has(id) ? n.delete(id) : n.add(id); return n; });
  }
  function toggleAll() {
    setSelected(prev => prev.size === rowIds.length ? new Set() : new Set(rowIds));
  }
  function deleteSelected() {
    setRows(prev => prev.filter((_, i) => !selected.has(rowIds[i])));
    setRowIds(prev => prev.filter(id => !selected.has(id)));
    setSelected(new Set());
  }

  // ── Изменение порядка строк: drag-and-drop + клавиатура (issue #171) ─────
  function moveRow(from: number, to: number) {
    if (to < 0 || to >= rows.length || from === to) return;
    setRows(prev => { const a = [...prev]; const [m] = a.splice(from, 1); a.splice(to, 0, m); return a; });
    setRowIds(prev => { const a = [...prev]; const [m] = a.splice(from, 1); a.splice(to, 0, m); return a; });
  }

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

  return (
    <Modal open={open} onOpenChange={onOpenChange}
      title={`${compositeType?.name ?? field.title} — таблица`}
      extraWide
      footer={
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-1">
            {selected.size > 0 ? (
              <>
                <span className="text-sm font-medium text-fg2 px-2">Выбрано: {selected.size}</span>
                <Button variant="text" size="sm" danger icon={<Trash2 size={13} />} onClick={deleteSelected}>Удалить выбранные</Button>
                <Button variant="text" size="sm" onClick={() => setSelected(new Set())}>Сбросить</Button>
              </>
            ) : (
              <>
                <Button variant="text" size="sm" icon={<Plus size={13} />} onClick={addRow}>Добавить строку</Button>
                <span className="text-stroke-strong">·</span>
                <Button variant="text" size="sm" icon={<Clipboard size={13} />} onClick={handlePasteClick}>Вставить из Excel</Button>
              </>
            )}
          </div>
          <div className="flex gap-2">
            <Button variant="text" onClick={() => onOpenChange(false)}>Отмена</Button>
            <Button variant="filled" onClick={handleSave}>Применить</Button>
          </div>
        </div>
      }>
      <div className="overflow-x-auto -mx-6 px-6">
        <table ref={tableRef} onKeyDown={onGridKey} role="grid" aria-label={`Строки: ${compositeType?.name ?? field.title}`}
          style={{ tableLayout: 'fixed', borderCollapse: 'collapse', width: 'max-content', minWidth: '100%' }}>
          <colgroup>
            <col style={{ width: 34 }} />
            <col style={{ width: 44 }} />
            {tableFields.map(f => <col key={f.key} style={{ width: getW(f) }} />)}
            <col style={{ width: 26 }} />
          </colgroup>
          <thead>
            <tr role="row">
              <th style={{ border: BORDER, background: TH_BG, padding: 0, width: 34 }}>
                <span className="flex items-center justify-center" style={{ height: 28 }}>
                  <input type="checkbox" aria-label="Выбрать все строки"
                    checked={rowIds.length > 0 && selected.size === rowIds.length}
                    ref={el => { if (el) el.indeterminate = selected.size > 0 && selected.size < rowIds.length; }}
                    onChange={toggleAll} className="w-4 h-4 accent-brand cursor-pointer" />
                </span>
              </th>
              <th role="columnheader" style={{ border: BORDER, background: TH_BG, padding: 0, width: 44 }}>
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
            {rows.map((row, i) => {
              const sel = selected.has(rowIds[i]);
              return (
              <tr key={rowIds[i]} role="row"
                onDragOver={e => { if (dragIdx !== null) { e.preventDefault(); if (dropIdx !== i) setDropIdx(i); } }}
                onDrop={e => { e.preventDefault(); if (dragIdx !== null) moveRow(dragIdx, i); setDragIdx(null); setDropIdx(null); }}
                style={dragIdx !== null && dropIdx === i && dragIdx !== i
                  ? { outline: '2px solid var(--color-brand)', outlineOffset: '-2px' } : undefined}>
                <td style={{ border: BORDER, padding: 0, textAlign: 'center' }} className={sel ? 'bg-brand-subtle' : ''}>
                  <span className="flex items-center justify-center" style={{ height: 26 }}>
                    <input type="checkbox" checked={sel} onChange={() => toggleSelect(rowIds[i])}
                      aria-label={`Выбрать строку ${i + 1}`} className="w-4 h-4 accent-brand cursor-pointer" />
                  </span>
                </td>
                <td role="rowheader" style={{ border: BORDER, padding: 0 }} className={sel ? 'bg-brand-subtle' : 'bg-base'}>
                  <div className="flex items-center justify-center gap-0.5" style={{ height: 26 }}>
                    <button type="button" draggable
                      onDragStart={e => { setDragIdx(i); e.dataTransfer.effectAllowed = 'move'; }}
                      onDragEnd={() => { setDragIdx(null); setDropIdx(null); }}
                      onKeyDown={e => {
                        if (e.key === 'ArrowUp') { e.preventDefault(); e.stopPropagation(); moveRow(i, i - 1); }
                        else if (e.key === 'ArrowDown') { e.preventDefault(); e.stopPropagation(); moveRow(i, i + 1); }
                      }}
                      title="Перетащить для изменения порядка (или стрелки ↑↓)"
                      aria-label={`Переместить строку ${i + 1}: стрелки вверх/вниз`}
                      className="cursor-grab active:cursor-grabbing text-fg4 hover:text-fg2 focus-visible:outline-none focus-visible:text-brand">
                      <GripVertical size={12} />
                    </button>
                    <span className="text-xs text-fg4 font-mono">{i + 1}</span>
                  </div>
                </td>
                {tableFields.map((f, ci) => {
                  const compositeForField = f.type === 'complex'
                    ? allDocTypes.find(dt => dt.id === f.typeId) ?? null : null;
                  return (
                    <td key={f.key} data-r={i} data-c={ci} role="gridcell"
                      className={`focus-within:bg-brand-subtle transition-colors ${sel ? 'bg-brand-subtle' : ''}`}
                      style={{ border: BORDER, padding: 0, height: 26 }}>
                      <TableCell field={f} value={row[f.key]} onChange={v => updateCell(i, f.key, v)}
                        compositeType={compositeForField} setId={setId} allDocTypes={allDocTypes}
                        scope={scope} scopeId={scopeId} primitiveTypeDef={primDef(f)} />
                    </td>
                  );
                })}
                <td style={{ border: BORDER, padding: 0, width: 26 }} className={sel ? 'bg-brand-subtle' : ''}>
                  <button type="button" onClick={() => removeRow(i)}
                    className="w-full h-full flex items-center justify-center text-stroke-strong hover:text-danger transition-colors"
                    style={{ height: 26 }}>
                    <Trash2 size={11} />
                  </button>
                </td>
              </tr>
              );
            })}
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
        scope={resolveScope} scopeId={resolveScopeId}
        onApply={newRows => {
          setRows(prev => [...prev, ...newRows]);
          setRowIds(prev => [...prev, ...newRows.map(() => crypto.randomUUID())]);
        }}
      />
    </Modal>
  );
}

// ─── Array field editor ───────────────────────────────────────────────────────

export function ArrayFieldEditor({ field, allDocTypes, value, onChange, showValidation,
  setId, otherInstances = [], scope, scopeId, docRefMode = 'catalog', brokenPaths, basePath,
}: {
  field: SchemaField; allDocTypes: DocumentType[]; value: unknown;
  onChange: (val: unknown[]) => void; showValidation: boolean;
  setId?: string; otherInstances?: DocumentInstance[];
  scope?: CatalogScope; scopeId?: string | null;
  docRefMode?: 'catalog' | 'instance';
  /** Пути битых ссылок (issue #332) + базовый путь массива — для пометки элементов-ссылок на удалённое. */
  brokenPaths?: Set<string>; basePath?: string;
}) {
  const compositeType = allDocTypes.find(dt => dt.id === field.typeId) ?? null;
  const allItems = Array.isArray(value) ? value as unknown[] : [];
  const inlineItems = allItems.filter(item => !isFieldRef(item)) as Record<string, unknown>[];
  const subFields = compositeType ? resolveEffectiveFields(compositeType, allDocTypes) : [];
  // Строка массива union-типа (issue #320): редактируется переключателем варианта, а не стопкой всех
  // полей — иначе диалог строки показывал оба поля union (баг). Тип union = тэг type.union на схеме.
  const isUnionComposite = !!compositeType
    && ((compositeType.schema as { tags?: string[] }).tags ?? []).includes(FUNCTIONAL_TAG.typeUnion);
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
              const itemBroken = !!basePath && !!brokenPaths?.has(`${basePath}[${i}]`);
              if (itemBroken) {
                return (
                  <div key={i}>
                    <div className={`flex items-center gap-2 px-3 py-2 ${BROKEN_PLATE}`}>
                      <span className="text-xs text-danger font-mono w-5 text-right shrink-0">{i + 1}</span>
                      <Link2 size={12} className="text-danger shrink-0" />
                      <span className={`flex-1 text-sm truncate ${BROKEN_LABEL}`}>{item.displayName}</span>
                      <button type="button" onClick={() => removeItem(i)}
                        className="p-1 text-danger hover:text-fg1 shrink-0"><Trash2 size={13} /></button>
                    </div>
                    <BrokenRefNote compact />
                  </div>
                );
              }
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
              <Button variant="filled" onClick={() => setRowModal(null)}>Готово</Button>
            </div>
          }>
          <div className="px-6 py-4 space-y-3">
            {isUnionComposite ? (
              <UnionFieldGroup field={field} allDocTypes={allDocTypes}
                value={allItems[rowModal] as Record<string, unknown>}
                onChange={row => updateRow(rowModal, row)}
                showValidation={showValidation} setId={setId} otherInstances={otherInstances}
                scope={scope} scopeId={scopeId} docRefMode={docRefMode} />
            ) : subFields.map(sf => {
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
  scope, scopeId, docRefMode = 'catalog', nested = false, broken = false,
}: {
  field: SchemaField; allDocTypes: DocumentType[]; value: unknown;
  onChange: (val: Record<string, unknown> | FieldRef) => void;
  showValidation: boolean;
  setId?: string; otherInstances?: DocumentInstance[];
  scope?: CatalogScope; scopeId?: string | null;
  docRefMode?: 'catalog' | 'instance';
  // issue #102: вложенное составное (глубина ≥1) правится в МОДАЛКЕ, а не инлайн — защита от «портянки»/матрёшки.
  nested?: boolean;
  /** Составное поле — ссылка на удалённую запись каталога (issue #332). */
  broken?: boolean;
}) {
  const [pickerOpen, setPickerOpen] = useState(false);
  const [collapsed, setCollapsed] = useState(true);
  const [modalOpen, setModalOpen] = useState(false);
  const { data: primitiveTypes = [] } = useListPrimitiveTypes();
  const compositeType = allDocTypes.find(dt => dt.id === field.typeId) ?? null;

  // Union-тип (issue #320): составной тип с тэгом type.union — «заполняется ровно одно из полей».
  // Рендерим переключатель варианта + редактор активного подполя вместо стопки всех подполей.
  const isUnion = !!compositeType
    && ((compositeType.schema as { tags?: string[] }).tags ?? []).includes(FUNCTIONAL_TAG.typeUnion);
  if (isUnion) {
    return (
      <UnionFieldGroup field={field} allDocTypes={allDocTypes} value={value} onChange={onChange}
        showValidation={showValidation} setId={setId} otherInstances={otherInstances}
        scope={scope} scopeId={scopeId} docRefMode={docRefMode} nested={nested} />
    );
  }

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

  if (isFieldRef(value)) {
    // Битая ссылка (issue #332): цель удалена — danger-плитка + нота вместо нейтрального контейнера.
    if (broken) {
      return (
        <div>
          <div className={`flex items-center gap-1.5 rounded-lg pl-3 pr-1.5 py-1.5 ${BROKEN_PLATE}`}>
            <Link2 size={16} className="text-danger shrink-0" />
            <span className={`flex-1 text-sm font-medium truncate ${BROKEN_LABEL}`}>{value.displayName}</span>
            <button type="button" onClick={() => setPickerOpen(true)}
              className="p-1.5 rounded-full text-danger hover:text-fg1 hover:bg-black/5 dark:hover:bg-white/10 transition-colors shrink-0" title="Заменить ссылку">
              <RefreshCw size={14} />
            </button>
            <button type="button" onClick={() => onChange({})}
              className="p-1.5 rounded-full text-danger hover:text-fg1 hover:bg-black/5 dark:hover:bg-white/10 transition-colors shrink-0" title="Снять ссылку">
              <Unlink size={14} />
            </button>
          </div>
          <BrokenRefNote />
          {picker}
        </div>
      );
    }
    // Link-строка (issue #189): нейтральный контейнер, имя — ссылка primary, тональный chip источника,
    // два действия — «заменить» (открыть пикер) и «снять».
    return (
      <div className="flex items-center gap-1.5 border border-stroke rounded-lg pl-3 pr-1.5 py-1.5 bg-base">
        <Link2 size={16} className="text-fg4 shrink-0" />
        <span className="flex-1 text-sm text-brand font-medium truncate">{value.displayName}</span>
        {value.scope && (
          <span className={`text-xs px-2 py-0.5 rounded-full font-medium shrink-0 ${SCOPE_COLORS[value.scope]}`}>
            {SCOPE_LABELS[value.scope]}
          </span>
        )}
        <button type="button" onClick={() => setPickerOpen(true)}
          className="p-1.5 rounded-full text-fg4 hover:text-brand hover:bg-black/5 dark:hover:bg-white/10 transition-colors shrink-0" title="Заменить ссылку">
          <RefreshCw size={14} />
        </button>
        <button type="button" onClick={() => onChange({})}
          className="p-1.5 rounded-full text-fg4 hover:text-danger hover:bg-black/5 dark:hover:bg-white/10 transition-colors shrink-0" title="Снять ссылку">
          <Unlink size={14} />
        </button>
        {picker}
      </div>
    );
  }

  const subValues = (value != null && typeof value === 'object' && !isFieldRef(value)
    ? value : {}) as Record<string, unknown>;
  const subFields = compositeType ? resolveEffectiveFields(compositeType, allDocTypes) : [];
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
            <SubfieldEditor sf={sf} value={subVal} onChange={v => setSubValue(sf.key, v)}
              allDocTypes={allDocTypes} showValidation={showValidation} setId={setId}
              otherInstances={otherInstances} scope={scope} scopeId={scopeId}
              docRefMode={docRefMode} primitiveTypes={primitiveTypes} />
            {invalid && <p className="text-xs text-danger mt-1">Обязательное поле</p>}
          </div>
        );
      })}
    </div>
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
              <Button variant="filled" onClick={() => setModalOpen(false)}>Готово</Button>
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
          <Button variant="tonal" size="sm" icon={<Link2 size={13} />} onClick={() => setPickerOpen(true)}>
            Выбрать из каталога
          </Button>
          <Button variant="text" size="sm" icon={<Pencil size={13} />} onClick={() => setCollapsed(false)}>
            Заполнить вручную
          </Button>
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

// ─── Один подполе-редактор (диспетчеризация по типу) ───────────────────────────
// Извлечено из ComplexFieldGroup, чтобы переиспользовать для активного варианта union (issue #320).
function SubfieldEditor({ sf, value, onChange, allDocTypes, showValidation, setId,
  otherInstances = [], scope, scopeId, docRefMode = 'catalog', primitiveTypes }: {
  sf: SchemaField; value: unknown; onChange: (v: unknown) => void;
  allDocTypes: DocumentType[]; showValidation: boolean; setId?: string;
  otherInstances?: DocumentInstance[]; scope?: CatalogScope; scopeId?: string | null;
  docRefMode?: 'catalog' | 'instance'; primitiveTypes: PrimitiveTypeDef[];
}) {
  const primDef = sf.type === 'primitive' ? primitiveTypes.find(pt => pt.id === sf.typeId) : undefined;
  const invalid = showValidation && isMissing(sf, value);
  if (sf.type === 'complex')
    return <ComplexFieldGroup field={sf} allDocTypes={allDocTypes} value={value} onChange={v => onChange(v)}
      showValidation={showValidation} setId={setId} otherInstances={otherInstances}
      scope={scope} scopeId={scopeId} docRefMode={docRefMode} nested />;
  if (sf.type === 'doc-ref')
    return docRefMode === 'instance'
      ? <DocRefField field={sf} allDocTypes={allDocTypes} value={value}
          onChange={v => onChange(v ?? undefined)} otherInstances={otherInstances} setId={setId} />
      : <DocRefCatalogPickerField field={sf} allDocTypes={allDocTypes} value={value}
          onChange={v => onChange(v ?? undefined)} setId={setId} scope={scope ?? 'System'} scopeId={scopeId ?? null} />;
  if (sf.type === 'doc-array' && docRefMode === 'instance')
    return <DocArrayField field={sf} allDocTypes={allDocTypes} value={value}
      onChange={v => onChange(v)} otherInstances={otherInstances} setId={setId} />;
  if (sf.type === 'image') return <ImageField value={value} onChange={v => onChange(v)} />;
  if (sf.type === 'file') return <FileField value={value} onChange={v => onChange(v ?? undefined)} />;
  if (sf.type === 'array')
    return <ArrayFieldEditor field={sf} allDocTypes={allDocTypes} value={value} onChange={v => onChange(v)}
      showValidation={showValidation} setId={setId} otherInstances={otherInstances}
      scope={scope} scopeId={scopeId} docRefMode={docRefMode} />;
  return <PrimitiveInput field={sf} value={value} label={sf.title} onChange={v => onChange(v)}
    invalid={invalid} primitiveTypeDef={primDef} />;
}

// ─── Union-поле (issue #320): заполняется РОВНО ОДИН вариант (подполе union-типа) ──
/** Вариант считается заполненным: непустой массив / FieldRef / непустой объект / непустая строка. */
function isVariantFilled(v: unknown): boolean {
  if (v == null) return false;
  if (isFieldRef(v)) return true;
  if (Array.isArray(v)) return v.length > 0;
  if (typeof v === 'object') return Object.keys(v as object).length > 0;
  return String(v).trim() !== '';
}

function VariantSegmentedSwitch({ options, active, onSelect }: {
  options: { key: string; label: string; filled: boolean }[];
  active: string; onSelect: (key: string) => void;
}) {
  return (
    <div role="radiogroup" className="inline-flex rounded-lg border border-stroke overflow-hidden text-sm">
      {options.map((o, i) => {
        const on = o.key === active;
        return (
          <button key={o.key} type="button" role="radio" aria-checked={on} onClick={() => onSelect(o.key)}
            className={`flex items-center gap-1.5 px-3 py-1.5 transition-colors ${i > 0 ? 'border-l border-stroke' : ''} ${
              on ? 'bg-brand text-white font-medium' : 'bg-surface text-fg2 hover:bg-base'}`}>
            {o.filled && <span className={`w-1.5 h-1.5 rounded-full shrink-0 ${on ? 'bg-white' : 'bg-brand'}`} />}
            <span className="truncate">{o.label}</span>
          </button>
        );
      })}
    </div>
  );
}

function UnionFieldGroup({ field, allDocTypes, value, onChange, showValidation, setId,
  otherInstances = [], scope, scopeId, docRefMode = 'catalog', nested = false }: {
  field: SchemaField; allDocTypes: DocumentType[]; value: unknown;
  onChange: (val: Record<string, unknown>) => void; showValidation: boolean;
  setId?: string; otherInstances?: DocumentInstance[];
  scope?: CatalogScope; scopeId?: string | null; docRefMode?: 'catalog' | 'instance'; nested?: boolean;
}) {
  const { data: primitiveTypes = [] } = useListPrimitiveTypes();
  const compositeType = allDocTypes.find(dt => dt.id === field.typeId) ?? null;
  const subFields = compositeType ? resolveEffectiveFields(compositeType, allDocTypes) : [];
  const subValues = (value != null && typeof value === 'object' && !isFieldRef(value) ? value : {}) as Record<string, unknown>;

  const presentKey = subFields.find(sf => isVariantFilled(subValues[sf.key]))?.key;
  const [activeKey, setActiveKey] = useState<string>(() => presentKey ?? subFields[0]?.key ?? '');
  // Стэш неактивных вариантов — недеструктивное переключение в течение сессии (дискриминатор C, issue #320):
  // persist хранит ОДИН ключ, данные другого варианта живут в локальном стэше до закрытия редактора.
  const [stash, setStash] = useState<Record<string, unknown>>({});
  const [modalOpen, setModalOpen] = useState(false);

  // Значение пришло с заполненным ключом (загрузка/base-merge) — подхватываем активный вариант.
  useEffect(() => { if (presentKey && presentKey !== activeKey) setActiveKey(presentKey); }, [presentKey]); // eslint-disable-line react-hooks/exhaustive-deps

  const activeSf = subFields.find(sf => sf.key === activeKey) ?? subFields[0] ?? null;

  function switchTo(key: string) {
    if (key === activeKey) return;
    setStash(prev => ({ ...prev, [activeKey]: subValues[activeKey] })); // припрятать текущий вариант
    const restored = stash[key];
    onChange(isVariantFilled(restored) ? { [key]: restored } : {}); // восстановить целевой (или пусто)
    setActiveKey(key);
  }
  // persist = один ключ активного варианта; пустой активный → {} (= «не выбрано», как обычный complex).
  // Ссылочные поля в union резолвятся как обычно (issue #324) — никакой спец-обработки ссылок.
  function setActiveValue(v: unknown) { onChange(isVariantFilled(v) ? { [activeKey]: v } : {}); }

  if (subFields.length === 0) return <p className="text-xs text-fg4">Union-тип без полей.</p>;

  const options = subFields.map(sf => ({
    key: sf.key, label: sf.title,
    filled: isVariantFilled(subValues[sf.key]) || isVariantFilled(stash[sf.key]),
  }));
  const chip = (
    <span className="text-[11px] text-fg4 flex items-center gap-1 shrink-0" title="Заполняется ровно один из вариантов">
      <Info size={11} /> заполните одно из
    </span>
  );
  const activeEditor = activeSf && (
    <SubfieldEditor sf={activeSf} value={subValues[activeSf.key]} onChange={setActiveValue}
      allDocTypes={allDocTypes} showValidation={showValidation} setId={setId}
      otherInstances={otherInstances} scope={scope} scopeId={scopeId} docRefMode={docRefMode} primitiveTypes={primitiveTypes} />
  );
  const bar = (
    <div className="flex items-center justify-between gap-2">
      <VariantSegmentedSwitch options={options} active={activeKey} onSelect={switchTo} />
      {chip}
    </div>
  );

  // Вложенный union (глубина ≥1) — строка-сводка активного варианта + правка в модалке.
  if (nested) {
    return (
      <>
        <div className="flex items-center gap-2 border border-stroke rounded-lg px-3 py-2 bg-base">
          <button type="button" onClick={() => setModalOpen(true)}
            className="flex-1 min-w-0 text-left text-sm text-fg2 hover:text-fg1 truncate">
            {unionSummary(activeSf, subValues[activeKey])}
          </button>
          <button type="button" onClick={() => setModalOpen(true)} title="Редактировать"
            className="p-1 text-fg4 hover:text-fg2 transition-colors shrink-0"><Pencil size={13} /></button>
        </div>
        <Modal open={modalOpen} onOpenChange={setModalOpen} wide title={field.title}
          footer={<div className="flex justify-end"><Button variant="filled" onClick={() => setModalOpen(false)}>Готово</Button></div>}>
          <div className="px-6 py-4 space-y-3">{bar}{activeEditor}</div>
        </Modal>
      </>
    );
  }

  return (
    <div className="border border-stroke rounded-lg p-3 space-y-3">
      {bar}
      {activeEditor}
    </div>
  );
}

/** Короткая сводка активного варианта union — для свёрнутой строки во вложенном режиме. */
function unionSummary(sf: SchemaField | null, val: unknown): string {
  if (!sf) return '(пусто)';
  if (!isVariantFilled(val)) return `${sf.title}: —`;
  if (isFieldRef(val)) return `${sf.title} → ${val.displayName}`;
  if (Array.isArray(val)) return `${sf.title} · ${val.length} стр.`;
  return `${sf.title}: ${String(val).slice(0, 40)}`;
}
