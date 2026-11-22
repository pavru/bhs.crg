import { useEffect, useState, type ReactNode } from 'react';
import { Button, IconButton } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { Icon } from '@/shared/ui/Icon';
import { Ctx as ThemeContext } from '@/shared/ui/themeContext';
import * as glyphs from '@/shared/ui/icons';
import type { IconDef } from '@/shared/ui/icons';
import metadata from 'virtual:catalog-metadata';
import { Foundations, IconLibrary } from './Foundations';
import { Actions, Fields, Selection, Feedback, Layers } from './Specimens';
import { DocumentScreen } from './DocumentScreen';
import { Library } from './Library';
import { Coverage } from './Coverage';
import { Mockups } from './Mockups';
import { Work12 } from './Work12';
import { Work19 } from './work19/Review';
import { componentNames, catalogStories } from './catalog-data';

const sections = [
  { id: 'overview', label: 'Обзор', icon: glyphs.Widgets, description: 'Как устроена живая библиотека' },
  { id: 'foundations', label: 'Основы', icon: glyphs.Palette, description: 'Цвет, типографика и форма' },
  { id: 'library', label: 'Все компоненты', icon: glyphs.Widgets, description: 'Компоненты, состояния и API из кода' },
  { id: 'actions', label: 'Действия', icon: glyphs.Tune, description: 'Button · IconButton' },
  { id: 'fields', label: 'Поля ввода', icon: glyphs.Edit, description: 'TextField · DateField · Select' },
  { id: 'selection', label: 'Выбор', icon: glyphs.Check, description: 'Checkbox · Switch · RadioGroup · Chip' },
  { id: 'feedback', label: 'Обратная связь', icon: glyphs.Notifications, description: 'Banner · StatusText · EmptyState' },
  { id: 'layers', label: 'Окна и меню', icon: glyphs.OpenInNew, description: 'Modal · Menu · Tooltip' },
  { id: 'icons', label: 'Значки', icon: glyphs.Search, description: 'Векторный каталог из кода' },
  { id: 'screen', label: 'Пример экрана', icon: glyphs.Description, description: 'Один экран в трёх компоновках' },
  { id: 'mockups', label: 'Восстановленные макеты', icon: glyphs.Description, description: 'Макеты и варианты из архива Claude' },
  { id: 'work12', label: 'Назначения · 4 варианта', icon: glyphs.Group, description: 'work-12 · Восстановленный макет · Не первичный' },
  { id: 'work19', label: 'Закрытие периода · Р4', icon: glyphs.CalendarCheck, description: 'Три варианта диалога work-19' },
  { id: 'coverage', label: 'Покрытие исходников', icon: glyphs.Check, description: 'Полнота библиотеки и служебные контексты' },
];

export function Source({ file, children }: { file: string; children: string }) {
  const [open, setOpen] = useState(false);
  return <div className="source-block">
    <div className="source-toolbar"><code>{file}</code><Button variant="text" onClick={() => setOpen(!open)}>{open ? 'Скрыть код' : 'Показать код'}</Button></div>
    {open && <pre><code>{children}</code></pre>}
  </div>;
}

export function Specimen({ title, note, children }: { title: string; note?: string; children: ReactNode }) {
  return <section className="specimen"><div className="specimen-heading"><h2>{title}</h2>{note && <p>{note}</p>}</div>{children}</section>;
}

function Overview({ navigate }: { navigate: (id: string) => void }) {
  return <>
    <div className="overview-intro">
      <div><h2>Интерфейс, который<br />можно попробовать</h2><p>Один источник для приложения, библиотеки компонентов и будущих макетов. Здесь работает тот же React-код, что и в BHS.CRG.</p><Button variant="filled" onClick={() => navigate('actions')} icon={<Icon icon={glyphs.Tune} size={18} />}>Настроить компонент</Button></div>
      <div className="overview-sample" aria-label="Пример поля и действий"><div className="sample-file"><Icon icon={glyphs.Description} size={40} /><span>Исполнительная документация</span></div><TextField label="Наименование комплекта" defaultValue="Электроснабжение. Корпус 1" /><div className="sample-actions"><Button variant="text">Отмена</Button><Button variant="tonal" icon={<Icon icon={glyphs.Check} size={18} />}>Сохранить комплект</Button></div></div>
    </div>
    <Specimen title="От общего языка — к экрану" note="Элементы можно проверить по отдельности, а затем увидеть вместе.">
      <div className="overview-links">{sections.slice(1).map(s => <Button key={s.id} variant="text" onClick={() => navigate(s.id)} className="overview-link"><Icon icon={s.icon} size={24} /><span><strong>{s.label}</strong><small>{s.description}</small></span><span aria-hidden>↗</span></Button>)}</div>
    </Specimen>
    <Specimen title="Как это связано с кодом">
      <div className="workflow"><div><h3>Текущая система</h3><p>Тема и компоненты импортируются напрямую. Изменение в <code>shared/ui</code> сразу появляется в открытом каталоге.</p></div><div><h3>Варианты макета</h3><p>Новый экран собирается из тех же элементов. В примере можно сравнить список, карточки и список с панелью.</p></div><div><h3>Выбор и реализация</h3><p>Выбранная компоновка становится основой реализации. Новые состояния добавляются в каталог вместе с компонентом.</p></div></div>
      <p className="catalog-note">В библиотеке {componentNames.length} визуальных элементов и {catalogStories.length} состояний и примеров. Полнота сверяется с исходниками в разделе «Покрытие». Сценарии описаны вручную; сами компоненты, стили, значки и API читаются из кода. Данные демонстрационные, запросы из образцов обрабатываются локальными фикстурами.</p>
    </Specimen>
  </>;
}

