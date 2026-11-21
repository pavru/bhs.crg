import * as glyphs from '@/shared/ui/icons';
import manifest from '@/shared/ui/icons/manifest.json';
import type { IconDef } from '@/shared/ui/icons';

const definitions = glyphs as unknown as Record<string, IconDef>;
const names: Record<string, IconDef> = {};
for (const [name, spec] of Object.entries(manifest.icons)) {
  const exported = 'export' in spec ? String(spec.export) : name.split('_').map(part => part[0].toUpperCase() + part.slice(1)).join('');
  if (definitions[exported]?.d) names[name] = definitions[exported];
}
const aliases: Record<string, string> = {
  arrow: 'arrow_drop_down', chevD: 'keyboard_arrow_down', chevU: 'keyboard_arrow_up', chevL: 'chevron_left', chevR: 'chevron_right',
  expand_more: 'keyboard_arrow_down', expand_less: 'keyboard_arrow_up', task: 'description',
  ok: 'check_circle', err: 'error', warn: 'warning', edit: 'edit', file: 'description', scan: 'document_scanner',
  menu: 'left_panel_close', menu_open: 'left_panel_open', more: 'more_vert', down: 'keyboard_arrow_down', up: 'keyboard_arrow_up', x: 'close',
  'chevron-down': 'keyboard_arrow_down', 'chevron-up': 'keyboard_arrow_up', 'chevron-left': 'chevron_left', 'chevron-right': 'chevron_right',
  'more-horizontal': 'more_horiz', 'more-vertical': 'more_vert', 'file-text': 'description', 'check-circle': 'check_circle',
  'alert-triangle': 'warning', 'alert-circle': 'error', 'external-link': 'open_in_new', 'plus': 'add', 'trash-2': 'delete',
  'x-circle': 'cancel', 'loader-2': 'progress_activity', 'arrow-left': 'arrow_back', 'arrow-right': 'arrow_forward',
  'folder-open': 'folder_open', 'settings-2': 'tune', 'building-2': 'apartment', 'calendar-days': 'calendar_month',
  'panel-left': 'view_sidebar', 'panel-right': 'view_sidebar', 'circle-help': 'help', 'circle-check': 'check_circle',
  'circle-alert': 'error', 'circle-x': 'cancel', 'list-filter': 'filter_list', 'log-out': 'logout', 'user-round': 'person',
};
export function mockupIcon(name: string): IconDef | undefined {
  return names[aliases[name] ?? name.replace(/-/g, '_')] ?? definitions[name];
}
export function svgMarkup(name: string, size = 18) {
  const icon = mockupIcon(name);
  return icon ? `<svg viewBox="0 -960 960 960" width="${size}" height="${size}" fill="currentColor" aria-hidden="true"><path d="${icon.d}"></path></svg>` : '';
}
/** The archive used both Material placeholders and lucide's imperative helper. */
export function installArchiveIcons() {
  const icons = new Proxy({}, { get: (_, name) => ({ toSvg: () => svgMarkup(String(name)) }) });
  Object.assign(window, { lucide: { icons, createIcons: () => replaceArchiveIcons(document.body) } });
}
export function replaceArchiveIcons(root: ParentNode) {
  for (const path of root.querySelectorAll('svg path[d*="{{"]')) {
    const name = path.getAttribute('d')?.match(/\{\{(?:ic\.)?([^}]+)\}\}/)?.[1];
    const icon = name && mockupIcon(name);
    if (icon) path.setAttribute('d', icon.d);
    else path.removeAttribute('d');
  }
  for (const element of root.querySelectorAll<HTMLElement>('.sym, .msym, .symbol, .material-symbols-outlined, [data-lucide]')) {
    if (element.dataset.restoredIcon) continue;
    const name = element.dataset.lucide ?? element.textContent?.trim() ?? '';
    if (!name || name.length > 60) continue;
    const markup = svgMarkup(name, Number.parseFloat(getComputedStyle(element).fontSize) >= 24 ? 24 : 18);
    if (!markup) continue;
    element.dataset.restoredIcon = name;
    element.style.fontFamily = 'inherit'; element.style.fontSize = 'inherit';
    element.innerHTML = markup;
  }
}
