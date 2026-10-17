/**
 * Строка страницы в подпанели раздела и в меню (issue #1429) — одна на `PageList` и строку «вне
 * пространства»: разойдясь, они дали бы в одной подпанели два вида строк.
 *
 * `muted` — страница вне пространства: тише своих.
 */
export function panelLink(isActive: boolean, muted = false): string {
  return 'flex items-center h-10 px-3 rounded-full text-sm select-none transition-colors ' +
    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand ' +
    (isActive
      ? `bg-tonal text-on-tonal${muted ? '' : ' font-medium'}`
      : `${muted ? 'text-fg3' : 'text-fg2'} hover:bg-black/5 dark:hover:bg-white/10`);
}
