import { Keep, KeepOff } from './icons';
import { usePinnedPages } from './pinnedPagesContext';
import type { RowAction } from './RowActionsMenu';
import { LIMIT_HINT, MAX_PINNED, PIN_LABEL, UNPIN_LABEL } from './pinnedSections';

/** Пункт «Закрепить страницу» / «Открепить страницу» меню «⋮» страницы подпанели. */
export function usePinAction(): (to: string) => RowAction {
  const { list, has, pin, unpin } = usePinnedPages();
  const full = list.length >= MAX_PINNED;
  return to => has(to)
    ? { key: 'unpin', label: UNPIN_LABEL, icon: KeepOff, onSelect: () => unpin(to) }
    : { key: 'pin', label: PIN_LABEL, icon: Keep, disabled: full, hint: full ? LIMIT_HINT : undefined, onSelect: () => pin(to) };
}
