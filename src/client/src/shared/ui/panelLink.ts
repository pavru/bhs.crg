/**
 * Строка страницы в подпанели раздела (issue #1429) — одна на `PageList` и строку «вне
 * пространства»: разойдясь, они дали бы в одной подпанели два вида строк. По образцу пункта MD3
 * navigation drawer: 48, форма full, `label-large`; длинная подпись обрезается многоточием.
 *
 * Страницы вне пространства от своих отличает место — раскрываемая группа после своих, — а не цвет
 * и вес (sharedui-md3-15).
 */
export function panelLink(isActive: boolean): string {
  return 'flex items-center h-12 px-4 rounded-full text-label-large select-none state-layer focus-ring ' +
    (isActive ? 'bg-secondary-container text-on-secondary-container' : 'text-on-surface-variant');
}

/** Микрозаголовок навигации и групп — по docs/UI.md, «Заголовки». Один на подпанель и меню раздела. */
export const MICRO_HEADING = 'px-4 mb-1 text-title-small select-none text-on-surface-variant';
