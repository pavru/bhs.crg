// Порядок Tab для слоёв над окном (docs/UI.md, «Слои над окном»): слой лежит в конце body, вне порядка
// Tab формы, и с его края фокус переносят сами — на следующую остановку после якоря.

const STOP = 'button, input, select, textarea, a[href], [tabindex]';

/** Остановки Tab внутри `root` в порядке документа: видимые, доступные, без `tabindex="-1"`. */
export function tabStops(root: ParentNode): HTMLElement[] {
  return [...root.querySelectorAll<HTMLElement>(STOP)]
    .filter(el => el.tabIndex >= 0 && !el.matches(':disabled') && !el.closest('[hidden], [inert]')
      && el.getClientRects().length > 0);
}

/**
 * Следующая остановка Tab после `from` — в пределах его окна (`[role="dialog"]`: окно или поповер,
 * где стоит поле) или страницы. Только для фокуса: где рисовать слой, это место не решает.
 */
export function nextTabStop(from: HTMLElement): HTMLElement | undefined {
  const stops = tabStops(from.closest<HTMLElement>('[role="dialog"]') ?? document.body);
  const at = stops.indexOf(from);
  return at < 0 ? undefined : stops[at + 1];
}
