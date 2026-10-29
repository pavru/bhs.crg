/**
 * Таблица массива отдельным окном: обёртка со стойкими ширинами колонок и тело. Массив, стоящий
 * сам в окне (поле составного в окне), открывает таблицу шагом этого окна (`WindowStep`, issue #1384),
 * и вставка из Excel над ней — тоже шагом.
 *
 * <p>Выделено из `ComplexFields.tsx` (issue #1014). Опирается только на ячейки
 * (`./ComplexCells`); на редакторы полей не ссылается, поэтому цикла здесь нет.</p>
 */
import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { ContentPaste, Delete, DragIndicator } from '@/shared/ui/icons';
import { toggleInSet } from '@/shared/utils/toggleInSet';
import { ModalExit } from '@/shared/ui/Modal';
import { WindowStep } from '@/shared/ui/WindowStep';
import { Button, IconButton } from '@/shared/ui/Button';
import { Checkbox } from '@/shared/ui/Checkbox';
import { EditGrid, type EditGridColumn } from '@/shared/ui/EditGrid';
import type { CatalogScope, DocumentType } from '@/shared/api/types';
import { useListPrimitiveTypes } from '@/shared/api/primitiveTypes';
import { resolveEffectiveFields, getDefaultValues, type SchemaField } from '@/shared/api/schema';
import { useListEnumTypes } from '@/shared/api/enumTypes';
import { ROW_DRAG_MIME, TABLE_SHOWN_TYPES, defaultColWidth } from './constants';
import { PasteMappingModal } from './PasteMappingModal';
import { newLocalId } from '@/shared/utils/localId';
import { TableCell } from './ComplexCells';
import { SuggestOwner } from './suggestContext';

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
  // Строки добавляют, удаляют и двигают кнопками — окно этого не замечает: признак правок явный.
  // По ССЫЛКЕ, а не записью значения: рендер идёт на каждую клавишу и каждый `dragover`, а таблица
  // бывает в сотни строк с картинками в data-URI — писать её в JSON на каждый кадр нельзя (ревью
  // PR #1311). Цена: строка, изменённая и возвращённая как была, остаётся правкой.
  const [opened] = useState(rows);
  const edited = rows !== opened;
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

  // Перетаскивание (issue #171): строка ложится туда, где черта. Над строкой ниже тащимой черта
  // встаёт ПОД неё — иначе строка, брошенная на соседа снизу, осталась бы на месте.
  const drag = {
    dragging: dragIdx,
    target: dropIdx,
    onOver: (e: React.DragEvent, i: number) => {
      if (dragIdx === null) return;
      e.preventDefault();
      const target = i > dragIdx ? i + 1 : i;
      if (dropIdx !== target) setDropIdx(target);
    },
    onDrop: () => {
      if (dragIdx !== null && dropIdx !== null) moveRow(dragIdx, dropIdx > dragIdx ? dropIdx - 1 : dropIdx);
      setDragIdx(null);
      setDropIdx(null);
    },
  };

  const columns: EditGridColumn<Record<string, unknown>>[] = [
    {
      key: '@select', label: 'Выбор', size: 34,
      header: (
        <span className="flex items-center justify-center">
          <Checkbox size="sm" aria-label="Выбрать все строки"
            checked={rowIds.length > 0 && selected.size === rowIds.length}
            indeterminate={selected.size > 0 && selected.size < rowIds.length}
            onChange={toggleAll} />
        </span>
      ),
      cell: (_, i) => (
        <span className="flex items-center justify-center">
          <Checkbox size="sm" checked={selected.has(rowIds[i])} onChange={() => toggleSelect(rowIds[i])}
            aria-label={`Выбрать строку ${i + 1}`} />
        </span>
      ),
    },
    {
      key: '@index', label: '#', size: 44, rowHeader: true,
      header: <span className="flex justify-center font-normal text-on-surface-variant">#</span>,
      cell: (_, i) => (
        <div className="flex items-center justify-center gap-0.5">
          <IconButton label={`Переместить строку ${i + 1}: стрелки вверх/вниз`}
            title="Перетащить для изменения порядка (или стрелки ↑↓)"
            size="sm"
            className="cursor-grab active:cursor-grabbing"
            draggable
            onDragStart={e => {
                setDragIdx(i);
                e.dataTransfer.effectAllowed = 'move';
                e.dataTransfer.setData(ROW_DRAG_MIME, String(i));
              }}
            onDragEnd={() => { setDragIdx(null); setDropIdx(null); }}
            onKeyDown={e => {
                if (e.key === 'ArrowUp') { e.preventDefault(); e.stopPropagation(); moveRow(i, i - 1); }
                else if (e.key === 'ArrowDown') { e.preventDefault(); e.stopPropagation(); moveRow(i, i + 1); }
              }}>
            <Icon icon={DragIndicator} size={18} />
          </IconButton>
          <span className="text-body-small text-on-surface-variant font-mono">{i + 1}</span>
        </div>
      ),
    },
    ...tableFields.map((f): EditGridColumn<Record<string, unknown>> => ({
      key: f.key, label: f.title, size: getW(f),
      header: (
        <>
          <span className="block truncate font-semibold text-on-surface-variant select-none">
            {f.title}{f.required && <span className="text-error ml-0.5">*</span>}
          </span>
          {/* Шапка липкая, а значит позиционирована: разделитель встаёт по её правому краю. */}
          <div role="separator" aria-orientation="vertical" tabIndex={0}
            aria-label={`Ширина колонки «${f.title}» — стрелки ←→`}
            onMouseDown={e => startResize(e, f.key, getW(f))}
            onKeyDown={e => {
              if (e.key === 'ArrowLeft') { e.preventDefault(); setColWidths(p => ({ ...p, [f.key]: Math.max(44, getW(f) - 16) })); }
              else if (e.key === 'ArrowRight') { e.preventDefault(); setColWidths(p => ({ ...p, [f.key]: getW(f) + 16 })); }
            }}
            className="absolute inset-y-0 right-0 z-[1] w-[5px] cursor-col-resize transition-colors
              hover:bg-primary-container/40 focus-visible:bg-primary focus-visible:outline-none" />
        </>
      ),
      cell: (row, i) => {
        const compositeForField = f.type === 'complex'
          ? allDocTypes.find(dt => dt.id === f.typeId) ?? null : null;
        return (
          <TableCell field={f} value={row[f.key]} onChange={v => updateCell(i, f.key, v)}
            compositeType={compositeForField} setId={setId} allDocTypes={allDocTypes}
            scope={scope} scopeId={scopeId} primitiveTypeDef={primDef(f)} enumTypeDef={enumDef(f)} />
        );
      },
    })),
  ];

  return (
    <WindowStep onBack={() => onOpenChange(false)}
      title={`${compositeType?.name ?? field.title} — таблица`}
      wide isDirty={edited}
      footer={
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-1">
            {selected.size > 0 ? (
              <>
                <span className="text-body-medium font-medium text-on-surface-variant px-2">Выбрано: {selected.size}</span>
                <Button variant="text" size="sm" danger icon={<Icon icon={Delete} size={18} />} onClick={deleteSelected}>Удалить выбранные</Button>
                <Button variant="text" size="sm" onClick={() => setSelected(new Set())}>Сбросить</Button>
              </>
            ) : (
              <>
                <Button variant="text" size="sm" icon={<Icon icon={ContentPaste} size={18} />} onClick={handlePasteClick}>Вставить из Excel</Button>
              </>
            )}
          </div>
          <div className="flex gap-2">
            <ModalExit />
            <Button variant="filled" onClick={handleSave}>Применить</Button>
          </div>
        </div>
      }>
      {/* Клеткам — тип строки (issue #1533): уровень записи таблица берёт у формы вокруг. */}
      <SuggestOwner type={field.typeId}>
        <EditGrid sheet label={`Строки: ${compositeType?.name ?? field.title}`} rows={rows} rowKey={(_, i) => rowIds[i]}
          columns={columns} selected={(_, i) => selected.has(rowIds[i])} drag={drag}
          empty="Нет строк — нажмите «Добавить строку»"
          add={{ label: 'Добавить строку', onAdd: addRow }} onRemove={(_, i) => removeRow(i)} />
      </SuggestOwner>
      {hiddenFields.length > 0 && (
        <p className="text-body-small text-on-surface-variant mt-3">
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
    </WindowStep>
  );
}
