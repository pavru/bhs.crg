import { useState } from 'react';
import { useEntryCatalog, type EntryCatalogItem, type EntryCatalogScope } from '@/shared/api/workEntryCatalog';
import { useDebouncedValue } from '@/shared/hooks/useDebouncedValue';
import { apiError } from '@/shared/utils/apiError';
import { Banner } from './Banner';
import { Button } from './Button';
import { fieldBox, useFieldDensity } from './fieldBox';
import { Icon } from './Icon';
import { Search } from './icons';
import { ListItem } from './ListItem';
import { Modal } from './Modal';
import { OutlinedField } from './OutlinedField';
import { SearchInput } from './SearchInput';

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

function CatalogChoices<K extends SearchKind>({ scope, kind, title, name, excluded, onPick, onClose }:
  PickerProps<K> & { onClose: () => void }) {
  const [search, setSearch] = useState('');
  const q = useDebouncedValue(search.trim(), 250);
  const catalog = useEntryCatalog(scope, kind, q);
  const items = catalog.data?.items?.filter(item => !excluded?.includes(item.id)) ?? [];
  const pending = search.trim() !== q || catalog.isPending;
  return <Modal open title={title} onOpenChange={open => { if (!open) onClose(); }} nothingToLose>
    <div className="space-y-3">
      <SearchInput ariaLabel="Поиск по справочнику" value={search} onChange={setSearch} />
      {pending ? <p role="status" className="text-body-medium">Загрузка…</p>
        : catalog.isError ? <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void catalog.refetch() }]}>
          {apiError(catalog.error, 'Не удалось открыть справочник.')}
        </Banner>
        : items.length ? <div>
          {items.map(item => <ListItem key={item.id} headline={name(item)} onClick={() => onPick(item)} />)}
        </div> : <p role="status" className="text-body-medium text-on-surface-variant">
          {catalog.data?.reason ?? (catalog.data?.items?.length ? 'Все найденные записи уже выбраны.' : 'Ничего не найдено.')}
        </p>}
      {!pending && catalog.data?.more && <p className="text-body-small text-on-surface-variant">
        Уточните поиск, чтобы увидеть другие записи.
      </p>}
    </div>
  </Modal>;
}
