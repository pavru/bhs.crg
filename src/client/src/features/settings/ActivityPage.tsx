import { useState } from 'react';
import { History, ArrowRight, ChevronLeft, ChevronRight } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { Select, SelectItem } from '@/shared/ui/Select';
import { useActivity, useActivityActions, type ActivityRecord } from '@/shared/api/activity';

/**
 * Журнал действий (issue #950, ТЗ CORE-28).
 *
 * Экран отвечает на вопрос, который задают задним числом: кто выдал права, когда у типа пропало
 * поле, что было до. Поэтому колонка «было» стоит рядом со «стало», а не прячется в карточке:
 * прежнее значение и есть то, ради чего в журнал заглядывают.
 *
 * Правки здесь нет никакой — ни кнопки, ни поля. Записи только дописываются, и сервер отвергает
 * попытку изменить их; экран об этом говорит тем, что предлагать нечего.
 */

const PAGE = 50;
const ALL = 'all';

function when(iso: string): string {
  return new Date(iso).toLocaleString('ru-RU', {
    day: '2-digit', month: '2-digit', year: '2-digit', hour: '2-digit', minute: '2-digit',
  });
}

function Change({ record }: { record: ActivityRecord }) {
  // Стрелка рисуется только когда есть и то и другое: «— → Администратор» читалось бы как потеря
  // прежнего значения, хотя его не было вовсе (заведение пользователя, начало отсчёта модулей).
  if (record.before && record.after) {
    return (
      <span className="inline-flex items-center gap-1.5 flex-wrap">
        <span className="text-fg3 line-through decoration-fg4/50">{record.before}</span>
        <ArrowRight size={13} className="text-fg4 shrink-0" />
        <span className="text-fg1">{record.after}</span>
      </span>
    );
  }
  if (record.after) return <span className="text-fg1">{record.after}</span>;
  if (record.before) return <span className="text-fg3 line-through decoration-fg4/50">{record.before}</span>;
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
      <div className="flex items-center justify-between mb-4 gap-4">
        <h1 className="text-xl font-semibold text-fg1 flex items-center gap-2">
          <History size={18} className="text-fg3" />
          Журнал действий
        </h1>
        <div className="w-64">
          {/* aria-label обязателен: видимой подписи у отбора нет — её заменяет заголовок рядом,
              а тот читалке отбор не называет, и объявлен он был бы просто «список». */}
          <Select value={action} onValueChange={v => { setAction(v); setPage(0); }}
            aria-label="Отбор по действию">
            <SelectItem value={ALL}>Все действия</SelectItem>
            {actions.map(a => <SelectItem key={a.code} value={a.code}>{a.title}</SelectItem>)}
          </Select>
        </div>
      </div>

      {isLoading ? (
        <div className="text-center text-fg4 text-sm py-10">Загрузка...</div>
      ) : records.length === 0 ? (
        <div className="text-center text-fg4 text-sm py-10">
          Записей нет. Журнал заполняется сам: сменой ролей, правкой схем и составом модулей.
        </div>
      ) : (
        <>
          <div className="border border-stroke rounded-lg overflow-hidden bg-surface">
            <table className="w-full text-sm">
              <thead className="bg-base border-b border-stroke">
                <tr>
                  <th className="text-left px-4 py-2.5 font-medium text-fg2 w-36">Когда</th>
                  <th className="text-left px-4 py-2.5 font-medium text-fg2 w-52">Кто</th>
                  <th className="text-left px-4 py-2.5 font-medium text-fg2">Что</th>
                  <th className="text-left px-4 py-2.5 font-medium text-fg2">Было → стало</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-muted">
                {records.map(r => (
                  <tr key={r.id} className="hover:bg-base align-top">
                    <td className="px-4 py-2.5 text-fg3 whitespace-nowrap tabular-nums">{when(r.occurredAt)}</td>
                    <td className="px-4 py-2.5 text-fg2 break-all">{r.actorName}</td>
                    <td className="px-4 py-2.5">
                      <div className="text-fg1">{r.actionTitle}</div>
                      {r.target && <div className="text-fg3 text-[12px] break-all">{r.target}</div>}
                    </td>
                    <td className="px-4 py-2.5 break-words">
                      <Change record={r} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {/* Страницами, а не кнопкой «показать ещё»: журнал копится годами, и растущий запрос
              рано или поздно упёрся бы в потолок страницы на сервере — список перестал бы
              удлиняться молча, и выглядело бы это как «записей больше нет». */}
          <div className="flex items-center justify-between mt-3 text-[13px] text-fg3">
            <span>
              Записи {page * PAGE + 1}–{page * PAGE + records.length} из {total}
            </span>
            <div className="flex items-center gap-2">
              <Button variant="tonal" disabled={page === 0} icon={<ChevronLeft size={15} />}
                onClick={() => setPage(p => Math.max(0, p - 1))}>Новее</Button>
              <Button variant="tonal" disabled={(page + 1) * PAGE >= total} icon={<ChevronRight size={15} />}
                onClick={() => setPage(p => p + 1)}>Раньше</Button>
            </div>
          </div>
        </>
      )}
    </div>
  );
}
