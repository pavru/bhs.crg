import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router';
import { Icon } from '@/shared/ui/Icon';
import { Add, FolderOpen, LinkIcon, Person, Warning } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { Chip } from '@/shared/ui/Chip';
import { EmptyState } from '@/shared/ui/EmptyState';
import { ListItem } from '@/shared/ui/ListItem';
import { ListDetailShell } from '@/shared/ui/ListDetailShell';
import { Modal } from '@/shared/ui/Modal';
import { SearchInput } from '@/shared/ui/SearchInput';
import { useSnackbar } from '@/shared/ui/Snackbar';
import { useListDetailView } from '@/shared/ui/listDetailView';
import { useRememberedSelection } from '@/shared/hooks/useRememberedSelection';
import { useCan } from '@/shared/api/access';
import { useListDocumentTypes } from '@/shared/api/documentTypes';
import { EMPLOYEE_CODE, ownDoorCode } from '@/shared/api/commonData';
import { resolveEffectiveFields } from '@/shared/api/schema';
import { useCreateEmployeeDirectory, useEmployees, type DirectoryMissing, type Employee } from '@/shared/api/employees';
import type { DocumentType } from '@/shared/api/types';
import { CatalogEntryForm } from '../document-sets/catalog';
import { EmployeeDetail } from './EmployeeDetail';
import { MatchAccountsDialog } from './MatchAccountsDialog';
import { FILTERS, filterCounts, inFilter, matchesQuery, personnelNumber, type EmployeeFilter } from './employeeAccount';

const SELECTION_KEYS = ['employee'] as const;
const LAST_KEY = 'employees-last';

/**
 * «Данные → Сотрудники» (ТЗ WORK-27, WORK-47; change `work-01-employee-account`, макет Р1-В): список и
 * карточка сотрудника, связь с учётной записью и режим «Сопоставить». Реквизиты правятся здесь — и только
 * здесь (решение владельца по C15): «Общие данные» пишут сотрудника этой же дверью.
 *
 * <p>Состояния различаются: «справочник не заведён» — с причиной и выходом, «сотрудников пока нет» —
 * с созданием (B20). Действия правки — только при `core.employees.edit`.</p>
 */
export function EmployeesPage() {
  const { data, isLoading, isError } = useEmployees();
  const { data: types = [] } = useListDocumentTypes();
  const can = useCan();
  const canEdit = can.permission('core.employees.edit');
  const ld = useListDetailView();
  const { values, remember } = useRememberedSelection(LAST_KEY, SELECTION_KEYS);
  const [filter, setFilter] = useState<EmployeeFilter>('all');
  const [q, setQ] = useState('');
  const [matching, setMatching] = useState(false);
  const [adding, setAdding] = useState(false);

  const employees = useMemo(() => [...(data?.items ?? [])]
    .sort((a, b) => a.displayName.localeCompare(b.displayName, 'ru')), [data]);
  const familyTypes = useMemo(() => types.filter(t => ownDoorCode(t.id, types) === EMPLOYEE_CODE), [types]);
  const counts = useMemo(() => filterCounts(employees), [employees]);
  const shown = employees.filter(e => inFilter(e, filter) && matchesQuery(e, q));
  // Выбранный остаётся в карточке, даже если вышел из отбора: отправленный в архив из «Все» иначе молча
  // сменился бы соседом, и плашку «в архиве» с «Вернуть» человек не увидел бы (ревью PR #1908).
  const selected = employees.find(e => e.id === values.employee) ?? shown[0];
  const open = (id: string) => { remember({ employee: id }); ld.showDetail(); };

  const missing = data?.missing ?? null;
  const overlay = isLoading
    ? <Centered>Загрузка…</Centered>
    : missing ? <DirectoryMissingState missing={missing} canEdit={canEdit} canTypes={can.permission('core.types.edit')} />
    : isError ? <Centered>Список сотрудников получить не удалось.</Centered>
    : employees.length === 0 ? (
      <div className="flex-1 p-6">
        <EmptyState icon={<Icon icon={Person} size={24} />} title="Сотрудников пока нет"
          description={canEdit ? 'Заведите первого — табельный номер, ФИО и даты работы.' : 'Обратитесь к администратору.'}
          action={canEdit && <Button variant="filled" icon={<Icon icon={Add} size={18} />} onClick={() => setAdding(true)}>Добавить сотрудника</Button>} />
      </div>
    ) : undefined;

  return (
    <>
      <ListDetailShell view={ld} title="Сотрудники"
        subtitle="Справочник сотрудников компании и их учётные записи для входа"
        icon={<Icon icon={Person} size={20} className="text-on-surface-variant" />}
        actions={canEdit && !missing && employees.length > 0 && (
          <>
            <Button variant="outlined" icon={<Icon icon={Add} size={18} />} onClick={() => setAdding(true)}>Добавить</Button>
            <Button variant="filled" icon={<Icon icon={LinkIcon} size={18} />} onClick={() => setMatching(true)}>
              Сопоставить с учётными записями
            </Button>
          </>
        )}
        overlay={overlay}
        nav={
          <div className="flex-1 min-h-0 flex flex-col">
            <div className="px-4 pt-2 pb-3 space-y-3">
              <SearchInput value={q} onChange={setQ} placeholder="Найти сотрудника" ariaLabel="Найти сотрудника" />
              <div className="flex flex-wrap gap-2" role="group" aria-label="Отбор">
                {FILTERS.map(f => (
                  <Chip key={f.key} selected={filter === f.key} onClick={() => setFilter(f.key)} count={counts[f.key]}>{f.label}</Chip>
                ))}
              </div>
            </div>
            <div className="flex-1 min-h-0 overflow-y-auto pb-3" data-testid="employees-list">
              {shown.map(e => <EmployeeRow key={e.id} employee={e} types={types} active={selected?.id === e.id} onClick={() => open(e.id)} />)}
              {shown.length === 0 && <p className="text-body-medium text-on-surface-variant px-4 py-2">Под отбор никто не подошёл.</p>}
            </div>
          </div>
        }
        detail={selected
          ? <EmployeeDetail key={selected.id} id={selected.id} canEdit={canEdit} types={types} familyTypes={familyTypes} />
          : <Centered>Выберите сотрудника</Centered>} />

      {matching && <MatchAccountsDialog onClose={() => setMatching(false)} />}
      <Modal open={adding} onOpenChange={setAdding} title="Новый сотрудник" wide flushBody>
        {adding && (
          <CatalogEntryForm entry={null} compositeTypes={familyTypes} allDocTypes={types} scope="System" scopeId={null}
            onClose={() => setAdding(false)} />
        )}
      </Modal>
    </>
  );
}

