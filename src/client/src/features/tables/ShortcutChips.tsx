import { Archive, Check, TriangleAlert } from 'lucide-react';
import { useTableShortcuts, type TableShortcut } from '@/shared/api/tables';
import { shortcutCount, shortcutShown, shortcutState, shortcutTitle, type ShortcutFilter } from './tableShortcuts';

/**
 * Строка «Навести порядок» над таблицей (issue #1186): готовые отборы модуля чипами с числом строк
 * под каждым — «Ссылки на удалённые записи · 3».
 *
 * Строки нет, пока показывать нечего: числа нулевые и ни один отбор не стоит. Ноль не рисуем нарочно —
 * чип с нулём звал бы туда, где пусто. А вот отказ в подсчёте виден: молча убрать строку значило бы
 * сказать «наводить нечего» там, где не посчитано.
 *
 * Про счета экран не знает: подписи, условия и «тихий» вид отбора приходят с сервера.
 */
export function ShortcutChips({ address, filter, enabled, onToggle }: {
  address: string;
  /** Отбор, который стоит на экране: по нему видно, нажат ли чип и можно ли его нажать. */
  filter: ShortcutFilter;
  enabled: boolean;
  onToggle: (shortcut: TableShortcut) => void;
}) {
  const shortcuts = useTableShortcuts(address, enabled);

  if (shortcuts.isError)
    return <p className="mb-2 text-xs text-warning" role="status">Готовые отборы не посчитаны — число строк под ними неизвестно.</p>;

  const shown = (shortcuts.data ?? []).filter(s => shortcutShown(filter, s));
  if (shown.length === 0) return null;

  return (
    <div className="mb-2 flex flex-wrap items-center gap-1.5 text-xs" role="group" aria-label="Готовые отборы">
      <span className="text-fg3">Навести порядок:</span>
      {shown.map(s => {
        const state = shortcutState(filter, s);
        const Icon = state === 'on' ? Check : s.quiet ? Archive : TriangleAlert;
        // Тихий отбор (архив) — без красного: строки под ним верны, их стоит заметить, а не чинить.
        const tone = state === 'on' ? 'border-brand text-brand bg-brand-subtle'
          : s.quiet ? 'border-stroke text-fg3 hover:text-fg1'
          : 'border-danger text-danger hover:bg-danger-subtle';
        return (
          <button key={s.code} type="button" disabled={state === 'complex' || state === 'broken'} aria-pressed={state === 'on'}
            title={shortcutTitle(s, state)} onClick={() => onToggle(s)}
            className={`inline-flex items-center gap-1 rounded-full border px-2.5 py-1 bg-surface disabled:opacity-50 ${tone}`}>
            <Icon size={12} aria-hidden />
            {s.title}
            <span className="font-medium">· {shortcutCount(s)}</span>
          </button>
        );
      })}
    </div>
  );
}
