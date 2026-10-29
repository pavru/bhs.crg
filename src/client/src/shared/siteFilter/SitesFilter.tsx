import * as Popover from '@radix-ui/react-popover';
import { Icon } from '@/shared/ui/Icon';
import { Apartment, Check, Close, Star } from '@/shared/ui/icons';
import { useId, useRef, useState, type KeyboardEvent } from 'react';
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
 * <p>Выход — одним щелчком, как у прежней строки «Показать все»: у включённого отбора крестик
 * «Показать все стройки». Выбор положения в окне окно закрывает — дальше смотрят на список; правка
 * избранного галочками — нет.</p>
 *
 * @param hidden сколько строк этого списка скрыто; `undefined` — список не считает.
 * @param partial число — о всём списке, а на экране его часть (поиск по загруженному у накладных):
 *     тогда — «часть скрыта», без числа (ревью PR #1335).
 */
export function SitesFilter({ hidden, partial = false }: { hidden?: number; partial?: boolean }) {
  const filter = useSiteFilterState();
  const heading = useId();
  const [open, setOpen] = useState(false);
  if (filter.status === 'unavailable') return null;

  if (filter.status === 'loading') {
    return <Chip icon={Apartment} disabled data-testid="sites-filter">Стройки…</Chip>;
  }

  const count = filter.favorites.size;
  const total = filter.sites.length;
  const hiddenText = !filter.active || !hidden ? undefined : partial ? 'часть скрыта' : `скрыто ${hidden}`;

  const chip = filter.status === 'error' ? (
    // Не пришло — сказать, а не изображать загрузку: списки при этом полные, отбор не действует.
    <Chip icon={Apartment} tone="warning" menu data-testid="sites-filter-error">
      Все стройки — избранные не загрузились
    </Chip>
  ) : (
    <Chip icon={filter.active ? Star : Apartment} selected={filter.active} menu count={hiddenText}
      data-testid="sites-filter"
      // Имя начинается с видимой подписи (WCAG 2.5.3): голосом нажимают то, что видят.
      aria-label={filter.active
        ? `Избранные стройки${hiddenText ? ` · ${hiddenText}` : ''} — ${count} из ${total}`
        : undefined}
      onRemove={filter.active ? () => filter.setOn(false) : undefined} removeLabel="Показать все стройки">
      {filter.active ? 'Избранные стройки' : 'Все стройки'}
    </Chip>
  );

  return (
    <Popover.Root open={open} onOpenChange={setOpen}>
      {filter.status === 'error' ? (
        <Tooltip content="Настройки или справочник строек не загрузились. Списки показаны полностью; обновите страницу.">
          <Popover.Trigger asChild>{chip}</Popover.Trigger>
        </Tooltip>
      ) : <Popover.Trigger asChild>{chip}</Popover.Trigger>}
      <Popover.Portal>
        <Popover.Content align="start" sideOffset={6} data-testid="sites-filter-popover" aria-labelledby={heading}
          className="z-50 w-80 max-w-(--radix-popover-content-available-width) max-h-(--radix-popover-content-available-height) overflow-y-auto rounded-md border border-outline-variant bg-surface p-4 shadow-elevation-2">
          <div className="flex items-center justify-between mb-2">
            <h2 id={heading} className="text-sm font-semibold text-on-surface">Стройки в списках</h2>
            <Popover.Close asChild>
              <IconButton label="Закрыть" size="sm"><Icon icon={Close} size={18} /></IconButton>
            </Popover.Close>
          </div>
          {/* Выбирать между «избранными» и «всеми» можно, только когда избранное есть: иначе положение
              «Избранные» показало бы всё и выглядело бы сломанным. Тогда остаётся сам выбор строек. */}
          {filter.status === 'ready' && count > 0 && (
            <Choices on={filter.on} onPick={filter.setOn} onDone={() => setOpen(false)}
              favorites={`${count} из ${total}`} all={String(total)} />
          )}
          <FavoritesPicker />
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}

/**
 * «Избранные / Все» — радиогруппа по шаблону APG: в группу ведёт одна остановка Tab (выбранное
 * положение), стрелки переключают положение. Щелчок выбирает и закрывает окно; стрелка — только
 * выбирает: ею перебирают, и окно не должно исчезать из-под клавиатуры.
 */
function Choices({ on, onPick, onDone, favorites, all }: {
  on: boolean; onPick: (on: boolean) => void; onDone: () => void; favorites: string; all: string;
}) {
  const refs = useRef<(HTMLButtonElement | null)[]>([]);
  const items = [
    { value: true, label: 'Избранные стройки', note: favorites },
    { value: false, label: 'Все стройки', note: all },
  ];
  const onKeyDown = (e: KeyboardEvent) => {
    if (!['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].includes(e.key)) return;
    e.preventDefault();
    // Положений два: любая стрелка ведёт к другому.
    const next = items.findIndex(i => i.value !== on);
    onPick(items[next].value);
    refs.current[next]?.focus();
  };
  return (
    <div role="radiogroup" aria-label="Какие стройки показывать" onKeyDown={onKeyDown}
      className="-mx-2 mb-3 pb-3 border-b border-outline-variant">
      {items.map((item, i) => {
        const checked = item.value === on;
        return (
          <button key={item.label} ref={el => { refs.current[i] = el; }} type="button" role="radio"
            aria-checked={checked} tabIndex={checked ? 0 : -1}
            onClick={() => { if (!checked) onPick(item.value); onDone(); }}
            className="w-full flex items-center gap-2 h-9 px-2 rounded-sm text-left text-sm text-on-surface state-layer focus-ring">
            <Icon icon={Check} size={18} aria-hidden className={`shrink-0 ${checked ? 'text-primary' : 'invisible'}`} />
            <span className={`flex-1 ${checked ? 'font-medium text-on-surface' : 'text-on-surface-variant'}`}>{item.label}</span>
            <span className="text-xs tabular-nums text-on-surface-variant">{item.note}</span>
          </button>
        );
      })}
    </div>
  );
}
