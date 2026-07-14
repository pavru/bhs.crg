import { Pencil, Trash2, Link2, FileText } from 'lucide-react';
import type { CommonDataEntry, DocumentType } from '@/shared/api/types';

// Общие презентационные части списка «объекты по типу» (issue #88) — устраняют дублирование между
// системным каталогом (SystemCommonDataPage) и скоуп-панелью каталога (ScopedCatalogPanel). Обёртки
// групп (карточка-страница vs рейл-панель) намеренно разные (issue #8, три канала иерархии) и остаются
// на стороне страниц; здесь — группировка, строка объекта и метка роли/прокси.

/** Группировка записей по составному типу (+ «без типа»). Порядок внутри группы — как во входе. */
export function groupObjectsByType(entries: CommonDataEntry[], types: DocumentType[]) {
  const groups = types
    .map(type => ({ type, items: entries.filter(e => e.compositeTypeId === type.id) }))
    .filter(g => g.items.length > 0);
  const noType = entries.filter(e => !types.some(t => t.id === e.compositeTypeId));
  return { groups, noType };
}

/** Метка роли/прокси (issue #89): если запись ссылается (`_baseRef`) на объект ТОГО ЖЕ типа —
 *  показываем «→ реальный» и открываем его по клику. Цель ищется среди siblings. */
export function ProxyRoleMarker({ entry, siblings, onOpen }: {
  entry: CommonDataEntry;
  siblings: CommonDataEntry[];
  onOpen: (e: CommonDataEntry) => void;
}) {
  const br = (entry.data as Record<string, unknown>)?._baseRef;
  const tid = typeof br === 'string' ? br : (br && typeof br === 'object' && 'id' in br ? (br as { id?: string }).id : undefined);
  const target = tid ? siblings.find(e => e.id === tid) : undefined;
  if (!target || target.compositeTypeId !== entry.compositeTypeId) return null;
  return (
    <button type="button" onClick={e => { e.stopPropagation(); onOpen(target); }}
      title="Открыть реальный объект"
      className="flex items-center gap-1 text-[11px] text-fg4 hover:text-brand shrink-0 max-w-[180px] truncate transition-colors">
      <Link2 size={11} className="shrink-0" />→ {target.displayName}
    </button>
  );
}

/** Строка объекта: имя + метка роли + (превью полей / бейдж внеш.документа) + скрытые hover-действия.
 *  <paramref name="dense"/> — компактный вид (панель) vs просторный (страница). Разделитель/фон —
 *  через <paramref name="className"/> на стороне вызывающего. */
export function ObjectRow({
  entry, siblings, onEdit, onDelete, deleteDisabled = false,
  dense = false, showPreview = false, docKind = false, className = '',
}: {
  entry: CommonDataEntry;
  siblings: CommonDataEntry[];
  onEdit: (e: CommonDataEntry) => void;
  onDelete: (e: CommonDataEntry) => void;
  deleteDisabled?: boolean;
  dense?: boolean;
  showPreview?: boolean;
  docKind?: boolean;   // тип-документ во внешнем каталоге — иконка + бейдж (в плотном виде панели)
  className?: string;
}) {
  const icon = dense ? 12 : 13;
  const preview = Object.entries(entry.data).filter(([, v]) => v != null && v !== '')
    .slice(0, 3).map(([k, v]) => `${k}: ${v}`).join(' · ');
  return (
    <div className={`group flex items-center transition-colors ${dense ? 'gap-3 px-3 py-2 hover:bg-muted' : 'gap-4 px-4 py-3 hover:bg-base'} ${className}`}>
      {docKind && dense && <FileText size={12} className="text-warning shrink-0" />}
      <span className={`flex-1 text-sm truncate ${dense ? 'text-fg1' : 'font-medium text-fg1'}`}>{entry.displayName}</span>
      <ProxyRoleMarker entry={entry} siblings={siblings} onOpen={onEdit} />
      {docKind && dense && (
        <span className="text-xs px-1.5 py-0.5 rounded bg-warning-subtle text-warning font-medium shrink-0">внеш. документ</span>
      )}
      {showPreview && preview && (
        <span className="text-xs text-fg4 truncate max-w-xs hidden sm:block">{preview}</span>
      )}
      <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 transition-opacity shrink-0">
        <button type="button" onClick={() => onEdit(entry)} title="Редактировать"
          className={`rounded transition-colors ${dense ? 'p-1 text-stroke-strong hover:text-fg2' : 'p-1.5 text-fg4 hover:text-fg2'}`}>
          <Pencil size={icon} />
        </button>
        <button type="button" onClick={() => onDelete(entry)} disabled={deleteDisabled} title="Удалить"
          className={`rounded transition-colors disabled:opacity-30 ${dense ? 'p-1 text-stroke-strong hover:text-danger' : 'p-1.5 text-fg4 hover:text-danger'}`}>
          <Trash2 size={icon} />
        </button>
      </div>
    </div>
  );
}
