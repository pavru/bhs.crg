import { createRoot } from 'react-dom/client';
import { useState } from 'react';
import { Button, IconButton } from '@/shared/ui/Button';
import { Select, SelectItem } from '@/shared/ui/Select';
import { Icon } from '@/shared/ui/Icon';
import { Description, Group, ChevronLeft, DarkMode, LightMode } from '@/shared/ui/icons';
import { useTheme } from '@/shared/ui/themeContext';
import { AssignmentsPanel } from '@/features/work/assignments/AssignmentsPanel';
import { setFormatLocale } from '@/shared/format/format';
import { StoryProviders } from '../story-providers';
import { Variants } from './Variants';
import { constructionId, installWork12Api, scenarios, SITE } from './data';
import metadata from './metadata.json';
import '@fontsource/roboto/400.css';
import '@fontsource/roboto/500.css';
import '../catalog.css';
import './work12.css';

const variants = [
  { id: 'a', name: 'А · Состав бригады', note: 'Группы людей и карточки. Назначение — в общем окне.', advantage: 'Когда важнее быстро увидеть людей и их роли.', cost: 'При большой бригаде потребуется больше прокрутки.' },
  { id: 'b', name: 'Б · Рабочее место', note: 'Список слева, подробности справа. Назначение — прямо на странице.', advantage: 'Для последовательной проверки и правки назначений.', cost: 'Постоянная панель оставляет меньше места списку.' },
  { id: 'c', name: 'В · Периоды в центре', note: 'Лента — основной вид. Сначала период, затем сотрудники.', advantage: 'Когда важнее сравнивать периоды и готовить следующий состав.', cost: 'Меньше строк помещается на узком экране; назначение в два шага.' },
  { id: 'code', name: '4 · Из текущего кода', note: 'Настоящая панель AssignmentsPanel, её таблица, лента и окна.', advantage: 'Контрольный образец реализованного А+.', cost: 'Оболочка стройки показана в сокращённом виде для сравнения.' },
];
const params = new URLSearchParams(location.search);
const variant = variants.find(v => v.id === params.get('variant')) ?? variants[0];
const scenario = scenarios.find(s => s.value === params.get('scenario'))?.value ?? 'normal';
installWork12Api(scenario);
setFormatLocale('ru-RU');

export function App() {
  const { resolvedTheme, setTheme } = useTheme();
  const [info, setInfo] = useState(true);
  const navigate = (key: string, value: string) => { const url = new URL(location.href); url.searchParams.set(key, value); location.assign(url); };
  return <div className="w12-root">
    <header className="w12-review"><div className="w12-review-title"><span className="w12-eyebrow">work-12 · {metadata.statusLabel}</span><strong>{metadata.title}</strong></div>
      <nav aria-label="Варианты макетов">{variants.map(v => <Button key={v.id} variant={variant.id === v.id ? 'tonal' : 'text'} aria-current={v.id === variant.id ? 'page' : undefined} onClick={() => navigate('variant', v.id)}>{v.name}</Button>)}</nav>
      <div className="w12-review-tools"><Select label="Сценарий" density="dense" value={scenario} onValueChange={v => navigate('scenario', v)}>{scenarios.map(s => <SelectItem key={s.value} value={s.value}>{s.label}</SelectItem>)}</Select><IconButton icon={resolvedTheme === 'dark' ? LightMode : DarkMode} label="Переключить тему" onClick={() => { const theme = resolvedTheme === 'dark' ? 'light' : 'dark'; setTheme(theme); const u = new URL(location.href); u.searchParams.set('theme', theme); history.replaceState(null, '', u); }} /></div>
    </header>
    <div className="w12-concept"><div><p className="w12-eyebrow">{variant.id === 'code' ? 'Восстановлен из кода · Реализация А+' : 'Новая концепция по требованиям закрытого work-12 · 22.11.2026'}</p><h2>{variant.name}</h2><p>{variant.note}</p>{info && <div className="w12-tradeoffs"><span>{variant.advantage}</span><span>{variant.cost}</span></div>}</div><Button variant="text" onClick={() => setInfo(!info)}>{info ? 'Скрыть пояснения' : 'О концепции'}</Button></div>
    <div className="w12-app"><aside className="w12-rail"><div className="w12-logo"><Icon icon={Description} size={24} /><strong>BHS.CRG</strong></div><div className="w12-back"><Icon icon={ChevronLeft} size={18} /> Стройки</div><p className="w12-eyebrow">Эта стройка</p><div className="w12-nav-label">Общие сведения</div><div className="w12-nav-label">Документы</div><div className="w12-nav-label is-active"><Icon icon={Group} size={18} />Назначения</div><p className="w12-rail-footer">Рабочее пространство<br />Учёт работ</p></aside>
      <div className="w12-app-main"><div className="w12-site"><div><p className="w12-eyebrow">Стройка / Электромонтажные работы</p><strong>{SITE}</strong></div><span className="w12-note">Менеджер проекта</span></div><main className="w12-canvas" key={variant.id}>{variant.id === 'code' ? <AssignmentsPanel constructionId={constructionId} /> : <Variants variant={variant.id} />}</main></div>
    </div>
    <footer className="w12-footnote">{metadata.statusLabel} · Сохранён для дальнейшей работы 22.11.2026<br />Данные вымышлены · Изменения живут до перезагрузки · Исторический выбор А+ не изменён</footer>
  </div>;
}

createRoot(document.getElementById('root')!).render(<StoryProviders theme={params.get('theme') === 'dark' ? 'dark' : 'light'} component="Work12"><App /></StoryProviders>);
