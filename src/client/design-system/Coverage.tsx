import { useState } from 'react';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { StatusText } from '@/shared/ui/StatusText';
import metadata from 'virtual:catalog-metadata';
import { catalogStories, componentNames } from './catalog-data';
import { integrations } from './integrations';
import { Specimen } from './Catalog';

const missingVisuals = [...new Set(metadata.inventory.flatMap(f => f.exports).filter(e => e.visual && !componentNames.includes(e.name) && !integrations[e.name]).map(e => e.name))];
declare global {
  interface Window {
    __catalogManifest?: { components: string[]; stories: { id: string; component: string; variant: string; state?: string }[]; missing: string[]; sourceFiles: number };
  }
}
window.__catalogManifest = { components: componentNames, stories: catalogStories.map(({ id, component, variant, state }) => ({ id, component, variant, state })), missing: missingVisuals, sourceFiles: metadata.inventory.length };

export function Coverage() {
  const [search, setSearch] = useState('');
  const [mode, setMode] = useState('all');
  const files = metadata.inventory.filter(file => `${file.file} ${file.exports.map(e => e.name).join(' ')}`.toLowerCase().includes(search.toLowerCase()))
    .filter(file => mode === 'all' || (mode === 'visual' ? file.visual : !file.visual));
  return <>
    <Specimen title="Соответствие текущему коду" note={`${metadata.inventory.length} файлов общих компонентов и фильтров. ${componentNames.length} визуальных элементов, ${catalogStories.length} состояний и примеров.`}>
      <StatusText tone={missingVisuals.length ? 'warning' : 'success'}>{missingVisuals.length ? `Требуют примеров: ${missingVisuals.join(', ')}` : 'Все визуальные экспорты представлены в каталоге или явно описаны как служебные'}</StatusText>
      <p className="catalog-note">Компонент и его внешний вид берутся из приложения. Сценарии, входные данные и допустимые комбинации свойств описаны в примерах. Новый компонент автоматически появляется в этой проверке; для него нужно добавить сценарии.</p>
      <div className="coverage-tools"><TextField density="dense" label="Поиск по исходникам" placeholder="Файл или экспорт…" value={search} onChange={e => setSearch(e.target.value)} /><div className="sample-row">{[['all', 'Все файлы'], ['visual', 'Визуальные'], ['helpers', 'Утилиты']].map(([value, label]) => <Button key={value} variant={mode === value ? 'tonal' : 'text'} onClick={() => setMode(value)}>{label}</Button>)}</div></div>
      <div className="coverage-table"><table className="api-table"><thead><tr><th>Исходник</th><th>Представление в каталоге</th></tr></thead><tbody>{files.map(file => <tr key={file.file}><th scope="row"><code>{file.file}</code></th><td>{file.exports.filter(e => e.visual).length ? file.exports.filter(e => e.visual).map(e => <div key={e.name}>{componentNames.includes(e.name) ? <a href={`?section=library&component=${e.name}`}>{e.name} · {catalogStories.filter(s => s.component === e.name).length} примеров</a> : integrations[e.name] ? <span>{e.name}: {integrations[e.name].purpose}</span> : <StatusText tone="warning">{e.name}: нужен пример</StatusText>}</div>) : <span className="text-on-surface-variant">Типы, данные, хуки или внутренняя логика. Отдельного визуального элемента нет.</span>}</td></tr>)}</tbody></table></div>
    </Specimen>
    <Specimen title="Служебные контексты и поведение"><div className="integration-list">{Object.entries(integrations).map(([name, value]) => <article key={name}><h3>{name}</h3><p>{value.purpose}</p><div>{value.examples.map(component => <a key={component} href={`?section=library&component=${component}`}>{component}</a>)}</div></article>)}</div></Specimen>
  </>;
}
