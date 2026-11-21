import { useEffect, useRef, useState } from 'react';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { Select, SelectItem } from '@/shared/ui/Select';
import { Checkbox } from '@/shared/ui/Checkbox';
import { useTheme } from '@/shared/ui/themeContext';
import { StatusText } from '@/shared/ui/StatusText';
import metadata from 'virtual:catalog-metadata';
import { catalogStories, componentNames } from './catalog-data';
import { categories, categoryFor, parseAxes, type StoryProps } from './story-model';
import { Source, Specimen } from './Catalog';

export function Library() {
  const query = new URLSearchParams(location.search);
  const [name, setName] = useState(componentNames.includes(query.get('component') ?? '') ? query.get('component')! : 'Button');
  const [search, setSearch] = useState('');
  const [category, setCategory] = useState('all');
  const [selected, setSelected] = useState(query.get('case') ?? '');
  const [overrides, setOverrides] = useState<StoryProps>({});
  const [viewport, setViewport] = useState('auto');
  const [revision, setRevision] = useState(0);
  const [error, setError] = useState<string>();
  const [readyFor, setReadyFor] = useState<string>();
  const frame = useRef<HTMLIFrameElement>(null);
  const { resolvedTheme } = useTheme();
  const candidates = catalogStories.filter(s => s.component === name);
  const story = candidates.find(s => s.id === selected) ?? candidates[0];
  const api = metadata.inventory.flatMap(file => file.exports).find(entry => entry.name === name);
  const filtered = componentNames.filter(n => `${n} ${categories[categoryFor(n)]}`.toLowerCase().includes(search.toLowerCase()) && (category === 'all' || categoryFor(n) === category));
  const axes = [...new Set(candidates.flatMap(s => Object.keys(parseAxes(s.variant))))];
  const currentAxes = parseAxes(story.variant);
  const choose = (component: string) => { setName(component); setSelected(''); setOverrides({}); setReadyFor(undefined); setError(undefined); };
  const chooseCase = (id: string) => { setSelected(id); setOverrides({}); setReadyFor(undefined); setError(undefined); };
  const queryString = new URLSearchParams({ story: story.id, theme: resolvedTheme, props: JSON.stringify(overrides) });
  const source = `/story.html?${queryString}`;
  const renderKey = `${source}-${revision}`;
  const ready = readyFor === renderKey;
  useEffect(() => {
    const url = new URL(location.href);
    url.searchParams.set('component', name); url.searchParams.set('case', story.id);
    history.replaceState(null, '', url);
  }, [name, story.id]);
  useEffect(() => {
    const receive = (event: MessageEvent) => {
      if (event.origin !== location.origin || event.source !== frame.current?.contentWindow || event.data?.type !== 'bhs-catalog-story' || event.data.id !== story.id) return;
      setReadyFor(renderKey); setError(event.data.error);
    };
    window.addEventListener('message', receive);
    return () => window.removeEventListener('message', receive);
  }, [story.id, renderKey]);
  const nativeControls = ['disabled', 'readOnly', 'required'].filter(name => api?.propertyNames.includes(name) && !api.properties.some(p => p.name === name)).map(name => ({ name, type: 'boolean | undefined', description: 'Нативное свойство контрола', required: false, options: undefined }));
  const scalarProps = [...api?.properties ?? [], ...nativeControls].filter(p => !p.name.startsWith('on') && p.name !== 'className' && p.name !== 'children' && (p.name in story.props || /^boolean/.test(p.type) || ['disabled', 'loading', 'readOnly', 'required', 'danger', 'fromScan'].includes(p.name)))
    .filter(p => ['string', 'number', 'boolean'].includes(typeof (overrides[p.name] ?? story.props[p.name])) || /^(boolean|string|number)( \| undefined)?$/.test(p.type));
  const codeProps = Object.entries({ ...story.props, ...overrides }).filter(([, value]) => ['string', 'boolean', 'number'].includes(typeof value)).map(([key, value]) => typeof value === 'string' ? `  ${key}=${JSON.stringify(value)}` : `  ${key}={${value}}`);
  const group = categoryFor(name);
  const previewHeight = group === 'files' || name === 'AppShell' ? 640 : group === 'layers' ? 540 : name === 'Calendar' || name === 'SectionRail' ? 520 : group === 'data' || story.state === 'Open' ? 400 : 240;
  return <div className="library-layout">
    <aside className="component-index"><TextField label="Поиск компонента" placeholder="DateField, Menu, Pdf…" density="dense" value={search} onChange={e => setSearch(e.target.value)} /><Select label="Группа компонентов" density="dense" value={category} onValueChange={setCategory}><SelectItem value="all">Все группы</SelectItem>{Object.entries(categories).map(([key, label]) => <SelectItem key={key} value={key}>{label}</SelectItem>)}</Select><p className="index-count">{filtered.length} из {componentNames.length} компонентов</p><nav aria-label="Компоненты">{filtered.map(component => <Button key={component} variant="text" className={`component-nav ${name === component ? 'is-current' : ''}`} onClick={() => choose(component)} aria-current={name === component ? 'page' : undefined}>{component}<span>{catalogStories.filter(s => s.component === component).length}</span></Button>)}</nav></aside>
    <div className="component-detail">
      <div className="component-heading"><div><h2>{name}</h2><p>{categories[categoryFor(name)]} · {candidates.length} вариантов и состояний</p></div><Button variant="outlined" onClick={() => { setOverrides({}); setRevision(revision + 1); setReadyFor(undefined); }}>Сбросить пример</Button></div>
      {api?.description && <details className="component-description"><summary>Описание компонента из исходника</summary><p>{api.description.replace(/<[^>]+>/g, '')}</p></details>}
      {story.renderAs && <p className="catalog-note">Показан в контексте {story.renderAs.replace(/^Demo/, '')}: вспомогательная часть использует реальный родительский компонент.</p>}
      <div className="variant-axes">{axes.map(axis => <div className="variant-axis" key={axis}><span>{({ Density: 'Плотность', State: 'Состояние', Precision: 'Точность', Flag: 'Свойство', Value: 'Значение', Variant: 'Вариант', Size: 'Размер', Tone: 'Тон', Context: 'Контекст', Rows: 'Число строк', Side: 'Сторона', Align: 'Выравнивание', Orientation: 'Ориентация' } as Record<string, string>)[axis] ?? axis}</span><Select label={axis} density="dense" value={currentAxes[axis] ?? '—'} onValueChange={value => {
        const options = candidates.filter(s => (parseAxes(s.variant)[axis] ?? '—') === value);
        options.sort((a, b) => axes.filter(key => key !== axis && parseAxes(b.variant)[key] === currentAxes[key]).length - axes.filter(key => key !== axis && parseAxes(a.variant)[key] === currentAxes[key]).length);
        if (options[0]) chooseCase(options[0].id);
      }}>{[...new Set(candidates.map(s => parseAxes(s.variant)[axis] ?? '—'))].map(value => <SelectItem key={value} value={value}>{value}</SelectItem>)}</Select></div>)}<div className="variant-axis"><span>Область просмотра</span><Select label="Область просмотра" density="dense" value={viewport} onValueChange={setViewport}><SelectItem value="auto">По ширине области</SelectItem><SelectItem value="390">Телефон · 390</SelectItem><SelectItem value="1024">Рабочий стол · 1024</SelectItem><SelectItem value="1440">Широкий · 1440</SelectItem></Select></div></div>
      <div className="story-preview-scroll"><iframe ref={frame} key={renderKey} title={`Живой компонент ${name}: ${story.variant}`} src={source} className="story-preview" style={{ height: previewHeight, width: viewport === 'auto' ? story.state === 'Phone' || ['BottomNav', 'PrimaryFab'].includes(name) ? 390 : '100%' : Number(viewport) }} /></div>
      <div className="story-status">{error ? <StatusText tone="error">{error}</StatusText> : <StatusText tone={ready ? 'success' : 'neutral'}>{ready ? 'Реальный компонент загружен' : 'Загрузка компонента…'}</StatusText>}<a href={source} target="_blank" rel="noreferrer">Открыть образец отдельно</a></div>
      {scalarProps.length > 0 && <details className="api-controls"><summary>Настроить свойства</summary><div className="property-controls">{scalarProps.map(property => {
        const value = overrides[property.name] ?? story.props[property.name];
        const change = (next: unknown) => { setOverrides({ ...overrides, [property.name]: next }); setReadyFor(undefined); };
        return typeof value === 'boolean' || /^boolean/.test(property.type) ? <Checkbox key={property.name} checked={value === true} onChange={change} label={property.name} /> : <div className="variant-axis" key={property.name}><span>{property.name}</span>{property.options?.length ? <Select label={property.name} density="dense" value={String(value ?? property.options[0])} onValueChange={v => change(typeof property.options![0] === 'number' ? Number(v) : v)}>{property.options.map(option => <SelectItem key={option} value={String(option)}>{String(option)}</SelectItem>)}</Select> : <TextField label={property.name} density="dense" value={String(value ?? '')} onChange={e => change(typeof value === 'number' ? Number(e.target.value) : e.target.value)} />}</div>;
      })}</div></details>}
      <details className="state-list"><summary>Все состояния ({candidates.length})</summary><div>{candidates.map(s => <Button variant="text" key={s.id} onClick={() => chooseCase(s.id)} aria-pressed={s.id === story.id}>{s.variant}</Button>)}</div></details>
      <Source file={api?.file ?? 'src/shared/ui'}>{`<${name}\n${codeProps.join('\n')}\n${story.children ? '>\n  {/* Содержимое и вложенные компоненты примера */}\n</' + name + '>' : '/>'}`}</Source>
      {api && <Specimen title="API из TypeScript" note="Названия, типы и описания читаются из текущих исходников при запуске и сборке каталога."><div className="api-table-scroll"><table className="api-table"><thead><tr><th>Свойство</th><th>Тип</th><th>Назначение</th></tr></thead><tbody>{api.properties.map(property => <tr key={property.name}><th scope="row"><code>{property.name}{property.required ? ' *' : ''}</code></th><td><code>{property.type}</code></td><td>{property.description || '—'}</td></tr>)}</tbody></table></div><p className="catalog-note">Источник: <a href={`https://github.com/pavru/bhs.crg/blob/${metadata.commit}/${'src/client/' + api.file}#L${api.line}`} target="_blank" rel="noreferrer">{api.file}:{api.line}</a></p></Specimen>}
    </div>
  </div>;
}
