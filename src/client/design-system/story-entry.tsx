import React, { Component, createElement, useEffect, useState, type ReactNode } from 'react';
import { createRoot } from 'react-dom/client';
import '@fontsource/roboto/400.css';
import '@fontsource/roboto/500.css';
import '@fontsource/roboto/700.css';
import '@fontsource/roboto-mono/400.css';
import './catalog.css';
import './story.css';
import { setFormatLocale } from '@/shared/format/format';
import { allStories } from './catalog-stories';
import metadata from 'virtual:catalog-metadata';
import { StoryProviders } from './story-providers';
import { activateStory } from './story-state';
import { installStoryApi, apiCalls } from './story-api';
import type { Registry, StoryProps } from './story-model';

declare global {
  interface Window {
    __catalogStory?: { ready: boolean; id: string; error?: string; apiCalls: string[] };
    __renderCatalogStory?: (id: string, overrides?: StoryProps, theme?: 'light' | 'dark') => void;
  }
}
const query = new URLSearchParams(location.search);
const id = query.get('story') ?? '';
let currentId = id;
const theme = query.get('theme') === 'dark' ? 'dark' : 'light';
document.documentElement.dataset.theme = theme;
setFormatLocale('ru-RU');
installStoryApi();

function publish(error?: string) {
  window.__catalogStory = { ready: true, id: currentId, error, apiCalls };
  parent.postMessage({ type: 'bhs-catalog-story', id: currentId, error }, location.origin);
}
class Boundary extends Component<{ children: ReactNode }, { error?: string }> {
  state: { error?: string } = {};
  static getDerivedStateFromError(error: Error) { return { error: error.message }; }
  componentDidCatch(error: Error) { publish(error.message); }
  render() { return this.state.error ? <p role="alert" className="story-error">Не удалось показать образец: {this.state.error}</p> : this.props.children; }
}

export function LiveElement({ type, initial, children }: { type: React.ElementType; initial: StoryProps; children?: ReactNode }) {
  const [props, setProps] = useState(initial);
  const [visible, setVisible] = useState(true);
  const update = (key: string, value: unknown) => setProps(previous => ({ ...previous, [key]: value }));
  const callbacks: StoryProps = {};
  if ('onChange' in props) callbacks.onChange = (next: unknown, second?: unknown) => {
    if ('from' in props && 'to' in props) { setProps(previous => ({ ...previous, from: next, to: second })); return; }
    if ('cond' in props) { update('cond', next); return; }
    if ('filter' in props && typeof next === 'function') { update('filter', next(props.filter)); return; }
    const value = next && typeof next === 'object' && 'target' in next ? (next as React.ChangeEvent<HTMLInputElement>).target.value : next;
    update('checked' in props ? 'checked' : 'selected' in props && value instanceof Set ? 'selected' : 'value', value);
  };
  if ('onValueChange' in props) callbacks.onValueChange = (value: string) => update('value', value);
  if ('onOpenChange' in props) callbacks.onOpenChange = (open: boolean) => update('open', open);
  if ('onClose' in props && 'open' in props) callbacks.onClose = () => update('open', false);
  if ('onStay' in props && 'open' in props) callbacks.onStay = () => update('open', false);
  if ('onLeave' in props && 'open' in props) callbacks.onLeave = () => update('open', false);
  if ('onBack' in props) callbacks.onBack = () => setVisible(false);
  if ('onCheckedChange' in props) callbacks.onCheckedChange = (checked: boolean) => update('checked', checked);
  if ('onToggle' in props && 'open' in props) callbacks.onToggle = () => update('open', !props.open);
  if ('onClick' in props && typeof props.selected === 'boolean') callbacks.onClick = () => update('selected', !props.selected);
  if ('onFile' in props) callbacks.onFile = (file: File) => update('file', file);
  if ('onSearch' in props) callbacks.onSearch = (search: string) => update('search', search);
  if ('onSort' in props) callbacks.onSort = (column: { key: string }) => setProps(previous => {
    const current = (previous.sort as { column: string; descending: boolean }[] | undefined)?.find(s => s.column === column.key);
    const descending = current ? !current.descending : false;
    const rows = [...previous.rows as StoryProps[]].sort((a, b) => String(a[column.key]).localeCompare(String(b[column.key]), 'ru', { numeric: true }) * (descending ? -1 : 1));
    return { ...previous, rows, sort: [{ column: column.key, descending }] };
  });
  if ('onRemove' in props && Array.isArray(props.rows)) callbacks.onRemove = (_row: unknown, index: number) => update('rows', (props.rows as unknown[]).filter((_, i) => i !== index));
  if (props.add && typeof props.add === 'object' && Array.isArray(props.rows)) callbacks.add = { ...props.add, onAdd: () => {
    const row = Object.fromEntries((props.columns as { key: string }[]).map(column => [column.key, column.key === 'name' ? 'Новая строка' : '']));
    let index = (props.rows as StoryProps[]).length;
    while ((props.rows as StoryProps[]).some(existing => existing.id === `example-${index}`)) index++;
    row.id = `example-${index}`;
    update('rows', [...props.rows as unknown[], row]);
  } };
  const child = (node: ReactNode): ReactNode => {
    if (Array.isArray(node)) return node.map((value, index) => <React.Fragment key={index}>{child(value)}</React.Fragment>);
    if (!React.isValidElement<StoryProps>(node)) return node;
    const { children: content, ...initialProps } = node.props;
    if ('onChange' in initialProps || 'onValueChange' in initialProps) return <LiveElement type={node.type as React.ElementType} initial={initialProps}>{content as ReactNode}</LiveElement>;
    return React.cloneElement(node, {}, child(content as ReactNode));
  };
  return visible ? createElement(type, { ...props, ...callbacks }, child(children)) : null;
}

