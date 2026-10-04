import { useState } from 'react';
import { CalendarCheck, Lock, Undo2 } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { Modal } from '@/shared/ui/Modal';
import { DateField } from '@/shared/ui/DateField';
import { TextAreaField } from '@/shared/ui/TextAreaField';
import { useToast } from '@/shared/ui/Toast';
import { apiError } from '@/shared/utils/apiError';
import { useCan } from '@/shared/api/access';
import { useListConstructions } from '@/shared/api/constructions';
import {
  usePeriods, usePeriodHistory, useClosePeriod, useReopenPeriod,
  type PeriodContourState, type PeriodClosureRecord,
} from '@/shared/api/periods';
import { ruDate, suggestFirstFrom, suggestThrough } from './periodDates';

/**
 * Учётный период (ТЗ CORE-35, issue #1081): до какой даты закрыт учёт компании и каждой стройки,
 * закрытие следующего периода и отмена ошибочного закрытия.
 *
 * Закрытие — префикс: «закрыто по дату» значит закрыто всё до неё. Поэтому у контура одна дата, а
 * не список месяцев, и закрыть можно только следующий отрезок — без пропусков.
 *
 * Перечня «что при этом замёрзнет» здесь нет: его соберёт диалог закрытия отдельной задачей
 * (E1b), по данным каждого модуля и под его правом.
 */

type Dialog = { kind: 'close' | 'reopen'; state: PeriodContourState; name: string } | null;

function when(iso: string): string {
  return new Date(iso).toLocaleString('ru-RU', {
    day: '2-digit', month: '2-digit', year: '2-digit', hour: '2-digit', minute: '2-digit',
  });
}

