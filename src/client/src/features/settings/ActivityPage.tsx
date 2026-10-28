import { formatDateTime, formatTime } from '@/shared/format/format';
import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { ArrowForward, ChevronLeft, ChevronRight, HistoryIcon, KeyboardArrowDown } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { Chip } from '@/shared/ui/Chip';
import { Select, SelectItem } from '@/shared/ui/Select';
import {
  isSeries, seriesLabel, useActivity, useActivityActions, useActivitySeries,
  type ActivityRecord, type ActivityRow,
} from '@/shared/api/activity';
import { PageHeader } from '@/shared/ui/PageHeader';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';

/**
 * Журнал действий (issue #950, ТЗ CORE-28).
 *
 * Экран отвечает на вопрос, который задают задним числом: кто выдал права, когда у типа пропало
 * поле, что было до. Поэтому колонка «было» стоит рядом со «стало», а не прячется в карточке:
 * прежнее значение и есть то, ради чего в журнал заглядывают.
 *
 * Правки здесь нет никакой — ни кнопки, ни поля. Записи только дописываются, и сервер отвергает
 * попытку изменить их; экран об этом говорит тем, что предлагать нечего.
 *
 * Подряд идущие правки одного объекта одним человеком приходят одним рядом (ТЗ CORE-28.2): «Счёт
 * изменён ×9 · 14:02–14:19», «было» и «стало» уже слиты сервером. Ряд раскрывается в записи — каждую
 * со своим временем и своим «было → стало»: свёртка показывает их вместе, а не прячет.
 */

const PAGE = 50;
const ALL = 'all';

const COLUMNS: DataGridColumn<ActivityRow>[] = [
  { key: 'occurredAt', label: 'Когда', width: 'w-36',
    cell: r => <span className="text-fg3 tabular-nums">{formatDateTime(r.occurredAt)}</span> },
  { key: 'actorName', label: 'Кто', width: 'w-52', wrap: true,
    cell: r => <span className="text-on-surface-variant break-all">{r.actorName}</span> },
  { key: 'actionTitle', label: 'Что', wrap: true, cell: r => <What row={r} /> },
  { key: 'change', label: 'Было → стало', wrap: true, cell: r => <Change record={r} /> },
];

/**
 * Что сделано и над чем. У серии — подпись «×N · с–по», раскрывающая её записи прямо под собой: кто и
 * что у них общие, отличаются время и «было → стало».
 */
function What({ row }: { row: ActivityRow }) {
  const [open, setOpen] = useState(false);
  const label = seriesLabel(row);
  return (
    <>
      <div className="flex items-center gap-2 flex-wrap">
        <span>{row.actionTitle}</span>
        {label && (
          <Chip icon={open ? KeyboardArrowDown : ChevronRight} aria-expanded={open}
            aria-label={`${label}: ${open ? 'свернуть' : 'показать'} записи`}
            onClick={() => setOpen(o => !o)}>
            <span className="tabular-nums">{label}</span>
          </Chip>
        )}
      </div>
      {row.target && <div className="text-fg3 text-xs break-all">{row.target}</div>}
      {/* Имя сменилось за серию: «Счёт № 41» был «Счётом № 14» — иначе первые записи ряда
          читались бы как правка другого документа. */}
      {row.firstTarget && <div className="text-fg4 text-xs break-all">была: {row.firstTarget}</div>}
      {open && isSeries(row) && <SeriesRecords sessionId={row.sessionId} action={row.action} count={row.count} />}
    </>
  );
}

