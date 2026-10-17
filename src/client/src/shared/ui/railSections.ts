import { railSections, type NavItem, type RailId, type RailSectionDef } from './navConfig';

/** Группа подпанели: подпись (у первой может не быть) и пункты подряд. */
export interface PanelGroup {
  label?: string;
  items: NavItem[];
}

/** Раздел рейла для этого человека и этого пространства (issue #1429). */
export interface RailSection {
  def: RailSectionDef;
  groups: PanelGroup[];
  /** Куда ведёт раздел в рейле — первая его страница. */
  home: string;
}

/**
 * Разделы рейла из уже отобранных пунктов: `work` — рабочие внутри пространства, `settings` —
 * администрирование (пространством не сужается). Раздел без единой страницы не показывается: пустой
 * раздел — дверь в пустую подпанель.
 *
 * Отбор по правам и пространству здесь НЕ повторяется — его делают `visibleNav` и `splitNav`, и
 * вторая копия правила разошлась бы с ними молча.
 */
export function buildRail(work: NavItem[], settings: NavItem[]): RailSection[] {
  const pool = [...work, ...settings];
  return railSections.flatMap(def => {
    const items = pool.filter(item => item.rail === def.id);
    return items.length ? [{ def, groups: groupItems(items), home: items[0].to }] : [];
  });
}

/** Пункты подряд одной группы — одна группа; порядок — как в `navConfig`. */
export function groupItems(items: NavItem[]): PanelGroup[] {
  const groups: PanelGroup[] = [];
  for (const item of items) {
    const last = groups.at(-1);
    if (last && last.label === item.group) last.items.push(item);
    else groups.push({ label: item.group, items: [item] });
  }
  return groups;
}

/**
 * Какому разделу принадлежит адрес — по пункту с самым длинным подходящим адресом, как плашка «вне
 * пространства» (`outsideItem`): `/tables/costs.invoices/registry` — «Учёт», а не что-то под
 * `/tables`. Адрес вне разделов (профиль, уведомления) — `null`: подсвечивать в рейле нечего.
 *
 * `items` — ВСЕ видные человеку пункты, и вне пространства тоже: открытая по ссылке страница вне
 * пространства всё равно принадлежит своему разделу.
 */
export function activeRail(pathname: string, items: NavItem[]): RailId | null {
  const owner = [...items]
    .sort((a, b) => b.to.length - a.to.length)
    .find(item => pathname === item.to || pathname.startsWith(`${item.to}/`));
  return owner?.rail ?? null;
}

/** Есть ли у этого адреса подпанель: в карточке стройки её место занимает навигация стройки. */
export function panelAt(pathname: string, def: RailSectionDef): boolean {
  return def.panelOnlyAt === undefined || pathname === def.panelOnlyAt;
}

export function railDef(id: RailId): RailSectionDef {
  return railSections.find(def => def.id === id)!;
}
