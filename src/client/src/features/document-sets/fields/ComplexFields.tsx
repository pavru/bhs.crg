import { useState, useEffect, useRef, type ReactNode } from 'react';
import {
  AlertTriangle, Clipboard, ChevronDown, ChevronUp, Database, FileSpreadsheet, GripVertical, Info, Link2, Pencil, Plus, RefreshCw, Share2, Trash2, Unlink, X,
} from 'lucide-react';
import { DateInput } from '@/shared/ui/DateInput';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { RowActionsMenu } from '@/shared/ui/RowActionsMenu';
import type {
  CatalogScope, DocumentInstance, DocumentType, EnumTypeDef, FieldRef, PrimitiveTypeDef,
} from '@/shared/api/types';
import { isFieldRef, SCOPE_LABELS } from '@/shared/api/types';
import { useListPrimitiveTypes } from '@/shared/api/primitiveTypes';
import {
  resolveEffectiveFields, getDefaultValues, isUnionType, type SchemaField,
} from '@/shared/api/schema';
import { useListEnumTypes } from '@/shared/api/enumTypes';
import { formatFieldValue, type FieldTypeDefs } from '@/shared/utils/fieldDisplay';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { objectSummary } from './objectSummary';
import {
  mergeTableSources, mergeTableOrigins, moveOrder, dropOrder, applyOrder, remapSelection,
  identityOrigins, appendOrigins, type PathOrigins,
} from './arrayRows';
import { VariantPicker } from './VariantPicker';
import { ExtractToCommonDataModal } from './ExtractToCommonDataModal';
import {
  CELL_INPUT, ROW_DRAG_MIME, SCOPE_COLORS, TABLE_SHOWN_TYPES, defaultColWidth, showsArrayTable,
} from './constants';
import { isMissing, PrimitiveInput } from './PrimitiveInput';
import { ImageField } from './ImageField';
import { FileField } from './FileField';
import { RefPickerModal } from './RefPickerModal';
import { DocRefCatalogPickerField } from './DocRefCatalogPickerField';
import { DocRefField, DocArrayField } from './DocRefField';
import { PasteMappingModal } from './PasteMappingModal';
import { BROKEN_PLATE, BROKEN_LABEL, BrokenRefNote } from './BrokenRef';

/** Модульная пустышка для дефолта пропа: инлайновый `= []` — новый массив на каждый рендер. */
const EMPTY_ENUM_TYPES: EnumTypeDef[] = [];

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