export function Catalog() {
  const initial = new URLSearchParams(location.search).get('section');
  const [section, setSection] = useState(sections.some(s => s.id === initial) ? initial! : 'overview');
  const [theme, setTheme] = useState<'light' | 'dark'>(new URLSearchParams(location.search).get('theme') === 'dark' ? 'dark' : 'light');
  const [search, setSearch] = useState('');
  const [mobileNav, setMobileNav] = useState(false);
  const current = sections.find(s => s.id === section)!;
  const matches = sections.filter(s => `${s.label} ${s.description}`.toLocaleLowerCase('ru').includes(search.toLocaleLowerCase('ru')));
  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    const url = new URL(location.href);
    url.searchParams.set('theme', theme);
    history.replaceState(null, '', url);
  }, [theme]);
  const navigate = (id: string) => {
    setSection(id); setMobileNav(false);
    history.replaceState(null, '', `?section=${id}&theme=${theme}`);
    window.scrollTo({ top: 0, behavior: 'instant' });
  };
  const content: Record<string, ReactNode> = {
    overview: <Overview navigate={navigate} />, foundations: <Foundations theme={theme} />,
    actions: <Actions />, fields: <Fields />, selection: <Selection />,
    feedback: <Feedback />, layers: <Layers />, icons: <IconLibrary />, screen: <DocumentScreen />, library: <Library />, coverage: <Coverage />, mockups: <Mockups />, work12: <Work12 />, work19: <Work19 />,
  };
  return <ThemeContext.Provider value={{ theme, resolvedTheme: theme, setTheme: t => setTheme(t === 'dark' ? 'dark' : 'light'), saveState: 'idle' }}>
    <a className="skip-link" href="#catalog-content">К содержимому</a>
    <div className="catalog-shell">
      <aside className={`catalog-sidebar ${mobileNav ? 'is-open' : ''}`}>
        <a className="catalog-brand" href="?section=overview" onClick={e => { e.preventDefault(); navigate('overview'); }}><span className="brand-symbol"><Icon icon={glyphs.Description} size={24} /></span><span><strong>BHS.CRG UI</strong><small>Дизайн-система</small></span></a>
        <TextField label="Поиск разделов" density="dense" placeholder="Найти компонент…" value={search} onChange={e => setSearch(e.target.value)} />
        <nav aria-label="Разделы дизайн-системы">{matches.map(s => <Button variant="text" key={s.id} className={`catalog-nav ${s.id === section ? 'is-current' : ''}`} onClick={() => navigate(s.id)} aria-current={s.id === section ? 'page' : undefined} icon={<Icon icon={s.icon as IconDef} size={18} />}>{s.label}</Button>)}{matches.length === 0 && <p className="catalog-note">Разделы не найдены</p>}</nav>
        <div className="sidebar-footer"><span className="live-indicator" />Компоненты из кода<code>{metadata.commit}</code><small>{metadata.sourceFiles} UI-файлов в текущей копии</small></div>
      </aside>
      <div className="catalog-main">
        <header className="catalog-topbar"><div className="topbar-location"><IconButton className="mobile-nav-toggle" icon={glyphs.Widgets} label="Разделы" onClick={() => setMobileNav(!mobileNav)} /><span>Библиотека / {current.label}</span></div><div className="theme-control"><span>{theme === 'light' ? 'Светлая тема' : 'Тёмная тема'}</span><IconButton icon={theme === 'light' ? glyphs.DarkMode : glyphs.LightMode} label={theme === 'light' ? 'Включить тёмную тему' : 'Включить светлую тему'} onClick={() => setTheme(theme === 'light' ? 'dark' : 'light')} /></div></header>
        <main id="catalog-content" className="catalog-content"><div className="page-heading"><h1>{section === 'overview' ? 'Живая дизайн-система' : current.label}</h1><p>{section === 'overview' ? 'Material 3 в BHS.CRG: основы, компоненты и примеры из одного источника.' : current.description}</p></div>{content[section]}</main>
        <footer className="catalog-footer">BHS.CRG UI <span>Живая библиотека текущего кода</span><code>{metadata.commit}</code></footer>
      </div>
    </div>
  </ThemeContext.Provider>;
}
