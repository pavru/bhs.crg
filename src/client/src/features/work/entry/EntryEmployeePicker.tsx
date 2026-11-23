import { useState } from 'react';
import { useEntryCandidates } from '@/shared/api/workReports';
import { useDebouncedValue } from '@/shared/hooks/useDebouncedValue';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { DateField } from '@/shared/ui/DateField';
import { EmployeePicker } from '@/shared/ui/EmployeePicker';
import { apiError } from '@/shared/utils/apiError';

export function EntryEmployeePicker({ date, onDate, onPick }: {
  date: string;
  onDate: (value: string) => void;
  onPick: (employee: { id: string; name: string }) => void;
}) {
  const [search, setSearch] = useState('');
  const [selected, setSelected] = useState(new Set<string>());
  const [incomplete, setIncomplete] = useState<string | null>(null);
  const q = useDebouncedValue(search.trim(), 250);
  const candidates = useEntryCandidates(q, date || undefined);
  const choice = candidates.data?.items.find(employee => selected.has(employee.employeeId));
  const pending = search.trim() !== q || candidates.isFetching;

  return <section className="max-w-3xl space-y-4" aria-label="Выбор сотрудника">
    <div className="max-w-sm">
      <DateField label="Дата смены" value={date} hint="Пустое поле — сегодня по времени каждой стройки."
        onChange={value => { onDate(value); setSelected(new Set()); }} onIncomplete={setIncomplete} />
    </div>
    {candidates.isError && <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void candidates.refetch() }]}>
      {apiError(candidates.error, 'Не удалось прочитать кандидатов.')}
    </Banner>}
    {!candidates.isError && <EmployeePicker candidates={candidates.data?.items} loading={candidates.isPending}
      search={search} onSearch={value => { setSearch(value); setSelected(new Set()); }} selected={selected} refused={{}}
      more={candidates.data?.more} limit={candidates.data?.limit} pending={pending}
      single onChange={setSelected} />}
    <Button variant="filled" disabled={!choice || pending || !!incomplete}
      onClick={() => { if (choice) onPick({ id: choice.employeeId, name: choice.name }); }}>Продолжить</Button>
  </section>;
}
