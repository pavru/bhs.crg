import { useEffect, useRef, useState } from 'react';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { Select, SelectItem } from '@/shared/ui/Select';
import { Icon } from '@/shared/ui/Icon';
import { OpenInNew } from '@/shared/ui/icons';
import { useTheme } from '@/shared/ui/themeContext';
import manifest from './mockups/manifest.json';
import { mockupUrl, type RestoredMockup } from './mockup-model';
import { liveMockups, type LiveMockup } from './live-mockups';
import './mockups.css';

const projects: (RestoredMockup | LiveMockup)[] = [...liveMockups, ...manifest.projects];
function Choice({ options, onChange, ...props }: { 'aria-label': string; value: string; onChange: (value: string) => void; options: { value: string; label: string }[] }) {
  return <Select {...props} label={props['aria-label']} density="dense" onValueChange={onChange}>{options.map(option => <SelectItem key={option.value} value={option.value}>{option.label}</SelectItem>)}</Select>;
}
export function Mockups() {
  const query = new URLSearchParams(location.search);
  const requested = query.get('project');
  const [projectId, setProjectId] = useState(projects.find(p => p.id === requested || p.aliases.includes(requested ?? ''))?.id ?? projects.find(p => p.title.startsWith('Экран счёта ·'))?.id ?? projects[0].id);
  const project = projects.find(p => p.id === projectId)!;
  const [pagePath, setPagePath] = useState(query.get('page') ?? query.get('file') ?? project.pages[0].path);
  const page = project.pages.find(p => p.path === pagePath) ?? project.pages[0];
  const [search, setSearch] = useState('');
  const [kind, setKind] = useState('design');
  const [size, setSize] = useState('auto');
  const [original, setOriginal] = useState(false);
  const [zoom, setZoom] = useState('fit');
  const [availableWidth, setAvailableWidth] = useState(900);
  const viewport = useRef<HTMLDivElement>(null);
  const { resolvedTheme } = useTheme();
  const theme = resolvedTheme;
  const matches = projects.filter(p => (kind === 'all' || p.kind === kind) && p.title.toLocaleLowerCase('ru').includes(search.toLocaleLowerCase('ru')));
  const persist = (id: string, path: string) => {
    const url = new URL(location.href); url.searchParams.set('project', id); url.searchParams.set('page', path); url.searchParams.delete('file'); history.replaceState(null, '', url);
  };
  const choose = (item: RestoredMockup | LiveMockup) => { setProjectId(item.id); setPagePath(item.pages[0].path); persist(item.id, item.pages[0].path); };
  const live = 'entry' in project;
  const url = live ? `${project.entry}?${new URLSearchParams({ page: page.path, theme })}` : mockupUrl(project.id, page.path, theme, original);
  const width = size === 'auto' ? page.width ?? 1366 : Number(size);
  const scale = zoom === 'fit' ? Math.min(1, availableWidth / width) : Number(zoom);
  const height = page.height ?? 900;
  useEffect(() => {
    if (!viewport.current) return;
    const observer = new ResizeObserver(([entry]) => setAvailableWidth(entry.contentRect.width));
    observer.observe(viewport.current); return () => observer.disconnect();
  }, []);
  return <div className="mockup-workspace">
    <aside className="mockup-index"><TextField label="Найти макет" placeholder="Найти макет…" density="dense" value={search} onChange={e => setSearch(e.target.value)} />
      <Choice aria-label="Состав каталога" value={kind} onChange={setKind} options={[{ value: 'design', label: 'Макеты' }, { value: 'reference', label: 'Разборы и снимки' }, { value: 'all', label: 'Весь каталог' }]} />
      <nav aria-label="Макеты">{matches.map(item => <Button key={item.id} multiline variant="text" aria-current={item.id === project.id ? 'page' : undefined} className="mockup-index-link" onClick={() => choose(item)}>{item.title}<small>{item.pages.length} стр.</small></Button>)}</nav>
    </aside>
    <section className="mockup-detail"><div><h2>{project.title}</h2><p className="catalog-note">{live ? 'Интерактивные варианты для выбора. Сценарий переключается внутри макета.' : project.kind === 'design' ? 'Компоновка и тексты из архива. Кнопки, поля и элементы выбора связаны с текущей библиотекой.' : 'Исторический разбор или снимки: сохранены как свидетельство прежнего состояния.'}</p></div>
      <div className="mockup-toolbar"><Choice aria-label="Страница макета" value={page.path} onChange={path => { setPagePath(path); persist(project.id, path); }} options={project.pages.map(p => ({ value: p.path, label: p.title }))} />
        <Choice aria-label="Ширина макета" value={size} onChange={setSize} options={[{ value: 'auto', label: 'Исходный размер' }, { value: '390', label: 'Телефон · 390' }, { value: '1024', label: '1024' }, { value: '1440', label: '1440' }]} />
        <Choice aria-label="Масштаб просмотра" value={zoom} onChange={setZoom} options={[{ value: 'fit', label: 'Вписать по ширине' }, { value: '1', label: '100 %' }, { value: '0.75', label: '75 %' }, { value: '0.5', label: '50 %' }]} />
        {!live && <Button variant="outlined" onClick={() => setOriginal(!original)}>{original ? 'Показать на живой системе' : 'Сравнить с оригиналом'}</Button>}
        <a className="mockup-open" href={url} target="_blank" rel="noreferrer"><Icon icon={OpenInNew} size={18} />Открыть отдельно</a></div>
      <div className="mockup-viewport" ref={viewport}><div style={{ width: width * scale, height: height * scale }}><iframe key={url} title={project.title + ' — ' + page.title} src={url} style={{ width, height, transform: `scale(${scale})`, transformOrigin: 'top left' }} /></div></div>
      <details><summary>Происхождение и ссылки в документах</summary>{live
        ? <p className="catalog-note"><code>{project.reference}</code></p>
        : <><p className="catalog-note">Источник: E:\Claude\Artifacts_restored\{project.source.folder}. Сохранены все найденные варианты; выбор владельца остаётся в документах.</p><ul>{project.references.map((ref, i) => <li key={i}><code>{ref.file}:{ref.line}</code></li>)}</ul></>}
      </details>
    </section>
  </div>;
}
