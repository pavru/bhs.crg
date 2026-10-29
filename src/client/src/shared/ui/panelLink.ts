/**
 * Строка страницы в подпанели раздела (issue #1429) — одна на `PageList` и строку «вне
 * пространства»: разойдясь, они дали бы в одной подпанели два вида строк.
 *
 * `muted` — страница вне пространства: тише своих.
 */
export function panelLink(isActive: boolean, muted = false): string {
  return 'flex items-center h-10 px-3 rounded-full text-sm select-none transition-colors ' +
    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary ' +
    (isActive
      ? `bg-secondary-container text-on-tonal${muted ? '' : ' font-medium'}`
      : `${muted ? 'text-on-surface-variant' : 'text-on-surface-variant'} hover:bg-black/5 dark:hover:bg-white/10`);
}

/** Микрозаголовок навигации и групп — по docs/UI.md, «Заголовки». Один на подпанель и меню раздела. */
export const MICRO_HEADING = 'px-3 mb-1 text-caption font-semibold uppercase tracking-widest select-none text-on-surface-variant';
