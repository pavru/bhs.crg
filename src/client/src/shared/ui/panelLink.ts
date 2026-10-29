/**
 * Строка страницы в подпанели раздела (issue #1429) — одна на `PageList` и строку «вне
 * пространства»: разойдясь, они дали бы в одной подпанели два вида строк.
 *
 * `muted` — страница вне пространства: тише своих.
 */
export function panelLink(isActive: boolean, muted = false): string {
  return 'flex items-center h-10 px-3 rounded-full text-body-medium select-none state-layer focus-ring ' +
    (isActive
      ? `bg-secondary-container text-on-secondary-container${muted ? '' : ' font-medium'}`
      : 'text-on-surface-variant');
}

/** Микрозаголовок навигации и групп — по docs/UI.md, «Заголовки». Один на подпанель и меню раздела. */
export const MICRO_HEADING = 'px-3 mb-1 text-title-small select-none text-on-surface-variant';
