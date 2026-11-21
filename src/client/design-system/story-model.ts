import type { ReactNode } from 'react';

export type StoryProps = Record<string, unknown>;
export type Registry = Record<string, unknown>;
export interface Story {
  id: string;
  component: string;
  variant: string;
  props: StoryProps;
  children?: ReactNode;
  width?: number;
  state?: string;
  renderAs?: string;
}
export type StoryCategory = 'actions' | 'fields' | 'selection' | 'data' | 'navigation' | 'layers' | 'feedback' | 'files' | 'composition' | 'filters';
export const categories: Record<StoryCategory, string> = {
  actions: 'Действия', fields: 'Поля и выбор значений', selection: 'Переключение и отбор',
  data: 'Списки и таблицы', navigation: 'Навигация и оболочка', layers: 'Окна, меню и подсказки',
  feedback: 'Статусы и обратная связь', files: 'Файлы и просмотр', composition: 'Составные элементы', filters: 'Общие фильтры',
};
export function categoryFor(name: string): StoryCategory {
  if (/Filter|Condition|SiteOptions|FavoritesPicker/.test(name)) return 'filters';
  if (/^Menu|Tooltip|Popover|Modal|Dialog|Sheet|WindowStep|Shortcuts|LeaveQuestion|CommandPalette|RowActionsMenu|^(Root|Trigger|Anchor|Content|Portal|Close|Arrow)$/.test(name)) return 'layers';
  if (/Pdf|Viewer|FileDropZone|ImageView|Magnifier|CodeEditor/.test(name)) return 'files';
  if (/Button|^Chip$|MoveButtons|PrimaryFab/.test(name)) return 'actions';
  if (/Field|Input|^Select|Picker|Calendar|Suggest|DateRange/.test(name)) return 'fields';
  if (/Checkbox|Radio|Switch$|Segmented|ThemeToggle/.test(name)) return 'selection';
  if (/Nav|Section|Pinned|Page|AppShell|Workspace|AccountMenu|Detail|OutsideBanner|HiddenByWorkspace|Tabs|TabPanel/.test(name)) return 'navigation';
  if (/List|Grid|Timeline|Row|SelectionBar|Archived/.test(name)) return 'data';
  if (/Banner|Status|Empty|Badge|Count|Progress|ErrorBoundary|ValueIssue|Scope|Origin|Stale|Reread|Problem/.test(name)) return 'feedback';
  return 'composition';
}
export function parseAxes(variant: string): Record<string, string> {
  return Object.fromEntries(variant.split(', ').map(item => { const i = item.indexOf('='); return i < 0 ? ['State', item] : [item.slice(0, i), item.slice(i + 1)]; }));
}
export function storyId(component: string, variant: string) { return `${component}::${variant}`; }
