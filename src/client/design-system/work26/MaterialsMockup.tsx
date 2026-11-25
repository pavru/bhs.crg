import { useState } from 'react';
import { Button } from '@/shared/ui/Button';
import { Card } from '@/shared/ui/Card';
import { Chip } from '@/shared/ui/Chip';
import { Icon } from '@/shared/ui/Icon';
import { Add, DarkMode, LightMode } from '@/shared/ui/icons';
import { LIST_CONTAINER, ListItem } from '@/shared/ui/ListItem';
import { Modal } from '@/shared/ui/Modal';
import { SearchInput } from '@/shared/ui/SearchInput';
import { Select, SelectItem } from '@/shared/ui/Select';
import { SideSheet } from '@/shared/ui/SideSheet';
import { StatusText } from '@/shared/ui/StatusText';
import { Tabs, TabPanel } from '@/shared/ui/Tabs';
import { TextField } from '@/shared/ui/TextField';
import { useTheme } from '@/shared/ui/themeContext';
import { scenarios, shortTitles, tiersFor, titles, type Material, type Scenario, type Tier, type TierId } from './materials';

type Variant = 'a' | 'b' | 'c';
const variants = {
  a: 'А · Ярусы в одном окне', b: 'Б · Источники на боковом листе', c: 'В · Подсказка в строке отчёта',
};
const sources = (Object.keys(titles) as TierId[]).map(id => ({ value: id, label: shortTitles[id] }));

function TierList({ tier, onPick, query, compact, onExpand, expanded }: {
  tier: Tier; onPick: (item: Material) => void; query: string;
  compact?: boolean; onExpand?: () => void; expanded?: boolean;
}) {
  const shown = compact && !expanded ? tier.items.slice(0, 1) : tier.items;
  return <section aria-label={titles[tier.id]} className="min-w-0 space-y-1">
    <h3 className="px-3 pt-2 text-title-small">{titles[tier.id]}</h3>
    {tier.state !== 'ok' ? <p className="break-words px-3 pb-2 text-body-medium text-on-surface-variant">{tier.reason}</p>
      : <>
        {shown.length > 0 ? <div className={LIST_CONTAINER}>
          {shown.map(item => <ListItem key={item.id} headline={item.name}
            supporting={item.notIssued ? <StatusText tone="warning">Не отпущен</StatusText> : undefined}
            trailing={item.unit} onClick={() => onPick(item)} />)}
        </div> : <p className="px-3 pb-2 text-body-medium text-on-surface-variant">
          {query ? 'Совпадений нет.' : 'Нет позиций для выбора.'}
        </p>}
        {compact && tier.items.length > 1 && <Button variant="text" onClick={onExpand}
          aria-expanded={expanded}>{expanded ? 'Свернуть' : `Ещё ${tier.items.length - 1}`}</Button>}
        {tier.inArchive ? <p className="px-3 text-body-small text-on-surface-variant">Совпадений в архиве: {tier.inArchive}</p> : null}
        {tier.more && <p className="px-3 pb-2 text-body-small text-on-surface-variant">Уточните поиск, чтобы найти другие позиции.</p>}
      </>}
  </section>;
}

export function MaterialsMockup({ variant }: { variant: Variant }) {
  const { resolvedTheme, setTheme } = useTheme();
  const [scenario, setScenario] = useState<Scenario>('normal');
  const [selected, setSelected] = useState<Material[]>([]);
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [tab, setTab] = useState<TierId>('issued');
  const [expanded, setExpanded] = useState<TierId[]>([]);
  const tiers = tiersFor(scenario, query, selected);
  const pick = (item: Material) => {
    setSelected(current => [...current, item]);
    if (variant === 'a') setOpen(false);
  };
  const search = <SearchInput ariaLabel="Поиск материала" value={query} onChange={setQuery} />;
  const groups = (compact = false) => <div className="divide-y divide-outline-variant">
    {tiers.map(tier => <TierList key={tier.id} tier={tier} query={query} onPick={pick} compact={compact}
      expanded={expanded.includes(tier.id)} onExpand={() => setExpanded(current => current.includes(tier.id)
        ? current.filter(id => id !== tier.id) : [...current, tier.id])} />)}
  </div>;
  const show = () => { setQuery(''); setTab('issued'); setExpanded([]); setOpen(true); };
  return <main className="mx-auto flex max-w-5xl flex-col gap-4 p-4 md:p-8">
    <header className="flex flex-wrap items-center justify-between gap-3">
      <h1 className="text-headline-small">{variants[variant]}</h1>
      <Button variant="outlined" icon={<Icon icon={resolvedTheme === 'light' ? DarkMode : LightMode} />}
        onClick={() => setTheme(resolvedTheme === 'light' ? 'dark' : 'light')}>
        {resolvedTheme === 'light' ? 'Тёмная тема' : 'Светлая тема'}
      </Button>
    </header>
    <div className="max-w-sm">
      <Select label="Сценарий" value={scenario} onValueChange={value => { setScenario(value as Scenario); setSelected([]); setQuery(''); }}>
        {scenarios.map(item => <SelectItem key={item.id} value={item.id}>{item.label}</SelectItem>)}
      </Select>
    </div>
    <Card title="Подробности строки · Прокладка кабеля">
      <div className="space-y-4">
        <div className="grid gap-3 md:grid-cols-2">
          <TextField label="Место работ" defaultValue="Корпус 1, этаж 2" />
          <TextField label="Объём работы" defaultValue="42 м" />
        </div>
        <fieldset className="space-y-3">
          <legend className="mb-1 text-title-small">Материалы</legend>
          {selected.length > 0 && <div className="flex flex-wrap gap-2">
            {selected.map(item => <Chip key={item.id} removeOnly removeLabel={`Убрать ${item.name}`}
              onRemove={() => setSelected(current => current.filter(other => other.id !== item.id))}>{item.name}</Chip>)}
          </div>}
          {variant !== 'c' || !open ? <Button variant="tonal" icon={<Icon icon={Add} />} onClick={show}>Добавить материал</Button>
            : <div className="space-y-2">
              {search}{groups(true)}
              <Button variant="text" onClick={() => setOpen(false)}>Свернуть подсказку</Button>
            </div>}
        </fieldset>
      </div>
    </Card>
    {variant === 'a' && <Modal open={open} onOpenChange={setOpen} title="Добавить материал" nothingToLose>
      <div className="space-y-2">{search}{groups()}</div>
    </Modal>}
    {variant === 'b' && <SideSheet open={open} onClose={() => setOpen(false)} title="Добавить материал" variant="modal">
      <div className="space-y-3">
        {search}
        <Tabs id="work26-source" aria-label="Источник материалов" value={tab} items={sources} onChange={setTab} />
        <TabPanel id="work26-source" value={tab}>
          <TierList tier={tiers.find(tier => tier.id === tab)!} query={query} onPick={pick} />
        </TabPanel>
        {selected.length > 0 && <p role="status" className="text-body-small text-on-surface-variant">Добавлено: {selected.length}</p>}
        <Button variant="filled" onClick={() => setOpen(false)}>Готово</Button>
      </div>
    </SideSheet>}
  </main>;
}