async function main() {
  const loaders = import.meta.glob(['../src/shared/ui/**/*.tsx', '../src/shared/filter/**/*.tsx', '../src/shared/siteFilter/**/*.tsx', '!../src/shared/**/*.test.tsx', '!../src/shared/ui/MonacoEditor.tsx']);
  const registry: Registry = {};
  for (const [path, load] of Object.entries(loaders)) {
    const module = await load() as Registry;
    for (const [name, value] of Object.entries(module)) {
      if (typeof value === 'function' || value && typeof value === 'object' && '$$typeof' in value) registry[name === 'default' ? path.replace(/.*\/|\.tsx$/g, '') : name] = value;
    }
  }
  const stories = allStories(registry, metadata.inventory);
  const root = createRoot(document.getElementById('story-root')!);
  import.meta.hot?.dispose(() => root.unmount());
  const render = (nextId: string, overrides: StoryProps = {}, nextTheme: 'light' | 'dark' = theme) => {
    currentId = nextId;
    window.__catalogStory = { ready: false, id: currentId, apiCalls };
    document.documentElement.dataset.theme = nextTheme;
    const story = stories.find(s => s.id === nextId);
    if (!story) { publish('Образец не найден'); return; }
    const type = registry[story.renderAs ?? story.component] as React.ElementType;
    if (!type) { publish('Компонент не найден: ' + story.component); return; }
    const props = { ...story.props, ...overrides };
    function Ready() {
      useEffect(() => { void activateStory(story!).then(() => { if (currentId === nextId) publish(); }).catch(error => publish(String(error))); }, []);
      return null;
    }
    root.render(<Boundary key={nextId + nextTheme}><StoryProviders theme={nextTheme} component={story.component}>
    <main id="specimen" data-component={story.component} style={{ width: story.width ? Math.min(story.width, innerWidth - 48) : 'fit-content', maxWidth: '100%', minHeight: /PdfViewer|ImageView|AppShell/.test(story.component) ? 560 : undefined, '--md-comp-icon-button-size': story.state === 'Dense' ? '32px' : undefined, '--md-comp-icon-button-icon': story.state === 'Dense' ? '20px' : undefined } as React.CSSProperties}>
      <LiveElement type={type} initial={props}>{story.children}</LiveElement>
    </main><Ready />
    </StoryProviders></Boundary>);
  };
  window.__renderCatalogStory = render;
  const overrides = query.get('props');
  render(id, overrides ? JSON.parse(overrides) as StoryProps : {});
}
void main().catch(error => { document.getElementById('story-root')!.textContent = String(error); publish(String(error)); });