/**
 * Строка списка (B16): вторая строка — табельный номер и состояние связи словами. Знак — тихий и дублирует
 * текст: связан — человек, удалена — треугольник; у «не связан» знака нет — это обычное положение.
 */
function EmployeeRow({ employee, types, active, onClick }: {
  employee: Employee; types: DocumentType[]; active: boolean; onClick: () => void;
}) {
  const type = types.find(t => t.id === employee.compositeTypeId);
  const number = type ? personnelNumber(resolveEffectiveFields(type, types), employee.data) : null;
  const state = employee.account.state === 'linked' ? `Связан: ${employee.account.name ?? ''}`
    : employee.account.state === 'deleted' ? 'Учётная запись удалена' : null;
  const supporting = [number && `Таб. № ${number}`, state, employee.archived && 'в архиве'].filter(Boolean).join(' · ');
  return (
    <ListItem onClick={onClick} selected={active} headline={employee.displayName} supporting={supporting || undefined}
      trailing={employee.account.state === 'linked' ? <Icon icon={Person} size={18} className="text-on-surface-variant" />
        : employee.account.state === 'deleted' ? <Icon icon={Warning} size={18} className="text-warning" /> : undefined} />
  );
}

/**
 * Справочник не заведён (B20) — отдельно от «сотрудников пока нет». Причина — от сервера: без «Персоны»
 * «Завести» не поможет, и выход — «Типы документов»; с ней — «Завести», без перезапуска сервера.
 */
function DirectoryMissingState({ missing, canEdit, canTypes }: { missing: DirectoryMissing; canEdit: boolean; canTypes: boolean }) {
  const create = useCreateEmployeeDirectory();
  const toast = useSnackbar();
  const navigate = useNavigate();
  const action = missing.reason === 'not-created'
    ? canEdit && (
      <Button variant="filled" loading={create.isPending} onClick={async () => {
        try { await create.mutateAsync(); toast.success('Справочник сотрудников заведён.'); }
        catch (e) { toast.apiError(e, 'Справочник не заведён.'); }
      }}>Завести</Button>
    )
    : canTypes && <Button variant="outlined" onClick={() => navigate('/document-types')}>Типы документов</Button>;
  return (
    <div className="flex-1 p-6" data-testid="employees-directory-missing">
      <EmptyState icon={<Icon icon={FolderOpen} size={24} />} title="Справочник сотрудников не заведён"
        description={action ? missing.error : `${missing.error} Обратитесь к администратору.`}
        action={action || undefined} />
    </div>
  );
}

function Centered({ children }: { children: string }) {
  return <div className="flex-1 flex items-center justify-center text-on-surface-variant text-body-medium">{children}</div>;
}
