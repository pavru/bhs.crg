import { useId, useState, type KeyboardEvent } from 'react';
import type { EntryCatalogScope, MaterialOption } from '@/shared/api/workEntryCatalog';
import { useWorkMaterialsHint, type MaterialHintItem, type MaterialHintLine, type MaterialHintTierId } from '@/shared/api/workMaterialsHint';
import { useDebouncedValue } from '@/shared/hooks/useDebouncedValue';
import { apiError } from '@/shared/utils/apiError';
import { Banner } from './Banner';
import { Button } from './Button';
import { Icon } from './Icon';
import { Add } from './icons';
import { LIST_CONTAINER } from './ListItem';
import { ListOption } from './ListOption';
import { SearchInput } from './SearchInput';
import { SideSheet } from './SideSheet';
import { StatusText } from './StatusText';
import { TabPanel, Tabs } from './Tabs';
import { useKeyScroll } from './useKeyScroll';

interface Props {
  scope: EntryCatalogScope;
  line: MaterialHintLine;
  excluded: readonly string[];
  describedBy?: string;
  onPick: (material: MaterialOption) => void;
}

const sources: { value: MaterialHintTierId; label: string; title: string }[] = [
  { value: 'issued', label: 'Стройка', title: 'Отпущено на стройку' },
  { value: 'estimate', label: 'Смета', title: 'Ресурсы позиции сметы' },
  { value: 'catalog', label: 'Справочник', title: 'Справочник' },
];

export function MaterialPicker(props: Props) {
  const [open, setOpen] = useState(false);
  return <>
    <Button variant="tonal" icon={<Icon icon={Add} />} aria-haspopup="dialog" aria-expanded={open}
      aria-describedby={props.describedBy} onClick={() => setOpen(true)}>Добавить материал</Button>
    {open && <MaterialChoices {...props} onClose={() => setOpen(false)} />}
  </>;
}

function MaterialChoices({ scope, line, excluded, onPick, onClose }: Props & { onClose: () => void }) {
  const [tab, setTab] = useState<MaterialHintTierId>('issued');
  const [search, setSearch] = useState('');
  const [active, setActive] = useState<string>();
  const [added, setAdded] = useState(0);
  const id = useId();
  const q = useDebouncedValue(search.trim(), 250);
  const hint = useWorkMaterialsHint(scope, line, q);
  const pending = search.trim() !== q || hint.isPending;
  const title = sources.find(source => source.value === tab)!.title;
  const tier = hint.data?.[tab];
  const items = pending || hint.isError || tier?.state !== 'ok' ? [] : tier.items.filter(item => !excluded.includes(item.id));
  const listId = `${id}-materials`;
  const activeIndex = items.findIndex(item => item.id === active);
  const activeId = activeIndex >= 0 ? `${listId}-${active}` : undefined;
  const byKeys = useKeyScroll(activeId);
  const pick = (item: MaterialHintItem) => {
    onPick({ id: item.id, displayName: item.displayName, archived: false });
    setAdded(count => count + 1);
    setActive(undefined);
  };
  function onKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.nativeEvent.isComposing || !items.length) return;
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      const next = event.key === 'ArrowDown' ? Math.min(activeIndex + 1, items.length - 1)
        : activeIndex < 0 ? items.length - 1 : Math.max(activeIndex - 1, 0);
      if (next !== activeIndex) { byKeys(); setActive(items[next].id); }
    } else if (event.key === 'Enter' && activeIndex >= 0) {
      event.preventDefault();
      pick(items[activeIndex]);
    }
  }

  return <SideSheet open title="Добавить материал" variant="modal" onClose={onClose}>
    <div className="space-y-3">
      <SearchInput ariaLabel="Поиск материала" value={search} autoFocus
        onChange={value => { setSearch(value); setActive(undefined); }}
        combobox={{ listId: items.length ? listId : undefined, activeId, onKeyDown }} />
      <Tabs id={id} aria-label="Источник материалов" items={sources} value={tab}
        onChange={next => { setTab(next); setActive(undefined); }} />
      <TabPanel id={id} value={tab}>
        <h3 className="px-3 pt-2 text-title-small">{title}</h3>
        {pending ? <p role="status" className="px-3 text-body-medium">Загрузка…</p>
          : hint.isError ? <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void hint.refetch() }]}>
            {apiError(hint.error, 'Не удалось загрузить подсказку материалов.')}
          </Banner> : tier?.state !== 'ok' ? <p role="status" className="break-words px-3 text-body-medium text-on-surface-variant">{tier?.reason}</p>
            : <>
              {items.length > 0 ? <div id={listId} role="listbox" aria-label={title} className={LIST_CONTAINER}>
                {items.map(item => <ListOption key={item.id} id={`${listId}-${item.id}`} active={item.id === active}
                  headline={item.displayName || 'Материал без названия'} trailing={item.unit}
                  supporting={item.notIssued || item.matchedAlias ? <>
                    {item.notIssued && <StatusText tone="warning">Не отпущен</StatusText>}
                    {item.matchedAlias && <span>Найдено по имени «{item.matchedAlias}»</span>}
                  </> : undefined}
                  onChoose={() => pick(item)} onHover={() => setActive(item.id)} />)}
              </div> : <p role="status" className="px-3 text-body-medium text-on-surface-variant">
                {tier.items.length ? 'Все найденные материалы уже добавлены.' : q ? 'Совпадений нет.' : 'Нет позиций для выбора.'}
              </p>}
              {tier.inArchive ? <p className="px-3 text-body-small text-on-surface-variant">Совпадений в архиве: {tier.inArchive}</p> : null}
              {tier.more && <p className="px-3 text-body-small text-on-surface-variant">Уточните поиск, чтобы найти другие позиции.</p>}
            </>}
      </TabPanel>
      {added > 0 && <p role="status" className="text-body-small text-on-surface-variant">Добавлено: {added}</p>}
      <Button variant="filled" onClick={onClose}>Готово</Button>
    </div>
  </SideSheet>;
}