function SeriesRecords({ sessionId, action, count }: { sessionId: string; action: string; count: number }) {
  const { data, isLoading, isError } = useActivitySeries(sessionId, action, count);
  if (isLoading) return <div className="text-fg4 text-xs mt-1.5">Загрузка...</div>;
  if (isError || !data)
    return <div className="text-error text-xs mt-1.5">Записи ряда не загрузились — обновите страницу.</div>;
  return (
    <ol className="mt-1.5 border-l border-outline-variant pl-3 space-y-1" aria-label="Записи ряда">
      {data.map(record => (
        <li key={record.id} className="flex items-baseline gap-2 text-xs">
          <span className="text-fg3 tabular-nums shrink-0">{formatTime(record.occurredAt)}</span>
          <Change record={record} />
        </li>
      ))}
      {/* Сервер отдаёт не больше потолка записей ряда — молча обрезанный список выглядел бы целым.
          Записи читаются под то же число, что у ряда (ключ запроса), поэтому «меньше» — это обрезка. */}
      {data.length < count && (
        <li className="text-fg3 text-xs">Показаны первые {data.length} из {count}.</li>
      )}
    </ol>
  );
}

function Change({ record }: { record: ActivityRecord }) {
  // Стрелка рисуется только когда есть и то и другое: «— → Администратор» читалось бы как потеря
  // прежнего значения, хотя его не было вовсе (заведение пользователя, начало отсчёта модулей).
  if (record.before && record.after) {
    return (
      <span className="inline-flex items-center gap-1.5 flex-wrap">
        <span className="text-fg3 line-through decoration-outline/50">{record.before}</span>
        <Icon icon={ArrowForward} size={18} className="text-fg4 shrink-0" />
        <span className="text-on-surface">{record.after}</span>
      </span>
    );
  }
  if (record.after) return <span className="text-on-surface">{record.after}</span>;
  if (record.before) return <span className="text-fg3 line-through decoration-outline/50">{record.before}</span>;
  return <span className="text-fg4">—</span>;
}

export function ActivityPage() {
  // «Все» — отдельное значение, а не пустая строка: Radix запрещает пустое value у пункта списка,
  // и пункт с ним просто не выбирается — отбор выглядел бы сломанным.
  const [action, setAction] = useState<string>(ALL);
  const [page, setPage] = useState(0);
  const { data, isLoading } = useActivity(page * PAGE, PAGE, action === ALL ? null : action);
  const { data: actions = [] } = useActivityActions();

  const records = data?.items ?? [];
  const total = data?.total ?? 0;

  return (
    <div className="px-6 py-4 max-w-5xl">
      <PageHeader icon={<Icon icon={HistoryIcon} size={18} aria-hidden />} title="Журнал действий" className="mb-4" actions={
        <div className="w-64">
          {/* aria-label обязателен: видимой подписи у отбора нет — её заменяет заголовок рядом,
              а тот читалке отбор не называет, и объявлен он был бы просто «список». */}
          <Select value={action} onValueChange={v => { setAction(v); setPage(0); }}
            aria-label="Отбор по действию">
            <SelectItem value={ALL}>Все действия</SelectItem>
            {actions.map(a => <SelectItem key={a.code} value={a.code}>{a.title}</SelectItem>)}
          </Select>
        </div>
      } />

      {isLoading ? (
        <div className="text-center text-fg4 text-sm py-10">Загрузка...</div>
      ) : records.length === 0 ? (
        <div className="text-center text-fg4 text-sm py-10">
          Записей нет. Журнал заполняется сам: сменой ролей, правкой схем и составом модулей.
        </div>
      ) : (
        <>
          <DataGrid columns={COLUMNS} rows={records} rowKey={r => r.id} />

          {/* Страницами, а не кнопкой «показать ещё»: журнал копится годами, и растущий запрос
              рано или поздно упёрся бы в потолок страницы на сервере — список перестал бы
              удлиняться молча, и выглядело бы это как «записей больше нет». */}
          <div className="flex items-center justify-between mt-3 text-note text-fg3">
            <span>
              Строки {page * PAGE + 1}–{page * PAGE + records.length} из {total}
            </span>
            <div className="flex items-center gap-2">
              <Button variant="tonal" disabled={page === 0} icon={<Icon icon={ChevronLeft} size={18} />}
                onClick={() => setPage(p => Math.max(0, p - 1))}>Новее</Button>
              <Button variant="tonal" disabled={(page + 1) * PAGE >= total} icon={<Icon icon={ChevronRight} size={18} />}
                onClick={() => setPage(p => p + 1)}>Раньше</Button>
            </div>
          </div>
        </>
      )}
    </div>
  );
}
