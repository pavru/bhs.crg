import { useId, useState, type KeyboardEvent } from 'react';
import { useEntryCatalog, type EntryCatalogItem, type EntryCatalogScope } from '@/shared/api/workEntryCatalog';
import { useDebouncedValue } from '@/shared/hooks/useDebouncedValue';
import { apiError } from '@/shared/utils/apiError';
import { Banner } from './Banner';
import { Button } from './Button';
import { fieldBox, useFieldDensity } from './fieldBox';
import { Icon } from './Icon';
import { Search } from './icons';
import { ListOption } from './ListOption';
import { Modal } from './Modal';
import { OutlinedField } from './OutlinedField';
import { SearchInput } from './SearchInput';
import { useKeyScroll } from './useKeyScroll';

type SearchKind = Exclude<keyof EntryCatalogItem, 'sections'>;

interface PickerProps<K extends SearchKind> {
  scope: EntryCatalogScope;
  kind: K;
  label: string;
  title: string;
  display: string;
  disabled?: boolean;
  describedBy?: string;
  field?: boolean;
  currentId?: string;
  name: (item: EntryCatalogItem[K]) => string;
  excluded?: readonly string[];
  onPick: (item: EntryCatalogItem[K]) => void;
}

export function WorkCatalogPicker<K extends SearchKind>(props: PickerProps<K>) {
  const [open, setOpen] = useState(false);
  const [focused, setFocused] = useState(false);
  const density = useFieldDensity(undefined, 'dense');
  const { display, label, disabled, describedBy, onPick, field } = props;
  return <>
    {field ? <OutlinedField label={label} raise="always" focused={focused || open} density={density}>
      <button type="button" aria-label={label} aria-haspopup="dialog" aria-expanded={open} disabled={disabled}
        onClick={() => setOpen(true)} onFocus={() => setFocused(true)} onBlur={() => setFocused(false)}
        className={`flex w-full items-center justify-between gap-1 rounded-xs text-left text-body-medium outline-none disabled-content ${fieldBox(density)}`}>
        <span className="truncate">{display}</span><Icon icon={Search} size={18} className="shrink-0 text-on-surface-variant" />
      </button>
    </OutlinedField> : <Button variant="outlined" aria-label={label} aria-haspopup="dialog" aria-describedby={describedBy}
      fullWidth disabled={disabled} onClick={() => setOpen(true)}>
      <span className="truncate">{display}</span>
    </Button>}
    {open && <CatalogChoices {...props} onClose={() => setOpen(false)} onPick={item => { onPick(item); setOpen(false); }} />}
  </>;
}

function CatalogChoices<K extends SearchKind>({ scope, kind, title, name, excluded, currentId, onPick, onClose }:
  PickerProps<K> & { onClose: () => void }) {
  const [search, setSearch] = useState('');
  const [active, setActive] = useState<string>();
  const id = useId();
  const q = useDebouncedValue(search.trim(), 250);
  const catalog = useEntryCatalog(scope, kind, q);
  const pending = search.trim() !== q || catalog.isPending;
  const items = pending || catalog.isError ? [] : (catalog.data?.items ?? []).filter(item => !excluded?.includes(item.id));
  const activeIndex = items.findIndex(item => item.id === active);
  const activeId = activeIndex < 0 ? undefined : `${id}-${activeIndex}`;
  const byKeys = useKeyScroll(activeId);
  function onKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.nativeEvent.isComposing || !items.length) return;
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      const down = event.key === 'ArrowDown';
      const next = down ? Math.min(activeIndex + 1, items.length - 1)
        : activeIndex < 0 ? items.length - 1 : Math.max(activeIndex - 1, 0);
      if (next !== activeIndex) { byKeys(); setActive(items[next].id); }
    } else if (event.key === 'Enter' && activeIndex >= 0) {
      event.preventDefault();
      onPick(items[activeIndex]);
    }
  }
  return <Modal open title={title} onOpenChange={open => { if (!open) onClose(); }} nothingToLose>
    <div className="space-y-3">
      <SearchInput ariaLabel="Поиск по справочнику" value={search} autoFocus
        onChange={value => { setSearch(value); setActive(undefined); }}
        combobox={{ listId: items.length ? id : undefined, activeId, onKeyDown }} />
      {pending ? <p role="status" className="text-body-medium">Загрузка…</p>
        : catalog.isError ? <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void catalog.refetch() }]}>
          {apiError(catalog.error, 'Не удалось открыть справочник.')}
        </Banner>
        : items.length ? <div role="listbox" id={id} aria-label={title} className="-mx-4">
          {items.map((item, index) => <ListOption key={item.id} id={`${id}-${index}`} headline={name(item)}
            current={item.id === currentId} active={index === activeIndex} onHover={() => setActive(item.id)} onChoose={() => onPick(item)} />)}
        </div> : <p role="status" className="text-body-medium text-on-surface-variant">
          {catalog.data?.reason ?? (catalog.data?.items?.length ? 'Все найденные записи уже выбраны.' : 'Ничего не найдено.')}
        </p>}
      {!pending && catalog.data?.more && <p className="text-body-small text-on-surface-variant">
        Уточните поиск, чтобы увидеть другие записи.
      </p>}
    </div>
  </Modal>;
}
