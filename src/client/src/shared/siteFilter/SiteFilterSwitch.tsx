import * as Popover from '@radix-ui/react-popover';
import { ChevronDown, Star, X } from 'lucide-react';
import type { ReactNode } from 'react';
import { FavoritesPicker } from './FavoritesPicker';
import { useWorkspace } from '@/shared/ui/workspaceContext';
import { useSiteFilterState } from './siteFilterContext';

const quiet = 'flex items-center gap-1.5 h-8 px-3 rounded-full text-sm text-fg3 hover:bg-black/5 dark:hover:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand';

/** Поповер выбора избранного — общий для всех состояний шапки, меняется только кнопка-вход. */
function PickerPopover({ trigger }: { trigger: ReactNode }) {
  return (
    <Popover.Root>
      <Popover.Trigger asChild>{trigger}</Popover.Trigger>
      <Popover.Portal>
        <Popover.Content align="start" sideOffset={6} data-testid="favorites-popover"
          className="z-50 w-80 rounded-lg border border-stroke bg-surface p-4"
          style={{ boxShadow: 'var(--f-shadow28)' }}>
          <div className="flex items-center justify-between mb-2">
            <h2 className="text-sm font-semibold text-fg1">Избранные стройки</h2>
            <Popover.Close aria-label="Закрыть"
              className="flex items-center justify-center w-7 h-7 rounded-full text-fg4 hover:text-fg1 hover:bg-muted">
              <X size={14} />
            </Popover.Close>
          </div>
          <FavoritesPicker />
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}

/**
 * «Избранные стройки» в шапке (issue #1218, разбор Дизайнера §62.1). Стоит слева, за переключателем
 * рабочего пространства: оба — «что я сейчас вижу», а не действие.
 *
 * <p>Сегмент из ДВУХ названных положений, а не чип-переключатель: у подсвеченного чипа не понять,
 * «включено» это или «нажми, чтобы включить». Состояние читается без цвета — словом выбранного
 * положения, заливкой и `aria-pressed`.</p>
 *
 * <p>Сегмент горит, только если на экране смонтирован список, который фильтр урезает (`useSiteFilter`).
 * Иначе шапка врала бы: она одна на все экраны, а урезан список не везде.</p>
 */
export function SiteFilterSwitch() {
  const filter = useSiteFilterState();
  // Разделитель — только когда слева стоит переключатель пространства: иначе он висел бы у края.
  const { shown } = useWorkspace();
  if (filter.status === 'unavailable') return null;
  return <>{shown && <span className="mx-1 h-5 w-px bg-stroke" aria-hidden />}<SwitchContent /></>;
}

function SwitchContent() {
  const filter = useSiteFilterState();

  if (filter.status === 'unavailable') return null;
  if (filter.status === 'loading') {
    return <span className="h-8 px-3 flex items-center text-sm text-fg4" data-testid="site-filter-loading">Избранные стройки…</span>;
  }

  const count = filter.favorites.size;
  const total = filter.sites.length;

  // Избранного нет — тихая кнопка к выбору, а не недоступный переключатель: у отключённого пути нет.
  if (count === 0) {
    return (
      <PickerPopover trigger={
        <button type="button" className={quiet} data-testid="site-filter-empty">
          <Star size={14} aria-hidden /> Избранные стройки
        </button>} />
    );
  }

  if (filter.consumers === 0) {
    return (
      <PickerPopover trigger={
        <button type="button" className={quiet} data-testid="site-filter-idle">
          <Star size={14} aria-hidden /> Избранные стройки — на этом экране не действуют
        </button>} />
    );
  }

  const seg = (pressed: boolean) =>
    `h-7 px-3 rounded-full text-sm whitespace-nowrap transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand ${
      pressed ? 'bg-tonal text-on-tonal font-medium' : 'text-fg3 hover:bg-black/5 dark:hover:bg-white/10'}`;

  return (
    <div className="flex items-center gap-1" data-testid="site-filter-switch">
      <div role="group" aria-label="Избранные стройки"
        title={filter.on ? `Показаны только избранные стройки: ${count} из ${total}` : 'Показаны все стройки'}
        className="flex items-center gap-0.5 p-0.5 rounded-full border border-stroke">
        <button type="button" aria-pressed={filter.on} className={seg(filter.on)}
          onClick={() => { if (!filter.on) filter.setOn(true); }}>
          <span className="inline-flex items-center gap-1">
            <Star size={13} className={filter.on ? 'fill-current' : ''} aria-hidden /> Избранные {count}
          </span>
        </button>
        <button type="button" aria-pressed={!filter.on} className={seg(!filter.on)}
          onClick={() => { if (filter.on) filter.setOn(false); }}>
          Все {total}
        </button>
      </div>
      <PickerPopover trigger={
        <button type="button" aria-label="Изменить список" title="Изменить список"
          className="flex items-center justify-center w-7 h-7 rounded-full text-fg3 hover:bg-black/5 dark:hover:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand">
          <ChevronDown size={14} />
        </button>} />
    </div>
  );
}
