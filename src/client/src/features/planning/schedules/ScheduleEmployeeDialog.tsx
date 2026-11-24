import { useState } from 'react';
import { useEmployees } from '@/shared/api/employees';
import type { ScheduleAssignee } from '@/shared/api/planningScheduleTypes';
import { EmployeePicker } from '@/shared/ui/EmployeePicker';
import { Banner } from '@/shared/ui/Banner';
import { Checkbox } from '@/shared/ui/Checkbox';
import { Modal, ModalButtons, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { apiError } from '@/shared/utils/apiError';

export function ScheduleEmployeeDialog({ ids, assignees, onClose, onChoice }: {
  ids: string[]; assignees: ScheduleAssignee[]; onClose: () => void;
  onChoice: (ids: string[], assignees: ScheduleAssignee[]) => void;
}) {
  const [selected, setSelected] = useState(() => new Set(ids));
  const [search, setSearch] = useState('');
  const query = useEmployees();
  const employees = query.data?.items ?? [];
  const all = employees.map(employee => ({
    employeeId: employee.id, name: employee.displayName, personnelNumber: null, eligible: !employee.archived,
    reason: employee.archived ? 'Запись в архиве' : null, assigned: null, account: employee.account,
  }));
  const candidates = all.filter(candidate => candidate.name.toLowerCase().includes(search.trim().toLowerCase()));
  const existingUnavailable = ids.filter(id => !all.some(candidate => candidate.employeeId === id && candidate.eligible));
  const missing = query.data?.missing?.error;

  function apply() {
    const next = [...selected];
    const labels = next.flatMap(id => {
      const record = all.find(candidate => candidate.employeeId === id);
      if (record) return [{ id, name: record.name, archived: !record.eligible }];
      return assignees.filter(person => person.id === id);
    });
    onChoice(next, labels);
  }

  return <Modal open wide title="Исполнители" onOpenChange={open => { if (!open) onClose(); }}
    footer={<ModalButtons><ModalExit /><ModalPrimary disabled={query.isPending || query.isError} onClick={apply}>
      Выбрать
    </ModalPrimary></ModalButtons>}>
    <div className="grid min-w-0 grid-cols-1 gap-3">
      {query.isError ? <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void query.refetch() }]}>
        {apiError(query.error, 'Справочник сотрудников не прочитался.')}
      </Banner> : <EmployeePicker candidates={query.isPending ? undefined : candidates} selected={selected}
        onChange={setSelected} search={search} onSearch={setSearch} missing={missing} loading={query.isPending}
        searchPlaceholder="Найти по ФИО" />}
      {query.isSuccess && !!existingUnavailable.length && <div className="grid gap-2">
        <h3 className="text-title-medium">Сохранить ранее назначенных</h3>
        {existingUnavailable.map(id => {
          const person = assignees.find(candidate => candidate.id === id);
          return <Checkbox key={id} checked={selected.has(id)} label={person?.name ?? 'Сотрудник недоступен'}
            onChange={checked => setSelected(current => {
              const next = new Set(current);
              if (checked) next.add(id);
              else next.delete(id);
              return next;
            })} />;
        })}
      </div>}
    </div>
  </Modal>;
}
