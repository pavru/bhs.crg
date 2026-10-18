import * as Popover from '@radix-ui/react-popover';
import { Building2, Check, Star, X } from 'lucide-react';
import { useId } from 'react';
import { IconButton } from '@/shared/ui/Button';
import { Chip } from '@/shared/ui/Chip';
import { Tooltip } from '@/shared/ui/Tooltip';
import { FavoritesPicker } from './FavoritesPicker';
import { useSiteFilterState } from './siteFilterContext';

/**
 * Отбор «Стройки» в строке фильтров списка (issue #1432) — вместо сегмента «Избранные N / Все N» в шапке.
 * Шапка одна на все экраны, а урезан список не везде, и ей приходилось объясняться длинными подписями
 * («на этом экране не действуют»). Здесь отбор стоит только там, где он действует, — и ровно над тем
 * списком, который урезает.
 *
 * <p>Чип называет состояние словами: «Избранные стройки · скрыто 12» или «Все стройки». Он же — всё
 * свидетельство урезанного списка: строку «скрыто» прежде ставили отдельно (§62.1), теперь она в
 * подписи отбора, и закрыть её по-прежнему нельзя — забытое «включено» прячет данные.</p>
 *
 * <p>Поведение не меняется: состояние ГЛОБАЛЬНОЕ (настройка пользователя), его меняет любой список,
 * и избранное правится здесь же, без похода в профиль.</p>
 *
 * @param hidden сколько строк этого списка скрыто; `undefined` — список не считает.
 * @param partial число — о всём списке, а на экране его часть (поиск по загруженному у накладных):
 *     тогда — «часть скрыта», без числа (ревью PR #1335).
 */
export function SitesFilter({ hidden, partial = false }: { hidden?: number; partial?: boolean }) {
  const filter = useSiteFilterState();
  const heading = useId();
  if (filter.status === 'unavailable') return null;

  if (filter.status === 'loading') {
    return <Chip icon={Building2} disabled data-testid="sites-filter">Стройки…</Chip>;
  }

  const count = filter.favorites.size;
  const total = filter.sites.length;
  const hiddenText = !filter.active || !hidden ? undefined : partial ? 'часть скрыта' : `скрыто ${hidden}`;

  const chip = filter.status === 'error' ? (
    // Не пришло — сказать, а не изображать загрузку: списки при этом полные, отбор не действует.
    <Chip icon={Building2} tone="warning" menu data-testid="sites-filter-error">
      Все стройки — избранные не загрузились
    </Chip>
  ) : (
    <Chip icon={filter.active ? Star : Building2} selected={filter.active} menu count={hiddenText}
      data-testid="sites-filter"
      aria-label={filter.active
        ? `Стройки: избранные, ${count} из ${total}${hiddenText ? `, ${hiddenText}` : ''}`
        : 'Стройки: все'}>
      {filter.active ? 'Избранные стройки' : 'Все стройки'}
    </Chip>
  );

  return (
    <Popover.Root>
      {filter.status === 'error' ? (
        <Tooltip content="Настройки или справочник строек не загрузились. Списки показаны полностью; обновите страницу.">
          <Popover.Trigger asChild>{chip}</Popover.Trigger>
        </Tooltip>
      ) : <Popover.Trigger asChild>{chip}</Popover.Trigger>}
      <Popover.Portal>
        <Popover.Content align="start" sideOffset={6} data-testid="sites-filter-popover" aria-labelledby={heading}
          className="z-50 w-80 max-w-(--radix-popover-content-available-width) max-h-(--radix-popover-content-available-height) overflow-y-auto rounded-lg border border-stroke bg-surface p-4"
          style={{ boxShadow: 'var(--f-shadow28)' }}>
          <div className="flex items-center justify-between mb-2">
            <h2 id={heading} className="text-sm font-semibold text-fg1">Стройки в списках</h2>
            <Popover.Close asChild>
              <IconButton label="Закрыть" size="sm"><X size={14} /></IconButton>
            </Popover.Close>
          </div>
          {/* Выбирать между «избранными» и «всеми» можно, только когда избранное есть: иначе положение
              «Избранные» показало бы всё и выглядело бы сломанным. Тогда остаётся сам выбор строек. */}
          {filter.status === 'ready' && count > 0 && (
            <div role="radiogroup" aria-label="Какие стройки показывать" className="-mx-2 mb-3 pb-3 border-b border-stroke">
              <Choice checked={filter.on} onPick={() => filter.setOn(true)}
                label="Избранные стройки" note={`${count} из ${total}`} />
              <Choice checked={!filter.on} onPick={() => filter.setOn(false)}
                label="Все стройки" note={String(total)} />
            </div>
          )}
          <FavoritesPicker />
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}

function Choice({ checked, onPick, label, note }: { checked: boolean; onPick: () => void; label: string; note: string }) {
  return (
    <button type="button" role="radio" aria-checked={checked} onClick={() => { if (!checked) onPick(); }}
      className="w-full flex items-center gap-2 h-9 px-2 rounded-md text-left text-sm hover:bg-black/5 dark:hover:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand">
      <Check size={16} aria-hidden className={`shrink-0 ${checked ? 'text-brand' : 'invisible'}`} />
      <span className={`flex-1 ${checked ? 'font-medium text-fg1' : 'text-fg2'}`}>{label}</span>
      <span className="text-xs tabular-nums text-fg3">{note}</span>
    </button>
  );
}