export function TableCell({ field, value, onChange, compositeType, setId, allDocTypes, scope, scopeId,
  primitiveTypeDef, enumTypeDef }: {
  field: SchemaField; value: unknown; onChange: (v: unknown) => void;
  compositeType: DocumentType | null;
  setId?: string; allDocTypes: DocumentType[];
  scope?: CatalogScope; scopeId?: string | null;
  primitiveTypeDef?: PrimitiveTypeDef;
  /** Перечисление из реестра (issue #59): без него ячейка читает только легаси-`options` и пустеет. */
  enumTypeDef?: EnumTypeDef;
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
    // Варианты знает реестр (issue #59); в схеме их нет вовсе — там только typeId. Легаси-поля,
    // наоборот, хранят коды прямо в options, и код там же и есть отображаемое имя.
    const opts = enumTypeDef
      ? enumTypeDef.values.map(v => ({ code: v.code, label: v.label }))
      : (field.options ?? []).filter(o => o !== '').map(o => ({ code: o, label: o }));
    return (
      <select value={strVal} onChange={e => onChange(e.target.value)}
        className={CELL_INPUT + ' cursor-pointer'}>
        <option value="">—</option>
        {opts.map(o => <option key={o.code} value={o.code}>{o.label}</option>)}
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
  /** @param origins место каждой строки среди исходных (null — добавлена в таблице), см. mergeTableRows. */
  onSave: (rows: Record<string, unknown>[], origins: (number | null)[]) => void;
  setId?: string; scope?: CatalogScope; scopeId?: string | null;
}) {
  const [rows, setRows] = useState<Record<string, unknown>[]>([]);
  // Стабильные id строк (issue #171): переживают reorder/удаление, служат ключом выбора.
  const [rowIds, setRowIds] = useState<string[]>([]);
  // Личности строк, какими они были при ОТКРЫТИИ таблицы. Только по ним видно, КАКОЙ слот исчез
  // при удалении: сами строки после правки неотличимы, а порядок мог измениться (issue #755).
  const [openedIds, setOpenedIds] = useState<string[]>([]);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [dragIdx, setDragIdx] = useState<number | null>(null);
  const [dropIdx, setDropIdx] = useState<number | null>(null);
  const [colWidths, setColWidths] = useState<Record<string, number>>({});
  const [pasteOpen, setPasteOpen] = useState(false);
  const [pasteText, setPasteText] = useState('');

  useEffect(() => {
    if (open) {
      setRows(items.map(r => ({ ...r })));
      const ids = items.map(() => crypto.randomUUID());
      setRowIds(ids);
      setOpenedIds(ids);
      setSelected(new Set());
    }
  }, [open]); // eslint-disable-line react-hooks/exhaustive-deps

  // Единый scope-контекст владельца (issue #82): комплект → (Set, setId), иначе (scope, scopeId).
  const resolveScope = setId ? 'Set' as const : scope;
  const resolveScopeId = setId ?? scopeId;
  const { data: primitiveTypes = [] } = useListPrimitiveTypes();
  const { data: enumTypes = [] } = useListEnumTypes();
  const primDef = (f: SchemaField) => f.type === 'primitive' ? primitiveTypes.find(pt => pt.id === f.typeId) : undefined;
  const enumDef = (f: SchemaField) => f.type === 'enum' ? enumTypes.find(et => et.id === f.typeId) : undefined;

  // Расчётные подполя (issue #368) не редактируются вручную — считаются при генерации; в редакторе скрыты.
  const subFields = compositeType ? resolveEffectiveFields(compositeType, allDocTypes).filter(f => !f.computed) : [];
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
  function handleSave() {
    // Происхождение строки: её место среди тех, что были при открытии, либо null — добавлена здесь.
    onSave(rows, rowIds.map(id => { const k = openedIds.indexOf(id); return k >= 0 ? k : null; }));
    onOpenChange(false);
  }

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
                      // Груз — страховка по спецификации, свой тип вместо text/plain (см. ROW_DRAG_MIME).
                      onDragStart={e => {
                        setDragIdx(i);
                        e.dataTransfer.effectAllowed = 'move';
                        e.dataTransfer.setData(ROW_DRAG_MIME, String(i));
                      }}
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
                        scope={scope} scopeId={scopeId} primitiveTypeDef={primDef(f)} enumTypeDef={enumDef(f)} />
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
  setId, otherInstances = [], scope, scopeId, docRefMode = 'catalog', brokenPaths, basePath, savedAt,
}: {
  field: SchemaField; allDocTypes: DocumentType[]; value: unknown;
  onChange: (val: unknown[]) => void; showValidation: boolean;
  setId?: string; otherInstances?: DocumentInstance[];
  scope?: CatalogScope; scopeId?: string | null;
  docRefMode?: 'catalog' | 'instance';
  /** Пути битых ссылок (issue #332) + базовый путь массива — для пометки элементов-ссылок на удалённое. */
  brokenPaths?: Set<string>; basePath?: string;
  /** Отметка времени сохранённого документа: её смена значит «пути диагностики перенумерованы» (#759). */
  savedAt?: string;
}) {
  const compositeType = allDocTypes.find(dt => dt.id === field.typeId) ?? null;
  const allItems = Array.isArray(value) ? value as unknown[] : [];
  const inlineItems = allItems.filter(item => !isFieldRef(item)) as Record<string, unknown>[];
  // Расчётные подполя (issue #368) не редактируются вручную — считаются при генерации; в редакторе скрыты.
  const subFields = compositeType ? resolveEffectiveFields(compositeType, allDocTypes).filter(f => !f.computed) : [];
  // Строка массива union-типа (issue #320): редактируется переключателем варианта, а не стопкой всех
  // полей — иначе диалог строки показывал оба поля union (баг). Тип union = тэг type.union на схеме.
  // typeHasTag, а не `.tags.includes`: инлайновая проверка не видит ни УНАСЛЕДОВАННЫЙ тэг, ни
  // параметризованную запись `type.union:2` (issue #747).
  const isUnionComposite = !!compositeType && isUnionType(compositeType, allDocTypes);
  const [rowModal, setRowModal] = useState<number | null>(null); // issue #102: строка массива правится в модалке, не инлайн
  const [extractRow, setExtractRow] = useState<number | null>(null); // issue #663
  const [tableOpen, setTableOpen] = useState(false);
  const [collapsed, setCollapsed] = useState(true);
  const [catalogPickerOpen, setCatalogPickerOpen] = useState(false);
  // Перестановка и пакетное удаление прямо в списке (issue #754). Выбор — номерами строк, см.
  // arrayRows: личностей у элементов массива нет, а номера переживают наши операции через remapSelection.
  /**
   * Соответствие «строка → номер, о котором говорил сервер» (issue #759).
   *
   * <p>Сбрасывается по факту СОХРАНЕНИЯ, а не по приходу новой диагностики. Разница существенна в
   * обе стороны:</p>
   *
   * <ul>
   * <li>диагностика может прийти (первая загрузка, фоновой refetch по возврату фокуса), когда
   * человек уже переставил строки, — сбрось мы тогда в тождество, метка села бы на живую строку,
   * ровно тот дефект, который эта задача и чинит;</li>
   * <li>после сохранения сервер перенумеровывает пути под НОВЫЙ порядок, и прежние поправки к ним
   * становятся неверны — тождество тут единственно правильное.</li>
   * </ul>
   *
   * <p>Сравнивать по объекту <code>brokenPaths</code> нельзя ещё и технически: React Query по
   * умолчанию делает structural sharing, и при неизменившейся диагностике возвращает ТУ ЖЕ ссылку —
   * сброс не сработал бы вовсе.</p>
   *
   * <p>Правка состояния прямо в рендере — тот случай, для которого React её и допускает: значение
   * производное от пропа, эффект дал бы лишний кадр со старым соответствием.</p>
   */
  const [marks, setMarks] = useState<{ savedAt?: string; origins: PathOrigins }>(
    () => ({ savedAt, origins: identityOrigins(allItems.length) }));
  if (marks.savedAt !== savedAt) {
    setMarks({ savedAt, origins: identityOrigins(allItems.length) });
  }

  const [selected, setSelected] = useState<Set<number>>(new Set());
  const [dragIdx, setDragIdx] = useState<number | null>(null);
  const [dropIdx, setDropIdx] = useState<number | null>(null);
  const [confirmBulk, setConfirmBulk] = useState(false);

  /**
   * Таблица — для ОДНОРОДНЫХ строк, поэтому union-массиву её не предлагаем (issue #748).
   *
   * <p>Колонки таблицы — это подполя элемента, а у union'а подполя суть ВАРИАНТЫ. Пять колонок
   * читаются как «и», а не «одно из»: заполнив две в одной строке, человек получает два ключа и
   * ломает инвариант «ровно один ключ» (#320) без единого предупреждения — ни в редакторе, ни при
   * сохранении. Исключение union'а до сих пор жило только в модалке строки и в
   * <code>ComplexFieldGroup</code>, до кнопки не доходило.</p>
   *
   * <p><b>Ввода данных не теряем</b>: <code>complex</code>-колонка рендерится
   * <code>ComplexCellPicker</code>'ом, то есть пикером ссылки на запись каталога, — инлайнового
   * ввода полей варианта в таблице нет и не было. На единственном живом носителе («Кабельный
   * журнал» → «Кабельные линии») это пять колонок пустых пикеров: записей каталога нужных типов в
   * базе ноль, а само поле там привязано к набору данных, и редактор массива подменён заглушкой
   * привязки — до кнопки не добраться вовсе.</p>
   *
   * <p><b>Но кое-что теряем, и это надо назвать.</b> В модалке таблицы живут три вещи, которых
   * больше нигде нет: перетаскивание строк (порядок строк значим, #663), удаление выбранных пачкой
   * и вставка из Excel. Для union-массива они уходят вместе с кнопкой. Перетаскивание и пакетное
   * удаление возвращаем в аккордеонный список отдельной задачей (#754) — они полезны ВСЕМ массивам,
   * не только union. Вставку не возвращаем сознательно: её маппинг в inline-режиме кладёт колонку в
   * ключ варианта, то есть две колонки на два варианта дают два ключа — вторая дверь в ту же дыру.</p>
   *
   * <p>Массовый ввод разнородных строк — задача отдельная и это ФИЧА, а не возврат утраченного:
   * таблице понадобится колонка-селектор варианта, иначе порядок строк и инвариант снова разойдутся.</p>
   */
  const hasTableFields = showsArrayTable(subFields, isUnionComposite);
  // Вынос строки в общие данные (issue #663) — только там, где известно, куда класть запись:
  // редактор документа (есть комплект) либо форма общих данных (есть свой уровень).
  const canExtract = !!compositeType && (!!setId || !!scope);
  const { data: primitiveTypes = [] } = useListPrimitiveTypes();
  const { data: enumTypes = [] } = useListEnumTypes();
  const primDef = (f: SchemaField) => f.type === 'primitive' ? primitiveTypes.find(pt => pt.id === f.typeId) : undefined;
  const enumDef = (f: SchemaField) => f.type === 'enum' ? enumTypes.find(et => et.id === f.typeId) : undefined;
  const typeDefs: FieldTypeDefs = { primitiveTypes, enumTypes }; // формат значений в сводке строки (issue #611)

  function addRow() {
    const newRow = getDefaultValues(subFields);
    onChange([...allItems, newRow]);
    setMarks(m => ({ ...m, origins: appendOrigins(m.origins, 1) }));
    setRowModal(allItems.length); // сразу открыть модалку новой строки
  }

  /**
   * Запись каталога становится строкой массива. Для union'а (issue #747) она заворачивается в ключ
   * варианта — ровно один ключ, инвариант #320 держится конструкцией; какой это вариант, решил
   * пикер по типу записи (и спросил человека, если тип не назвал единственного).
   */
  function addFromCatalog(ref: FieldRef, variantKey?: string) {
    onChange([...allItems, variantKey ? { [variantKey]: ref } : ref]);
    setMarks(m => ({ ...m, origins: appendOrigins(m.origins, 1) }));
  }

  /**
   * Единственный заполненный вариант union-строки, если это ссылка: {ключ, ссылка, подпись}.
   * Не union или не ссылка — null.
   */
  function unionRef(row: Record<string, unknown>): { key: string; ref: FieldRef; label: string } | null {
    if (!isUnionComposite) return null;
    const keys = Object.keys(row);
    if (keys.length !== 1) return null;
    const value = row[keys[0]];
    if (!isFieldRef(value)) return null;
    const sub = subFields.find(f => f.key === keys[0]);
    return { key: keys[0], ref: value, label: sub?.title ?? keys[0] };
  }

  /**
   * Единственная дверь ко всякому изменению порядка и состава строк (issue #754).
   *
   * <p>Порядок применяется и к строкам, и к номерам выбранных — одним и тем же значением, поэтому
   * выбор не может указать на чужую строку. Массив ПЕРЕСТАВЛЯЕТСЯ, а не пересобирается из частей:
   * пересборка по частям — это #755, где ссылочные строки уезжали в начало.</p>
   */
  function reorder(order: number[]) {
    onChange(applyOrder(allItems, order));
    setSelected(prev => remapSelection(prev, order));
    // Метки битых ссылок едут тем же порядком (issue #759): иначе красная плитка осталась бы на
    // МЕСТЕ, а не на строке, и указывала бы на живую запись.
    setMarks(m => ({ ...m, origins: applyOrder(m.origins, order) }));
  }

  /**
   * Путь строки в нумерации серверной диагностики; null — сервер про эту строку не говорил.
   * Рисовать метку в этом случае не по чему, и рисовать её наугад хуже, чем не рисовать.
   */
  function markPath(i: number): string | null {
    const origin = marks.origins[i];
    return basePath && origin != null ? `${basePath}[${origin}]` : null;
  }

  function moveItem(from: number, to: number) {
    if (to < 0 || to >= allItems.length || from === to) return;
    reorder(moveOrder(allItems.length, from, to));
  }

  function removeItem(i: number) {
    reorder(dropOrder(allItems.length, new Set([i])));
    setRowModal(null);
  }

  // Номера выбранных, которые ещё существуют: массив мог поредеть помимо выбора (таблица, вынос).
  // Считаем по ним и удаляем их же — подпись обязана совпадать с тем, что произойдёт.
  const chosen = [...selected].filter(i => i < allItems.length).sort((a, b) => a - b);

  function toggleSelect(i: number) {
    // delete возвращает «было ли», так что снятие и постановка — одна ветка.
    setSelected(prev => { const n = new Set(prev); if (!n.delete(i)) n.add(i); return n; });
  }

  function deleteSelected() {
    reorder(dropOrder(allItems.length, new Set(chosen)));
    setRowModal(null);
  }

  /** Свёрнутый список выбор не показывает — держать невидимый выбор значит удалить вслепую. */
  function toggleCollapsed() {
    if (!collapsed) setSelected(new Set());
    setCollapsed(v => !v);
  }

  /**
   * Строка, чью ручку надо вернуть под фокус после перестановки клавиатурой.
   *
   * <p>Ключ строки здесь — её номер (личностей у элементов массива нет), поэтому React после
   * перестановки переиспользует тот же узел: фокус остался бы на МЕСТЕ, а не на уехавшей строке, и
   * второе нажатие ↑ двинуло бы уже соседа. Без этого клавиатурная перестановка работает ровно один
   * раз — а «довести строку до края клавиатурой» и есть тот сценарий, ради которого живёт
   * MoveButtons (#517, #542).</p>
   */
  const listRef = useRef<HTMLDivElement>(null);
  const focusRow = useRef<number | null>(null);
  // Без списка зависимостей: заявка живёт в ref, поэтому эффекту нечего сравнивать — он просто
  // смотрит после каждой отрисовки, не ждёт ли кто фокуса, и почти всегда сразу выходит.
  useEffect(() => {
    const target = focusRow.current;
    if (target === null) return;
    focusRow.current = null;
    listRef.current?.querySelector<HTMLElement>(`[data-grip="${target}"]`)?.focus();
  });

  /** Чекбокс + ручка перетаскивания + номер. Обычная функция, не компонент: у компонента,
   *  объявленного внутри рендера, каждый раз новый тип — React размонтировал бы ручку с фокусом. */
  function rowChrome(i: number, danger = false) {
    return (
      <>
        <input type="checkbox" checked={selected.has(i)} onChange={() => toggleSelect(i)}
          aria-label={`Выбрать строку ${i + 1}`}
          className="shrink-0 w-3.5 h-3.5 accent-brand cursor-pointer" />
        <button type="button" draggable data-grip={i}
          // Груз — страховка по спецификации, свой тип вместо text/plain (см. ROW_DRAG_MIME).
          onDragStart={e => {
            setDragIdx(i);
            e.dataTransfer.effectAllowed = 'move';
            e.dataTransfer.setData(ROW_DRAG_MIME, String(i));
          }}
          onDragEnd={() => { setDragIdx(null); setDropIdx(null); }}
          onKeyDown={e => {
            const to = e.key === 'ArrowUp' ? i - 1 : e.key === 'ArrowDown' ? i + 1 : null;
            if (to === null) return;
            // Стрелку гасим и на краю: иначе последнее нажатие в «довести строку до низа»
            // достаётся браузеру, и форма уезжает прокруткой ровно в тот момент, когда строка
            // встала на место. Тот же сценарий, ради которого MoveButtons не делает disabled (#517).
            e.preventDefault(); e.stopPropagation();
            if (to < 0 || to >= allItems.length) return;
            moveItem(i, to);
            focusRow.current = to;
          }}
          title="Перетащить для изменения порядка (или стрелки ↑↓)"
          aria-label={`Переместить строку ${i + 1}: стрелки вверх и вниз`}
          className="shrink-0 cursor-grab active:cursor-grabbing text-fg4 hover:text-fg2 focus-visible:outline-none focus-visible:text-brand">
          <GripVertical size={12} />
        </button>
        <span className={`text-xs font-mono w-5 text-right shrink-0 ${danger ? 'text-danger' : 'text-fg4'}`}>
          {i + 1}
        </span>
      </>
    );
  }

  /**
   * Признак выбора для строки, у которой фон уже занят (битая ссылка — danger-плитка).
   *
   * <p>Иначе выбранная битая строка отличалась бы от невыбранной одним чекбоксом в 14 пикселей,
   * при том что шапка говорит «Выбрано: 3», а удаление пачкой её и правда унесёт. Ровно то
   * «удалить вслепую», ради которого выбор снимается при сворачивании списка.</p>
   */
  function selectedRing(i: number) {
    return selected.has(i) ? 'ring-1 ring-inset ring-brand' : '';
  }

  /** Обвязка строки: приём перетаскиваемой строки на своё место + подсветка цели. */
  function dropTarget(i: number) {
    return {
      onDragOver: (e: React.DragEvent) => {
        if (dragIdx === null) return;
        e.preventDefault();
        if (dropIdx !== i) setDropIdx(i);
      },
      onDrop: (e: React.DragEvent) => {
        e.preventDefault();
        if (dragIdx !== null) moveItem(dragIdx, i);
        setDragIdx(null); setDropIdx(null);
      },
      style: dragIdx !== null && dropIdx === i && dragIdx !== i
        ? { outline: '2px solid var(--color-brand)', outlineOffset: '-2px' } : undefined,
    };
  }

  function updateRow(i: number, row: Record<string, unknown>) {
    onChange(allItems.map((it, idx) => idx === i ? row : it));
  }

  function rowSummary(row: Record<string, unknown>) {
    return objectSummary(row, subFields, typeDefs);
  }

  return (
    <div className="border border-stroke rounded-lg overflow-hidden">
      <div className={`flex items-center justify-between px-3 py-2 bg-base ${collapsed ? '' : 'border-b border-stroke'}`}>
        <button type="button" onClick={toggleCollapsed}
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
        {/* Есть выбор — шапка показывает действия над выбранным вместо действий над списком (тот же
            приём, что в подвале модалки таблицы). Место выбрано за то, что оно ВИДНО: подвал списка
            у контейнера с overflow-hidden не «липнет», и на девятнадцати строках кнопка удаления
            оказалась бы за экраном — отметил строки вверху, а нажать нечего. */}
        {chosen.length > 0 ? (
          <div className="flex items-center gap-1">
            <span className="text-xs text-fg2 px-1">Выбрано: {chosen.length}</span>
            <button type="button" onClick={() => setConfirmBulk(true)}
              className="flex items-center gap-1 text-xs text-danger hover:text-danger px-2 py-0.5 rounded hover:bg-danger-subtle transition-colors">
              <Trash2 size={11} /> Удалить выбранные
            </button>
            <button type="button" onClick={() => setSelected(new Set())}
              className="text-xs text-fg4 hover:text-fg2 px-2 py-0.5 rounded hover:bg-stroke transition-colors">
              Снять выбор
            </button>
          </div>
        ) : (
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
        )}
      </div>
      {!collapsed && allItems.length === 0 && (
        <p className="text-xs text-fg4 text-center py-3">Нет строк — нажмите «Добавить строку» или «Из каталога»</p>
      )}
      {!collapsed && allItems.length > 0 && (
        <div ref={listRef} className="divide-y divide-muted">
          {allItems.map((item, i) => {
            if (isFieldRef(item)) {
              const path = markPath(i);
              const itemBroken = !!path && !!brokenPaths?.has(path);
              if (itemBroken) {
                return (
                  <div key={i}>
                    <div {...dropTarget(i)}
                      className={`flex items-center gap-2 px-3 py-2 ${BROKEN_PLATE} ${selectedRing(i)}`}>
                      {rowChrome(i, true)}
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
                <div key={i} {...dropTarget(i)}
                  className={`flex items-center gap-2 px-3 py-2 ${selected.has(i) ? 'bg-brand-subtle' : 'hover:bg-base'}`}>
                  {rowChrome(i)}
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

            // Строка union'а, чей единственный вариант — ссылка (issue #747). Без этой ветки она
            // ушла бы в rowSummary как обычный объект: сводка вернула бы голое displayName, и
            // строка потеряла бы и признак ссылки, и вариант — на вид обычные данные, которых на
            // самом деле нет. Рисуем тем же языком, что соседние ref-строки, плюс метка варианта.
            const wrapped = unionRef(row);
            if (wrapped) {
              // Путь битой ссылки сервер строит ВНУТРЬ объекта («Поле[0].Вариант», ResolutionDiagnostics),
              // а не по индексу строки: проверь мы только «Поле[0]», удалённая цель показалась бы
              // здоровой ссылкой — ровно та потеря признака, ради которой заведена эта ветка.
              const wrappedPath = markPath(i);
              const wrappedBroken = !!wrappedPath
                && !!brokenPaths?.has(`${wrappedPath}.${wrapped.key}`);
              return (
                <div key={i}>
                <div {...dropTarget(i)}
                  className={`flex items-center gap-2 px-3 py-2 ${wrappedBroken
                    ? `${BROKEN_PLATE} ${selectedRing(i)}`
                    : selected.has(i) ? 'bg-brand-subtle' : 'hover:bg-base'}`}>
                  {rowChrome(i, wrappedBroken)}
                  <Link2 size={12} className={`shrink-0 ${wrappedBroken ? 'text-danger' : 'text-warning'}`} />
                  <span className={`flex-1 text-sm truncate ${wrappedBroken ? BROKEN_LABEL : 'text-warning'}`}>{wrapped.ref.displayName}</span>
                  <span className="text-[11px] text-fg4 shrink-0 truncate max-w-[40%]">{wrapped.label}</span>
                  {/* ✎ оставлен: это единственный вход сменить вариант и снять ссылку. */}
                  <button type="button" onClick={() => setRowModal(i)} title="Редактировать"
                    className="p-1 text-fg4 hover:text-fg2 shrink-0">
                    <Pencil size={13} />
                  </button>
                  <button type="button" onClick={() => removeItem(i)}
                    className="p-1 text-fg4 hover:text-danger shrink-0">
                    <Trash2 size={13} />
                  </button>
                </div>
                {wrappedBroken && <BrokenRefNote compact />}
                </div>
              );
            }

            // Строка union'а, где заполнен не один вариант (issue #756). До этой ветки она доходит
            // именно потому, что ключей больше одного: unionRef выше требует ровно одного и
            // возвращает null, так что строка рисовалась обычной сводкой — без единого признака,
            // что открыть её значит потерять часть данных.
            const extraVariants = isUnionComposite ? filledVariants(row, subFields) : [];

            // issue #102: строка — компактная сводка + ✎ (модалка), без инлайн-раскрытия (источник «портянки»).
            return (
              <div key={i} {...dropTarget(i)}
                className={`flex items-center gap-2 px-3 py-2 ${selected.has(i) ? 'bg-brand-subtle' : 'hover:bg-base'}`}>
                {rowChrome(i)}
                <button type="button" onClick={() => setRowModal(i)}
                  className="flex-1 text-left text-sm text-fg2 hover:text-fg1 truncate">
                  {rowSummary(row)}
                </button>
                {extraVariants.length > 1 && (
                  <span className="flex items-center gap-1 text-[11px] text-warning shrink-0"
                    title={overfilledNote(extraVariants)} role="img" aria-label={overfilledNote(extraVariants)}>
                    <AlertTriangle size={12} /> вариантов: {extraVariants.length}
                  </span>
                )}
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
      <ConfirmDialog
        open={confirmBulk} onOpenChange={setConfirmBulk}
        title={`Удалить строк: ${chosen.length}?`}
        description={<p>Строки исчезнут из поля «{field.title}». Изменение попадёт в документ при
          сохранении формы — до этого его можно отменить, закрыв форму без сохранения.</p>}
        confirmLabel={`Удалить (${chosen.length})`}
        onConfirm={deleteSelected} />
      {rowModal !== null && allItems[rowModal] != null && !isFieldRef(allItems[rowModal]) && (
        <Modal open onOpenChange={o => { if (!o) setRowModal(null); }} wide
          title={`${compositeType?.name ?? field.title} — строка ${rowModal + 1}`}
          footer={
            <div className="flex items-center justify-between gap-2">
              {/* Вынос строки в переиспользуемую запись (issue #663) — по одной: массовый вынос
                  отдельной задачей, у него свой selection-state. */}
              {canExtract ? (
                <Button variant="text" size="sm" icon={<Share2 size={13} />}
                  // Строка-ссылка выносу не подлежит: получилась бы запись, всё содержимое которой —
                  // ссылка на другую запись, то есть лишнее звено в цепочке (у неё есть предел, #723).
                  // ComplexFieldGroup такую ветку до выноса не доводит вовсе; у массива защиты не было.
                  disabled={isRowEmpty(allItems[rowModal] as Record<string, unknown>, subFields)
                    || !!unionRef(allItems[rowModal] as Record<string, unknown>)}
                  title="Создать запись общих данных из этой строки"
                  onClick={() => { const i = rowModal; setRowModal(null); setExtractRow(i); }}>
                  Вынести в общие данные…
                </Button>
              ) : <span />}
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
                      primitiveTypeDef={primDef(sf)} enumTypeDef={enumDef(sf)} />
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
        // Выбор снимаем: таблица меняет и состав, и порядок строк, и номера выбранных после неё
        // указывали бы неизвестно на что (issue #754).
        onSave={(rows, origins) => {
          // Источники строк считаем ОДИН раз и берём из них и сами строки, и их серверные номера
          // (issue #759). Гасить метки целиком было бы неверно: слоты известны точно, а вернуть их
          // потом нечему — при неизменившейся диагностике сервер отдаёт тот же объект.
          const sources = mergeTableSources(allItems, rows, origins);
          onChange(sources.map(s => (s.edited != null ? rows[s.edited] : allItems[s.at!])));
          setSelected(new Set());
          setMarks(m => ({ ...m, origins: mergeTableOrigins(sources, m.origins) }));
        }}
        setId={setId} scope={scope} scopeId={scopeId}
      />
      {compositeType && (
        <RefPickerModal
          open={catalogPickerOpen} onOpenChange={setCatalogPickerOpen}
          compositeType={compositeType}
          setId={setId} scope={scope} scopeId={scopeId}
          // Документы комплекта — источник строк наравне с каталогом (issue #751): целиком, в
          // doc-ref-вариант union'а, и полями — в вариант подходящего типа. Раздел полей завёлся в
          // #750 (до того сюда приходил жёсткий `[]`, и он был пуст ВСЕГДА — возможность выглядела
          // существующей, а её не было); на живых схемах предлагать есть что — пятнадцать пар вида
          // «АОСР → Представитель заказчика» для массивов типа «Подписант» и «Организация».
          //
          // Инвариант union'а держится ключом варианта: куда ляжет кандидат, решает placeInUnion, и
          // ничья спрашивается вторым шагом, а не разрешается молча.
          //
          // «Ссылка на ссылку» из #750 ВОЗМОЖНА, только не там, где её искала задача. Поле
          // type='complex' сплошь и рядом хранит не объект, а ссылку на запись каталога (её кладёт
          // ComplexFieldGroup), и тогда $ref:'document' указывает на $ref:'catalog'. Ветка
          // case "document" резолвера копирует значение БЕЗ рекурсии, так что за один проход
          // вложенная ссылка остаётся сырой. Генерация, предпросмотр и проверка гоняют резолв
          // дважды и её добирают; снимок для внешнего чтения — один раз, и отдаёт стаб вместо
          // данных. Дефект не наш (то же доступно у одиночного complex-поля с master), но этот
          // раздел делает его частым — заведено отдельно.
          otherInstances={otherInstances}
          allDocTypes={allDocTypes}
          unionAware
          onSelect={addFromCatalog}
        />
      )}
      {/* Строка заменяется ссылкой НА СВОЁМ МЕСТЕ: порядок строк значим, и вынесенный материал не
          должен уезжать в конец таблицы (issue #663). */}
      {compositeType && extractRow !== null
        && allItems[extractRow] != null && !isFieldRef(allItems[extractRow]) && (
        <ExtractToCommonDataModal
          open onOpenChange={o => { if (!o) setExtractRow(null); }}
          values={allItems[extractRow] as Record<string, unknown>}
          compositeType={compositeType} allDocTypes={allDocTypes}
          setId={setId} scope={scope} scopeId={scopeId}
          onExtracted={ref => {
            onChange(allItems.map((item, i) => (i === extractRow ? ref : item)));
            // Строка стала ссылкой на ТОЛЬКО ЧТО созданную запись — битой она быть не может, а
            // серверная метка по её прежнему номеру относилась к прежнему содержимому (issue #759).
            setMarks(m => ({
              ...m, origins: m.origins.map((o, i) => (i === extractRow ? null : o)),
            }));
            setExtractRow(null);
          }} />
      )}
    </div>
  );
}

/** Строка без единого заполненного подполя — выносить нечего. */
function isRowEmpty(row: Record<string, unknown> | undefined, subFields: SchemaField[]): boolean {
  if (!row) return true;
  return subFields.every(f => { const v = row[f.key]; return v == null || v === ''; });
}

// ─── Complex field group ──────────────────────────────────────────────────────

/** Сворачиваемая секция «Заполняются автоматически» (issue #102, P2): read-only поля из источника
 *  прячем по умолчанию, чтобы длинная форма не выглядела «портянкой» одинаковых боксов. */
export function AutoFieldsSection(
  { count, recognizedCount = 0, children }: { count: number; recognizedCount?: number; children: ReactNode },
) {
  const [open, setOpen] = useState(false);
  return (
    <div className="border border-dashed border-stroke rounded-lg overflow-hidden">
      <button type="button" onClick={() => setOpen(v => !v)} aria-expanded={open}
        className="w-full flex items-center gap-2 px-3 py-2 bg-base/40 hover:bg-base transition-colors text-left">
        {open ? <ChevronUp size={12} className="text-fg4 shrink-0" /> : <ChevronDown size={12} className="text-fg4 shrink-0" />}
        {/* Глиф называет НАЗНАЧЕНИЕ секции, а не её содержимое, и продублирован подписью рядом —
            поэтому он приглушён: brand в этой строке должен быть ровно один, и указывать он должен
            на факт, ради которого сюда смотрят. */}
        <Database size={11} className="text-fg4 shrink-0" />
        <span className="text-xs text-fg3 flex-1">Заполняются автоматически</span>
        {/* Факт в подписи самой раскрывашки — здесь он НЕСУЩИЙ, а не дублирующий: секция свёрнута
            по умолчанию, и значок у поля внутри неё человек по умолчанию не видит вовсе. Отдельной
            интерактивности факту не даём — заголовок и так кнопка с очевидным действием.
            Общее число оставлено намеренно: «со сканов: 4» — подмножество, и без основания оно не
            читается. 4 из 4 и 4 из 40 — разные решения, а решает именно отношение. */}
        <span className="text-xs text-fg4">
          {count} п.
          {recognizedCount > 0 && (
            <span className="text-brand"
              title="Часть значений распознана с отсканированных документов. Ошибки чтения возможны — сверьте с оригиналом; править — в источнике данных.">
              {' · '}
              {/* Совпали — говорим словом: сравнивать два числа на глаз читателю не должно
                  приходиться (в прошлый раз этот дефект пришлось записать в «не чиним»). */}
              {recognizedCount === count ? 'все со сканов' : `со сканов: ${recognizedCount}`}
            </span>
          )}
        </span>
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
  const [extractOpen, setExtractOpen] = useState(false); // issue #663
  const { data: primitiveTypes = [] } = useListPrimitiveTypes();
  const { data: enumTypes = [] } = useListEnumTypes();
  const typeDefs: FieldTypeDefs = { primitiveTypes, enumTypes }; // формат значений в сводке (issue #611)
  const compositeType = allDocTypes.find(dt => dt.id === field.typeId) ?? null;

  // Union-тип (issue #320): составной тип с тэгом type.union — «заполняется ровно одно из полей».
  // Рендерим переключатель варианта + редактор активного подполя вместо стопки всех подполей.
  const isUnion = !!compositeType && isUnionType(compositeType, allDocTypes);
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
  // Расчётные подполя (issue #368) не редактируются вручную — считаются при генерации; в редакторе скрыты.
  const subFields = compositeType ? resolveEffectiveFields(compositeType, allDocTypes).filter(f => !f.computed) : [];
  const isEmpty = subFields.every(f => { const v = subValues[f.key]; return v == null || v === ''; });

  function setSubValue(key: string, val: unknown) {
    onChange({ ...subValues, [key]: val });
  }

  // Вынос в общие данные (issue #663). Показываем только там, где известно, КУДА класть запись:
  // в редакторе документа (есть комплект) и в форме общих данных (есть свой уровень). Значение-ссылка
  // сюда не доходит — ref-ветки вернулись выше; пустой объект выносить нечего.
  const canExtract = !!compositeType && (!!setId || !!scope);
  // Монтируем только открытым: начальные значения формы считаются при монтировании, и держать
  // окно смонтированным значило бы подсказывать имя от прошлого выноса.
  const extractModal = compositeType && extractOpen && (
    <ExtractToCommonDataModal
      open onOpenChange={o => { if (!o) setExtractOpen(false); }}
      values={subValues} compositeType={compositeType} allDocTypes={allDocTypes}
      setId={setId} scope={scope} scopeId={scopeId}
      onExtracted={ref => onChange(ref)} />
  );

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
              docRefMode={docRefMode} primitiveTypes={primitiveTypes} enumTypes={enumTypes} />
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
            {isEmpty ? <span className="text-fg4">Заполнить…</span> : objectSummary(subValues, subFields, typeDefs)}
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
            <div className="flex items-center justify-between gap-2">
              {/* Команда идёт в футер уже существующей модалки, а не четвёртой иконкой в ряд поля
                  (issue #663): ряд и так несёт три действия. */}
              {canExtract ? (
                <Button variant="text" size="sm" icon={<Share2 size={13} />} disabled={isEmpty}
                  title={isEmpty ? 'Нечего выносить — объект пуст' : undefined}
                  onClick={() => { setModalOpen(false); setExtractOpen(true); }}>
                  Вынести в общие данные…
                </Button>
              ) : <span />}
              <Button variant="filled" onClick={() => setModalOpen(false)}>Готово</Button>
            </div>
          }>
          <div className="px-6 py-4">{subfieldsBody}</div>
        </Modal>
        {picker}
        {extractModal}
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
              : objectSummary(subValues, subFields, typeDefs)}
          </span>
        </button>
        <div className="flex items-center gap-1 shrink-0">
          <button type="button" onClick={() => setPickerOpen(true)}
            className="flex items-center gap-1.5 text-xs text-brand hover:text-brand-hover px-2 py-0.5 rounded hover:bg-brand-subtle transition-colors">
            <Link2 size={11} /> Выбрать из каталога
          </button>
          {/* Обратное движение (issue #663) — в kebab, а не четвёртой кнопкой: конвенция «>3 действий
              в ряду → меню». У пустого объекта прячем меню целиком: пункт в нём ровно один, и
              «три точки» с единственным выключенным пунктом — приглашение, за которым ничего нет.
              В футерах модалок тот же случай выражен как disabled — там кнопка не одна. */}
          {canExtract && !isEmpty && (
            <RowActionsMenu ariaLabel="Действия составного поля" actions={[{
              key: 'extract', label: 'Вынести в общие данные…', icon: <Share2 size={14} />,
              onSelect: () => setExtractOpen(true),
            }]} />
          )}
        </div>
      </div>
      {!collapsed && <div className="px-3 py-3">{subfieldsBody}</div>}
      {picker}
      {extractModal}
    </div>
  );
}

// ─── Один подполе-редактор (диспетчеризация по типу) ───────────────────────────
// Извлечено из ComplexFieldGroup, чтобы переиспользовать для активного варианта union (issue #320).
function SubfieldEditor({ sf, value, onChange, allDocTypes, showValidation, setId,
  otherInstances = [], scope, scopeId, docRefMode = 'catalog', primitiveTypes, enumTypes = EMPTY_ENUM_TYPES }: {
  sf: SchemaField; value: unknown; onChange: (v: unknown) => void;
  allDocTypes: DocumentType[]; showValidation: boolean; setId?: string;
  otherInstances?: DocumentInstance[]; scope?: CatalogScope; scopeId?: string | null;
  docRefMode?: 'catalog' | 'instance'; primitiveTypes: PrimitiveTypeDef[]; enumTypes?: EnumTypeDef[];
}) {
  const primDef = sf.type === 'primitive' ? primitiveTypes.find(pt => pt.id === sf.typeId) : undefined;
  const enumTypeDef = sf.type === 'enum' ? enumTypes.find(et => et.id === sf.typeId) : undefined;
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
    invalid={invalid} primitiveTypeDef={primDef} enumTypeDef={enumTypeDef} />;
}

// ─── Union-поле (issue #320): заполняется РОВНО ОДИН вариант (подполе union-типа) ──

/**
 * Подписи заполненных вариантов union-значения (issue #756).
 *
 * <p>Инвариант — «заполнен ровно один» (#320), и записать иное приложение не даёт. Но
 * <code>PUT …/requisites</code> кладёт тело как есть (путь записи схема-агностичен сознательно), так
 * что значение с двумя ключами приезжает из восстановленной копии, правки JSONB руками или импорта.
 * Единственный путь через такие данные в редакторе — потеря части: он показывает ПЕРВЫЙ заполненный
 * вариант, а первая же правка выбрасывает остальные. Поэтому — сказать заранее.</p>
 *
 * <p><code>subFields</code> приходит уже без расчётных подполей (#368) — их значение считает
 * генерация, вариантами они не являются, и серверная проверка арности их так же исключает.</p>
 */
function filledVariants(row: Record<string, unknown>, subFields: SchemaField[]): string[] {
  return subFields.filter(sf => isVariantFilled(row[sf.key])).map(sf => sf.title);
}

/**
 * Текст предупреждения о нескольких заполненных вариантах — одинаковый в списке и в редакторе.
 *
 * <p>Про «попадёт в документ» не говорим: в data.json уходят оба ключа, а что напечатается, решает
 * блок типа в шаблоне — утверждать за него нечего. Обещаем только то, что гарантирует код.</p>
 */
function overfilledNote(titles: string[]): string {
  return `Заполнено вариантов: ${titles.length} (${titles.join(', ')}), а должен быть один. `
    + 'В данные уйдут все; что попадёт в документ, решит блок типа в шаблоне. '
    + 'Редактор откроет первый и при правке потеряет остальные.';
}

/** Вариант считается заполненным: непустой массив / FieldRef / непустой объект / непустая строка. */
function isVariantFilled(v: unknown): boolean {
  if (v == null) return false;
  if (isFieldRef(v)) return true;
  if (Array.isArray(v)) return v.length > 0;
  if (typeof v === 'object') return Object.keys(v as object).length > 0;
  return String(v).trim() !== '';
}

// VariantPicker живёт своим файлом (issue #747): его зовёт ещё и пикер ссылок, а импорт
// оттуда в ComplexFields замкнул бы цикл. Реэкспорт — чтобы не трогать существующих потребителей.
export { VariantPicker };

function UnionFieldGroup({ field, allDocTypes, value, onChange, showValidation, setId,
  otherInstances = [], scope, scopeId, docRefMode = 'catalog', nested = false }: {
  field: SchemaField; allDocTypes: DocumentType[]; value: unknown;
  onChange: (val: Record<string, unknown>) => void; showValidation: boolean;
  setId?: string; otherInstances?: DocumentInstance[];
  scope?: CatalogScope; scopeId?: string | null; docRefMode?: 'catalog' | 'instance'; nested?: boolean;
}) {
  const { data: primitiveTypes = [] } = useListPrimitiveTypes();
  const { data: enumTypes = [] } = useListEnumTypes();
  const compositeType = allDocTypes.find(dt => dt.id === field.typeId) ?? null;
  // Расчётные подполя (issue #368) не редактируются вручную — считаются при генерации; в редакторе скрыты.
  const subFields = compositeType ? resolveEffectiveFields(compositeType, allDocTypes).filter(f => !f.computed) : [];
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
  // Значение пришло с несколькими заполненными вариантами (issue #756) — считаем по СОХРАНЁННОМУ
  // значению, не по options: там к заполненным приплюсован стэш, а он наш собственный и законен.
  const overfilled = filledVariants(subValues, subFields);
  const chip = overfilled.length > 1 ? (
    <span className="text-[11px] text-warning flex items-start gap-1" role="alert">
      <AlertTriangle size={11} className="shrink-0 mt-0.5" />
      <span>{overfilledNote(overfilled)}</span>
    </span>
  ) : (
    <span className="text-[11px] text-fg4 flex items-center gap-1 shrink-0" title="Заполняется ровно один из вариантов">
      <Info size={11} /> заполните одно из
    </span>
  );
  const activeEditor = activeSf && (
    <SubfieldEditor sf={activeSf} value={subValues[activeSf.key]} onChange={setActiveValue}
      allDocTypes={allDocTypes} showValidation={showValidation} setId={setId}
      otherInstances={otherInstances} scope={scope} scopeId={scopeId} docRefMode={docRefMode}
      primitiveTypes={primitiveTypes} enumTypes={enumTypes} />
  );
  const bar = (
    <div className="space-y-1.5">
      {chip}
      <VariantPicker options={options} active={activeKey} onSelect={switchTo} />
    </div>
  );

  // Вложенный union (глубина ≥1) — строка-сводка активного варианта + правка в модалке.
  if (nested) {
    return (
      <>
        <div className="flex items-center gap-2 border border-stroke rounded-lg px-3 py-2 bg-base">
          <button type="button" onClick={() => setModalOpen(true)}
            className="flex-1 min-w-0 text-left text-sm text-fg2 hover:text-fg1 truncate">
            {unionSummary(activeSf, subValues[activeKey], { primitiveTypes, enumTypes })}
          </button>
          {/* Свёрнутая строка показывает ТОЛЬКО активный вариант, поэтому без метки она выглядела бы
              обычными данными — а ✎ и первая правка унесли бы второй вариант (issue #756). Ровно та
              же дверь, что закрыта в списке строк массива; здесь она оставалась открытой. */}
          {overfilled.length > 1 && (
            <span className="flex items-center gap-1 text-[11px] text-warning shrink-0"
              title={overfilledNote(overfilled)} role="img" aria-label={overfilledNote(overfilled)}>
              <AlertTriangle size={12} /> вариантов: {overfilled.length}
            </span>
          )}
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
function unionSummary(sf: SchemaField | null, val: unknown, defs: FieldTypeDefs = {}): string {
  if (!sf) return '(пусто)';
  if (!isVariantFilled(val)) return `${sf.title}: —`;
  if (isFieldRef(val)) return `${sf.title} → ${val.displayName}`;
  if (Array.isArray(val)) return `${sf.title} · ${val.length} стр.`;
  return `${sf.title}: ${formatFieldValue(sf, val, defs).slice(0, 40)}`; // формат по типу (issue #611)
}
