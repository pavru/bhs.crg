import { useEffect, useState } from 'react';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { Switch } from '@/shared/ui/Switch';
import { Select, SelectItem } from '@/shared/ui/Select';
import { Icon } from '@/shared/ui/Icon';
import * as glyphs from '@/shared/ui/icons';
import type { FilledIconDef, IconDef } from '@/shared/ui/icons';
import metadata from 'virtual:catalog-metadata';
import { Source, Specimen } from './Catalog';

async function copy(text: string, status: (message: string) => void) {
  try { await navigator.clipboard.writeText(text); status('Скопировано'); }
  catch { status('Выделите и скопируйте строку кода'); }
}

export function Foundations({ theme }: { theme: 'light' | 'dark' }) {
  const [selected, setSelected] = useState('--md-sys-color-primary');
  const [values, setValues] = useState<Record<string, string>>({});
  const [status, setStatus] = useState('');
  const [duration, setDuration] = useState('medium2');
  const [easing, setEasing] = useState('standard');
  const [moved, setMoved] = useState(false);
  const durations = metadata.motion.duration as Record<string, string>;
  const easings = metadata.motion.easing as Record<string, string>;
  useEffect(() => {
    const frame = requestAnimationFrame(() => {
      const css = getComputedStyle(document.documentElement);
      setValues(Object.fromEntries(metadata.roles.map(role => [role, css.getPropertyValue(role).trim()])));
    });
    return () => cancelAnimationFrame(frame);
  }, [theme]);
  const short = (role: string) => role.replace(/^--md-(sys|ext)-color-/, '');
  return <>
    <Specimen title="Цвет — по смыслу" note={`${metadata.roles.length} ролей из md3-tokens.css. Выберите цвет, чтобы получить его переменную.`}>
      <div className="token-inspector"><span className="token-large" style={{ background: `var(${selected})` }} /><div><strong>{short(selected)}</strong><code>var({selected})</code><small>{values[selected]} · {theme === 'light' ? 'светлая тема' : 'тёмная тема'}</small></div><Button variant="outlined" onClick={() => copy(`var(${selected})`, setStatus)}>Копировать</Button><span role="status">{status}</span></div>
      <div className="color-grid">{metadata.roles.map(role => <button key={role} type="button" className={`color-swatch focus-ring ${role === selected ? 'is-selected' : ''}`} onClick={() => { setSelected(role); setStatus(''); }} aria-pressed={role === selected}><span style={{ background: `var(${role})` }} /><strong>{short(role)}</strong><code>{values[role]}</code></button>)}</div>
    </Specimen>
    <Specimen title="Типографика" note="Roboto для интерфейса, Roboto Mono для кода. Все 15 ролей — из текущей темы.">
      <div className="type-list">{metadata.typography.map(([role, value]) => <div className="type-row" key={role}><div><code>{role}</code><small>{value.size} / {value['line-height']} · {value.weight}</small></div><span style={{ fontSize: value.size, lineHeight: value['line-height'], letterSpacing: value.tracking, fontWeight: value.weight }}>Документы стройки</span></div>)}</div>
    </Specimen>
    <Specimen title="Форма и возвышение" note="Форма указывает роль элемента, а тень — его положение над поверхностью.">
      <div className="shape-grid">{metadata.shape.map(([name, value]) => <div key={name}><span style={{ borderRadius: `var(--md-sys-shape-corner-${name})` }} /><code>{name}</code><small>{value === '9999px' ? 'полное скругление' : value}</small></div>)}</div>
      <div className="elevation-grid">{[0, 1, 2, 3, 4, 5].map(level => <div key={level} style={{ boxShadow: `var(--md-sys-elevation-level${level})` }}>Уровень {level}</div>)}</div>
      <Source file="src/theme/md3-theme.json">{'// Меняем источник темы, затем пересобираем CSS\nnpm run md3:theme\n\n// Компоненты используют роли, а не значения цвета\n<Button variant="filled">Сохранить</Button>'}</Source>
    </Specimen>
    <Specimen title="Слои состояния" note="Те же значения прозрачности, которые используют наведение, фокус, нажатие и перетаскивание в компонентах."><div className="state-token-grid">{Object.entries(metadata.state).filter(([, value]) => typeof value === 'number').map(([name, value]) => <div key={name}><span style={{ background: `color-mix(in srgb, var(--md-sys-color-on-surface) ${Number(value) * 100}%, var(--md-sys-color-surface))` }} /><code>{name}</code><small>{Number(value) * 100}%</small></div>)}</div></Specimen>
    <Specimen title="Движение" note="Кривые и длительности читаются из темы. Предпочтение уменьшенного движения учитывается браузером."><div className="variant-axes"><Select label="Длительность" density="dense" value={duration} onValueChange={setDuration}>{Object.entries(durations).map(([key, value]) => <SelectItem key={key} value={key}>{key} · {value}</SelectItem>)}</Select><Select label="Кривая" density="dense" value={easing} onValueChange={setEasing}>{Object.keys(easings).map(key => <SelectItem key={key} value={key}>{key}</SelectItem>)}</Select><Button variant="outlined" onClick={() => setMoved(!moved)}>Показать движение</Button></div><div className="motion-track"><span className={moved ? 'is-moved' : ''} style={{ transitionDuration: durations[duration], transitionTimingFunction: easings[easing] }} /></div><code>{durations[duration]} · {easings[easing]}</code></Specimen>
    <Specimen title="Размеры и плотность" note="Общие правила текущего интерфейса из docs/UI.md. Сами размеры применяют компоненты библиотеки."><div className="api-table-scroll"><table className="api-table"><thead><tr><th>Элемент</th><th>Размер</th><th>Назначение</th></tr></thead><tbody>{[['Button', '40 px', 'Одна высота для всех видов действия'], ['IconButton', '40 / 32 px', 'Обычный размер / место XS внутри поля или клетки'], ['TextField, DateField', '56 / 40 px', 'Форма / плотное поле и клетка'], ['Chip', '32 px', 'Отбор, выбранное значение или вспомогательное действие'], ['DataGrid', '48 / 40 px', 'Минимальная высота обычной / компактной строки'], ['Цель касания', '48 px', 'Дополнительная область нажатия вокруг небольшого элемента']].map(([element, size, use]) => <tr key={element}><th scope="row">{element}</th><td>{size}</td><td>{use}</td></tr>)}</tbody></table></div></Specimen>
  </>;
}

