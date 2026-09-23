/**
 * Таблица массива отдельным окном: обёртка со стойкими ширинами колонок и тело.
 *
 * <p>Выделено из `ComplexFields.tsx` (issue #1014). Опирается только на ячейки
 * (`./ComplexCells`); на редакторы полей не ссылается, поэтому цикла здесь нет.</p>
 */
import { useState, useRef } from 'react';
import { Clipboard, GripVertical, Plus, Trash2 } from 'lucide-react';
import { toggleInSet } from '@/shared/utils/toggleInSet';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import type { CatalogScope, DocumentType } from '@/shared/api/types';
import { useListPrimitiveTypes } from '@/shared/api/primitiveTypes';
import { resolveEffectiveFields, getDefaultValues, type SchemaField } from '@/shared/api/schema';
import { useListEnumTypes } from '@/shared/api/enumTypes';
import { ROW_DRAG_MIME, TABLE_SHOWN_TYPES, defaultColWidth } from './constants';
import { PasteMappingModal } from './PasteMappingModal';
import { newLocalId } from '@/shared/utils/localId';
import { TableCell } from './ComplexCells';

// ─── Array table modal ────────────────────────────────────────────────────────

interface ArrayTableModalProps {
  open: boolean; onOpenChange: (v: boolean) => void;
  field: SchemaField; compositeType: DocumentType | null; allDocTypes: DocumentType[];
  items: Record<string, unknown>[];
  /** @param origins место каждой строки среди исходных (null — добавлена в таблице), см. mergeTableRows. */
  onSave: (rows: Record<string, unknown>[], origins: (number | null)[]) => void;
  setId?: string; scope?: CatalogScope; scopeId?: string | null;
}

/**
 * Таблица массива. Тело монтируется по открытию (issue #858).
 *
 * <p>Правки идут по СВОЕЙ копии строк, и снимок с `items` раньше делал эффект на `open`. Снимок,
 * сделанный эффектом, — это всегда лишний коммит с чужим содержимым между ними: первый рендер
 * открытой таблицы успевал показать пустой список и старые «личности строк» (#755), по которым
 * потом считается, какой слот удалён. Мы вместо этого заводим состояние заново — снимок делает
 * инициализатор `useState`, и первого-неправильного рендера не существует.</p>
 */
export function ArrayTableModal(props: ArrayTableModalProps) {
  // Ширины колонок живут в ОБЁРТКЕ, а не в теле: их человек задаёт руками, и переживать закрытие
  // таблицы они обязаны — как переживали, пока тело было смонтировано постоянно. Прежний эффект не
  // трогал их намеренно, сбрасывая только строки и выбор (поймано ревью PR #861).
  const [colWidths, setColWidths] = useState<Record<string, number>>({});
  return props.open
    ? <ArrayTableModalBody {...props} colWidths={colWidths} setColWidths={setColWidths} />
    : null;
}

function ArrayTableModalBody({
  onOpenChange, field, compositeType, allDocTypes, items, onSave,
  setId, scope, scopeId, colWidths, setColWidths,
}: ArrayTableModalProps & {
  colWidths: Record<string, number>;
  setColWidths: React.Dispatch<React.SetStateAction<Record<string, number>>>;
}) {
  const [rows, setRows] = useState<Record<string, unknown>[]>(() => items.map(r => ({ ...r })));
  // Стабильные id строк (issue #171): переживают reorder/удаление, служат ключом выбора.
  const [rowIds, setRowIds] = useState<string[]>(() => items.map(() => newLocalId()));
  // Личности строк, какими они были при ОТКРЫТИИ таблицы. Только по ним видно, КАКОЙ слот исчез
  // при удалении: сами строки после правки неотличимы, а порядок мог измениться (issue #755).
  const [openedIds] = useState<string[]>(rowIds);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [dragIdx, setDragIdx] = useState<number | null>(null);
  const [dropIdx, setDropIdx] = useState<number | null>(null);
  const [pasteOpen, setPasteOpen] = useState(false);
  const [pasteText, setPasteText] = useState('');

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
    setRowIds(prev => [...prev, newLocalId()]);
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
    setSelected(prev => toggleInSet(prev, id));
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
    <Modal open onOpenChange={onOpenChange}
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
          setRowIds(prev => [...prev, ...newRows.map(() => newLocalId())]);
        }}
      />
    </Modal>
  );
}