export function PeriodsPage() {
  const can = useCan();
  const { data, isLoading } = usePeriods();
  const { data: history = [] } = usePeriodHistory();
  // Названия строек открывает своё право: без него строки остаются, но безымянными.
  const namesAllowed = can.permission('core.constructions.read');
  const { data: constructions = [] } = useListConstructions(namesAllowed);
  const [dialog, setDialog] = useState<Dialog>(null);

  const nameOf = (id: string | null) => {
    if (!id) return 'Компания';
    return constructions.find(c => c.id === id)?.name ?? (namesAllowed ? 'Стройка удалена' : 'Стройка');
  };

  if (isLoading || !data) {
    return <div className="px-6 py-10 text-center text-fg4 text-sm">Загрузка...</div>;
  }

  const rows = [data.company, ...data.constructions];

  return (
    <div className="px-6 py-4 max-w-5xl">
      <h1 className="text-xl font-semibold text-fg1 flex items-center gap-2 mb-1">
        <CalendarCheck size={18} className="text-fg3" />
        Учётный период
      </h1>
      <p className="text-[13px] text-fg3 mb-4 max-w-3xl">
        Закрытый период модули править не дают. Закрытие компании закрывает все стройки; стройку
        можно закрыть и дальше компании. Сегодня по часам компании — {ruDate(data.today)}:
        закрываются только прошедшие дни.
      </p>

      <div className="border border-stroke rounded-lg overflow-hidden bg-surface">
        <table className="w-full text-sm">
          <thead className="bg-base border-b border-stroke">
            <tr>
              <th className="text-left px-4 py-2.5 font-medium text-fg2">Контур</th>
              <th className="text-left px-4 py-2.5 font-medium text-fg2 w-72">Закрыто</th>
              <th className="px-4 py-2.5 w-72" />
            </tr>
          </thead>
          <tbody className="divide-y divide-muted">
            {rows.map(state => {
              const name = nameOf(state.constructionId);
              return (
                <tr key={state.constructionId ?? 'company'} className="hover:bg-base">
                  <td className={`px-4 py-2 ${state.constructionId ? 'text-fg1' : 'text-fg1 font-medium'}`}>
                    {name}
                  </td>
                  <td className="px-4 py-2"><Closed state={state} /></td>
                  <td className="px-4 py-2">
                    <div className="flex items-center justify-end gap-1">
                      {state.reopenable && (
                        <Button size="sm" icon={<Undo2 size={14} />}
                          onClick={() => setDialog({ kind: 'reopen', state, name })}>
                          Отменить закрытие
                        </Button>
                      )}
                      <Button size="sm" variant="tonal" icon={<Lock size={14} />}
                        onClick={() => setDialog({ kind: 'close', state, name })}>
                        Закрыть период
                      </Button>
                    </div>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>

      <History records={history} nameOf={nameOf} />

      {dialog?.kind === 'close' && (
        <CloseDialog state={dialog.state} name={dialog.name} today={data.today} onDone={() => setDialog(null)} />
      )}
      {dialog?.kind === 'reopen' && (
        <ReopenDialog state={dialog.state} name={dialog.name} onDone={() => setDialog(null)} />
      )}
    </div>
  );
}

function Closed({ state }: { state: PeriodContourState }) {
  if (!state.closedThrough) return <span className="text-fg4">не закрыто ничего</span>;

  // Стройка, закрытая чужим закрытием, обязана это сказать: «Отменить» у неё нет, и без пояснения
  // строка выглядит сломанной — закрыта, а открыть нечем.
  const byCompany = state.constructionId && state.ownClosedThrough !== state.closedThrough;
  return (
    <span className="text-fg1 tabular-nums">
      по {ruDate(state.closedThrough)}
      {byCompany && <span className="text-fg3"> — закрытием компании</span>}
    </span>
  );
}

function CloseDialog({ state, name, today, onDone }: {
  state: PeriodContourState; name: string; today: string; onDone: () => void;
}) {
  const toast = useToast();
  const close = useClosePeriod();
  const [through, setThrough] = useState(() => suggestThrough(today, state.closedThrough));
  // Начало первого закрытия называет человек; у следующих оно задано границей и не правится.
  const [firstFrom, setFirstFrom] = useState(() => suggestFirstFrom(suggestThrough(today, state.closedThrough)));
  const [error, setError] = useState<string | null>(null);

  const from = state.expectedFrom ?? firstFrom;
  const ready = !!from && !!through;

  return (
    <Modal open onOpenChange={o => { if (!o) onDone(); }} title={`Закрыть период: ${name}`}
      footer={
        <div className="flex items-center justify-end gap-2">
          <Button variant="text" onClick={onDone}>Отмена</Button>
          <Button variant="filled" disabled={!ready} loading={close.isPending}
            onClick={async () => {
              setError(null);
              try {
                await close.mutateAsync({ state, from, through });
                toast.success(`${name}: период закрыт по ${ruDate(through)}.`);
                onDone();
              } catch (e) {
                setError(apiError(e, 'Не удалось закрыть период.'));
              }
            }}>
            Закрыть период
          </Button>
        </div>
      }>
      <div className="space-y-4">
        <div className="grid grid-cols-2 gap-3">
          {state.expectedFrom ? (
            <DateField label="С" value={state.expectedFrom} onChange={() => {}} readOnly
              hint="следующий день после закрытого" />
          ) : (
            <DateField label="С" value={firstFrom} onChange={setFirstFrom} required
              hint="всё до этого дня закроется тоже" />
          )}
          <DateField label="По (включительно)" value={through} onChange={setThrough} required />
        </div>
        <p className="text-[13px] text-fg2">
          {through
            ? <>Будет закрыто всё по {ruDate(through)} включительно. Записи этого периода модули
                править не дадут.</>
            : <>Назовите последний день периода.</>}
          {!state.constructionId && ' Закрытие компании закрывает и все стройки.'}
        </p>
        {error && <p role="alert" className="text-[13px] text-danger">{error}</p>}
      </div>
    </Modal>
  );
}

function ReopenDialog({ state, name, onDone }: {
  state: PeriodContourState; name: string; onDone: () => void;
}) {
  const toast = useToast();
  const reopen = useReopenPeriod();
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);
  const target = state.reopenable!;

  return (
    <Modal open onOpenChange={o => { if (!o) onDone(); }} title={`Отменить закрытие: ${name}`}
      footer={
        <div className="flex items-center justify-end gap-2">
          <Button variant="text" onClick={onDone}>Не отменять</Button>
          <Button variant="filled" danger disabled={!reason.trim()} loading={reopen.isPending}
            onClick={async () => {
              setError(null);
              try {
                await reopen.mutateAsync({ state, reason });
                toast.success(`${name}: закрытие по ${ruDate(target.through)} отменено.`);
                onDone();
              } catch (e) {
                setError(apiError(e, 'Не удалось отменить закрытие.'));
              }
            }}>
            Отменить закрытие
          </Button>
        </div>
      }>
      <div className="space-y-4">
        <p className="text-[13px] text-fg2">
          Отменяется последнее закрытие — с {ruDate(target.from)} по {ruDate(target.through)}. Период
          снова откроется для правки. Учётные даты, записанные, пока он был закрыт, останутся как
          легли. Запись о закрытии не стирается: отмена записывается рядом, с причиной.
        </p>
        <TextAreaField label="Причина отмены" value={reason} onChange={e => setReason(e.target.value)}
          rows={3} required hint="её увидят в истории и в журнале действий" />
        {error && <p role="alert" className="text-[13px] text-danger">{error}</p>}
      </div>
    </Modal>
  );
}

function History({ records, nameOf }: {
  records: PeriodClosureRecord[]; nameOf: (id: string | null) => string;
}) {
  if (records.length === 0) return null;

  return (
    <>
      <h2 className="text-sm font-semibold text-fg2 mt-6 mb-2">История</h2>
      <div className="border border-stroke rounded-lg overflow-hidden bg-surface">
        <table className="w-full text-sm">
          <thead className="bg-base border-b border-stroke">
            <tr>
              <th className="text-left px-4 py-2.5 font-medium text-fg2 w-36">Когда</th>
              <th className="text-left px-4 py-2.5 font-medium text-fg2 w-52">Кто</th>
              <th className="text-left px-4 py-2.5 font-medium text-fg2">Что</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-muted">
            {records.map(r => (
              <tr key={r.id} className="hover:bg-base align-top">
                <td className="px-4 py-2 text-fg3 whitespace-nowrap tabular-nums">{when(r.at)}</td>
                <td className="px-4 py-2 text-fg2 break-all">{r.byName}</td>
                <td className="px-4 py-2">
                  <div className="text-fg1">
                    {r.kind === 'Close' ? 'Закрыт период' : 'Отменено закрытие'}
                    {' '}с {ruDate(r.from)} по {ruDate(r.through)} — {nameOf(r.constructionId)}
                  </div>
                  {r.reason && <div className="text-fg3 text-[12px] break-words">{r.reason}</div>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}
