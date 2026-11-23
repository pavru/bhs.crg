import { Select, SelectItem } from './Select';

export function SitePicker({ sites, boundary, value, onChange, disabled }: {
  sites: ReadonlyArray<{ id: string; name: string }>;
  boundary?: string;
  value: string;
  onChange: (id: string) => void;
  disabled?: boolean;
}) {
  return <div className="min-w-0 space-y-1">
    <Select label="Стройка" required placeholder="Выберите стройку" value={value} onValueChange={onChange} disabled={disabled}>
      {sites.map(site => <SelectItem key={site.id} value={site.id}>{site.name}</SelectItem>)}
    </Select>
    {boundary && <p className="text-body-small text-on-surface-variant">{boundary}</p>}
  </div>;
}