const icons = Object.entries(glyphs).filter((entry): entry is [string, IconDef] => !!entry[1] && typeof entry[1] === 'object' && 'd' in entry[1]);
export function IconLibrary() {
  const [search, setSearch] = useState('');
  const [filled, setFilled] = useState(false);
  const [selected, setSelected] = useState('Description');
  const [status, setStatus] = useState('');
  const visible = icons.filter(([name]) => name.toLowerCase().includes(search.toLowerCase()));
  const code = `<Icon icon={${selected}} size={24}${filled && (glyphs[selected as keyof typeof glyphs] as IconDef).fill ? ' filled' : ''} />`;
  return <Specimen title={`${icons.length} значка из приложения`} note="Материал из исходников: те же контуры и залитые варианты, что использует интерфейс.">
    <div className="icon-tools"><TextField label="Поиск значка" density="dense" placeholder="Description, Search, Add…" value={search} onChange={e => setSearch(e.target.value)} /><label className="switch-label">С заливкой<Switch checked={filled} onChange={setFilled} label="Показать значки с заливкой" /></label></div>
    <div className="icon-code"><code>{code}</code><Button variant="text" onClick={() => copy(code, setStatus)}>Копировать код</Button><span role="status">{status}</span></div>
    <div className="icon-grid">{visible.map(([name, icon]) => <button type="button" key={name} aria-pressed={selected === name} className={`icon-tile focus-ring ${selected === name ? 'is-selected' : ''}`} onClick={() => { setSelected(name); setStatus(''); }}>{filled && icon.fill ? <Icon icon={icon as FilledIconDef} filled size={24} /> : <Icon icon={icon} size={24} />}<span>{name}</span></button>)}</div>
    {visible.length === 0 && <p className="catalog-note">Совпадений нет. Попробуйте другое название.</p>}
  </Specimen>;
}
