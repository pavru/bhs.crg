import { Pencil, Trash2, Link2, FileText } from 'lucide-react';
import { IconButton } from '@/shared/ui/Button';
import type { CommonDataEntry, DocumentType } from '@/shared/api/types';

// Общие презентационные части списка «объекты по типу» (issue #88) — устраняют дублирование между
// системным каталогом (SystemCommonDataPage) и скоуп-панелью каталога (ScopedCatalogPanel). Обёртки
// групп (карточка-страница vs рейл-панель) намеренно разные (issue #8, три канала иерархии) и остаются
// на стороне страниц; здесь — группировка, строка объекта и метка роли/прокси.

/**
 * Комплексный текстовый матч записи каталога (issue #249). Ищем подстроку `query` (регистронезависимо)
 * в: имени записи, её алиасах, имени её типа (искали «орга» → находим тип «Организация») и значениях
 * собственных скалярных полей. Служебные ключи (`_baseRef` и пр., префикс `_`) и составные значения
 * (объекты/массивы) пропускаем — как в превью строки. Пустой запрос матчит всё.
 */
export function entryMatchesQuery(entry: CommonDataEntry, typeName: string | undefined, query: string): boolean {
  const q = query.trim().toLowerCase();
  if (!q) return true;
  if (entry.displayName.toLowerCase().includes(q)) return true;
  if (entry.aliases?.some(a => a.toLowerCase().includes(q))) return true;
  if (typeName && typeName.toLowerCase().includes(q)) return true;
  return Object.entries(entry.data).some(([k, v]) =>
    !k.startsWith('_') && v != null && typeof v !== 'object' && String(v).toLowerCase().includes(q));
}

/** Группировка записей по составному типу (+ «без типа»). Порядок внутри группы — как во входе. */
export function groupObjectsByType(entries: CommonDataEntry[], types: DocumentType[]) {
  const groups = types
    .map(type => ({ type, items: entries.filter(e => e.compositeTypeId === type.id) }))
    .filter(g => g.items.length > 0);
  const noType = entries.filter(e => !types.some(t => t.id === e.compositeTypeId));
  return { groups, noType };
}

/** Метка роли/прокси (issue #89): если запись ссылается (`_baseRef`) на объект ТОГО ЖЕ типа —
 *  показываем «→ реальный» и открываем его по клику. Цель ищется среди `resolvePool` (если задан —
 *  вся scope-цепочка, чтобы находить прокси-цель уровнем ВЫШЕ; иначе — `siblings` того же scope). */
export function ProxyRoleMarker({ entry, siblings, resolvePool, onOpen }: {
  entry: CommonDataEntry;
  siblings: CommonDataEntry[];
  /** Пул для резолва цели (обычно scope-цепочка `useCommonDataForScope`) — покрывает кросс-scope прокси. */
  resolvePool?: CommonDataEntry[];
  onOpen: (e: CommonDataEntry) => void;
}) {
  const br = (entry.data as Record<string, unknown>)?._baseRef;
  const tid = typeof br === 'string' ? br : (br && typeof br === 'object' && 'id' in br ? (br as { id?: string }).id : undefined);
  const target = tid ? (resolvePool ?? siblings).find(e => e.id === tid) : undefined;
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
  entry, siblings, resolvePool, onEdit, onDelete, deleteDisabled = false,
  dense = false, showPreview = false, docKind = false, className = '',
}: {
  entry: CommonDataEntry;
  siblings: CommonDataEntry[];
  /** Пул для резолва прокси-цели (scope-цепочка) — покрывает кросс-scope прокси; см. ProxyRoleMarker. */
  resolvePool?: CommonDataEntry[];
  onEdit: (e: CommonDataEntry) => void;
  onDelete: (e: CommonDataEntry) => void;
  deleteDisabled?: boolean;
  dense?: boolean;
  showPreview?: boolean;
  docKind?: boolean;   // тип-документ во внешнем каталоге — иконка + бейдж (в плотном виде панели)
  className?: string;
}) {
  const icon = dense ? 12 : 13;
  // Превью строки: только собственные скалярные поля. Исключаем служебные ключи (`_baseRef` и пр.,
  // с префиксом `_`) — иначе в UI протекал сырой uuid; и составные/массивные значения (объекты) —
  // иначе рендерился `[object Object]` (их содержимое смотрят в редакторе записи).
  const preview = Object.entries(entry.data)
    .filter(([k, v]) => !k.startsWith('_') && v != null && v !== '' && typeof v !== 'object')
    .slice(0, 3).map(([k, v]) => `${k}: ${v}`).join(' · ');
  return (
    <div className={`group flex items-center transition-colors ${dense ? 'gap-3 px-3 py-2 hover:bg-muted' : 'gap-4 px-4 py-3 hover:bg-base'} ${className}`}>
      {docKind && dense && <FileText size={12} className="text-warning shrink-0" />}
      <span className={`flex-1 min-w-[8rem] text-sm truncate ${dense ? 'text-fg1' : 'font-medium text-fg1'}`}>{entry.displayName}</span>
      <ProxyRoleMarker entry={entry} siblings={siblings} resolvePool={resolvePool} onOpen={onEdit} />
      {docKind && dense && (
        <span className="text-xs px-1.5 py-0.5 rounded bg-warning-subtle text-warning font-medium shrink-0">внеш. документ</span>
      )}
      {showPreview && preview && (
        <span className="text-xs text-fg4 truncate max-w-xs hidden sm:block">{preview}</span>
      )}
      <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 transition-opacity shrink-0">
        <IconButton label="Редактировать" size="sm" onClick={() => onEdit(entry)}>
          <Pencil size={icon} />
        </IconButton>
        <IconButton label="Удалить" size="sm" danger onClick={() => onDelete(entry)} disabled={deleteDisabled}>
          <Trash2 size={icon} />
        </IconButton>
      </div>
    </div>
  );
}
