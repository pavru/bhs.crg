import type { AssignmentCandidate } from '@/shared/api/work';
import { Checkbox } from './Checkbox';
import { DataGrid, type DataGridColumn } from './DataGrid';
import { Icon } from './Icon';
import { Block, ErrorIcon, LinkOff } from './icons';
import { SearchInput } from './SearchInput';

/**
 * Выбор сотрудников из кандидатов сервера (change `work-12-assignments-ui`, ТЗ WORK-21, WORK-47; макет Р2,
 * вариант А): поиск, таблица с флажками, причина исключения и пометка учётной записи. Годится и для `work-22`.
 *
 * Кого показывать и почему кто-то не годен, решает СЕРВЕР: кандидат с `eligible: false` остаётся в списке
 * неактивным, с причиной словами, — уволенный не «пропадает», и менеджер не ищет его в справочнике. Пометка
 * «нет учётной записи» — по полю `account.state` (`work-29`): годности она не меняет, такого сотрудника назначить
 * можно, но вводить отчёты сам он не сможет. Отказ пачки (422) приходит сюда `refused` — причина у названного
 * сотрудника цветом ошибки, отметки остальных целы.
 *
 * Состояния выдачи различаются словами: справочник не заведён (`missing`) — не «никого не нашлось»; выдача
 * обрезана пределом (`more`) — строка «показаны первые N — сузьте поиском», а не список, похожий на весь штат.
 */
export function EmployeePicker({
  candidates, selected, onChange, search, onSearch, refused = {}, more = false, limit, missing = null, pending = false,
  disabled = false,
}: {
  /** `undefined` — ещё не спрошены (период не задан). */
  candidates: AssignmentCandidate[] | undefined;
  selected: ReadonlySet<string>;
  onChange: (next: Set<string>) => void;
  search: string;
  onSearch: (q: string) => void;
  /** Отказавшие в последней записи: сотрудник → причина. */
  refused?: Readonly<Record<string, string>>;
  more?: boolean;
  limit?: number;
  /** Справочник не заведён — текст сервера. */
  missing?: string | null;
  /** Идёт запрос под новый поиск или период: прежняя выдача на месте, но приглушена. */
  pending?: boolean;
  disabled?: boolean;
}) {
  const eligible = (candidates ?? []).filter(c => c.eligible);
  const picked = eligible.filter(c => selected.has(c.employeeId)).length;

  const toggle = (id: string, on: boolean) => {
    const next = new Set(selected);
    if (on) next.add(id); else next.delete(id);
    onChange(next);
  };
  const toggleAll = (on: boolean) => {
    const next = new Set(selected);
    for (const c of eligible) if (on) next.add(c.employeeId); else next.delete(c.employeeId);
    onChange(next);
  };

  const columns: DataGridColumn<AssignmentCandidate>[] = [
    {
      key: 'pick', label: 'Отметка', width: 'w-14', interactive: true,
      header: (
        <Checkbox aria-label="Отметить всех годных" checked={eligible.length > 0 && picked === eligible.length}
          indeterminate={picked > 0 && picked < eligible.length} disabled={disabled || eligible.length === 0}
          onChange={toggleAll} />
      ),
      cell: c => (
        <Checkbox aria-label={`Отметить: ${c.name}`} checked={c.eligible && selected.has(c.employeeId)}
          disabled={disabled || !c.eligible} onChange={on => toggle(c.employeeId, on)} />
      ),
    },
    {
      key: 'name', label: 'Сотрудник', rowHeader: true, wrap: true,
      cell: c => <span className={c.eligible ? 'text-on-surface' : 'text-on-surface/38'}>{c.name}</span>,
    },
    {
      key: 'personnelNumber', label: 'Таб. №', width: 'w-24',
      cell: c => <span className={c.eligible ? 'text-on-surface-variant' : 'text-on-surface/38'}>{c.personnelNumber ?? '—'}</span>,
    },
    {
      key: 'note', label: 'Пометка или причина', width: 'w-80', wrap: true,
      cell: c => <CandidateNote candidate={c} refused={refused[c.employeeId]} />,
    },
  ];

  return (
    <div className="space-y-3">
      <SearchInput value={search} onChange={onSearch} placeholder="Найти по ФИО или табельному номеру"
        ariaLabel="Найти сотрудника" />
      {missing ? (
        <p role="alert" className="text-body-medium text-error">{missing}</p>
      ) : candidates === undefined ? (
        <p className="text-body-medium text-on-surface-variant">Задайте период — кандидаты подберутся под него.</p>
      ) : (
        <>
          {more && (
            <p className="text-body-small text-on-surface-variant">
              Показаны первые {limit ?? candidates.length} — сузьте поиском по ФИО или табельному номеру.
            </p>
          )}
          <div className={pending ? 'opacity-60' : undefined} aria-busy={pending || undefined}>
            <DataGrid columns={columns} rows={candidates} rowKey={c => c.employeeId} density="compact"
              state={search.trim() ? 'filtered-out' : 'no-data'} />
          </div>
        </>
      )}
    </div>
  );
}

/**
 * Пометка или причина у кандидата: отказ последней записи (цветом ошибки) важнее причины сервера, причина —
 * важнее пометки учётной записи; пометка остаётся видна и у годного.
 */
function CandidateNote({ candidate: c, refused }: { candidate: AssignmentCandidate; refused?: string }) {
  const account = c.account?.state === 'none' ? 'нет учётной записи'
    : c.account?.state === 'deleted' ? 'учётная запись удалена' : null;
  return (
    <span className="flex flex-wrap items-center gap-x-3 gap-y-1">
      {refused && (
        <span className="inline-flex items-center gap-1.5 text-body-small text-error">
          <Icon icon={ErrorIcon} size={18} aria-hidden />{refused}
        </span>
      )}
      {!refused && c.reason && (
        <span className="inline-flex items-center gap-1.5 text-body-small text-on-surface-variant">
          <Icon icon={Block} size={18} aria-hidden />{c.reason}
        </span>
      )}
      {account && (
        <span className="inline-flex h-6 items-center gap-1 rounded-sm border border-outline-variant px-2 text-label-medium text-on-surface-variant">
          <Icon icon={LinkOff} size={18} aria-hidden />{account}
        </span>
      )}
    </span>
  );
}
