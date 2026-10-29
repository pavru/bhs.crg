import { Archive, Warning } from '@/shared/ui/icons';
import { Chip } from '@/shared/ui/Chip';
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
      <span className="text-on-surface-variant">Навести порядок:</span>
      {shown.map(s => {
        const state = shortcutState(filter, s);
        // Тихий отбор (архив) — без красного: строки под ним верны, их стоит заметить, а не чинить.
        return (
          <Chip key={s.code} selected={state === 'on'} icon={s.quiet ? Archive : Warning}
            tone={s.quiet ? 'neutral' : 'danger'} count={shortcutCount(s)}
            disabled={state === 'complex' || state === 'broken'}
            title={shortcutTitle(s, state)} onClick={() => onToggle(s)}>
            {s.title}
          </Chip>
        );
      })}
    </div>
  );
}
