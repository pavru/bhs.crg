import { formatDateTime } from '@/shared/format/format';
import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { CalendarCheck, ChevronRight, KeyboardArrowDown, LockIcon, Undo } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { useChangedSince } from '@/shared/hooks/useChangedSince';
import { Modal, ModalExit } from '@/shared/ui/Modal';
import { DateField } from '@/shared/ui/DateField';
import { TextAreaField } from '@/shared/ui/TextAreaField';
import { useToast } from '@/shared/ui/Toast';
import { apiError } from '@/shared/utils/apiError';
import { toggleInSet } from '@/shared/utils/toggleInSet';
import { useCan } from '@/shared/api/access';
import { useListConstructions } from '@/shared/api/constructions';
import { useSiteFilter } from '@/shared/siteFilter/siteFilterContext';
import { SitesFilter } from '@/shared/siteFilter/SitesFilter';
import {
  usePeriods, usePeriodHistory, useClosePeriod, useReopenPeriod, useClosingPreview, inverted,
  type PeriodContourState, type PeriodClosureRecord, type ClosingSection,
} from '@/shared/api/periods';
import { ruDate, suggestFirstFrom, suggestThrough } from './periodDates';
import { ClosingSections } from './ClosingSections';
import { unfinishedSummary, visiblyChanged } from './closing';
import { companyClosingNote, companyReopenNote, shownSitesCount, type PeriodSites } from './periodSites';
import { DataGrid } from '@/shared/ui/DataGrid';
import { PageHeader } from '@/shared/ui/PageHeader';

/**
 * Учётный период (ТЗ CORE-35, issue #1081): до какой даты закрыт учёт компании и каждой стройки,
 * закрытие следующего периода и отмена ошибочного закрытия.
 *
 * Закрытие — префикс: «закрыто по дату» значит закрыто всё до неё. Поэтому у контура одна дата, а
 * не список месяцев, и закрыть можно только следующий отрезок — без пропусков.
 *
 * Диалог закрытия перечисляет по каждому модулю, что войдёт в закрытый период и что не завершено
 * (E1b, issue #1099). Перечень считает сервер; закрытие называет его отпечаток, и сервер откажет,
 * если данные с тех пор изменились, — в запись ложится то, что человек видел.
 */

/**
 * Диалог помнит КОНТУР, а не его состояние: состояние берётся из свежего ответа сервера при каждой
 * отрисовке. Со снимком на момент открытия повтор после отказа «границу тем временем изменили» слал
 * бы ту же устаревшую границу и получал бы тот же отказ — выйти можно было только закрыв диалог.
 */
type Dialog = { kind: 'close' | 'reopen'; constructionId: string | null } | null;

export function PeriodsPage() {
  const can = useCan();
  const { data, isLoading } = usePeriods();
  const { data: history = [] } = usePeriodHistory();
  // Названия строек открывает своё право: без него строки остаются, но безымянными.
  const namesAllowed = can.permission('core.constructions.read');
  const { data: constructions } = useListConstructions(namesAllowed);
  const [dialog, setDialog] = useState<Dialog>(null);
  // Избранные стройки (#1220) урезают ТАБЛИЦУ, и только её: закрытие, отмена, перечень и история
  // работают со всеми стройками, как без фильтра. Компания видна всегда.
  const filter = useSiteFilter();

  const nameOf = (id: string | null) => {
    if (!id) return 'Компания';
    if (!namesAllowed) return 'Стройка';
    // «Ещё не знаем» — не «удалена»: пока справочник не пришёл (или не пришёл вовсе), имени нет, и
    // сказать про стройку «удалена» значило бы выдать незнание за факт.
    if (!constructions) return 'Стройка…';
    return constructions.find(c => c.id === id)?.name ?? 'Стройка удалена';
  };

  if (isLoading || !data) {
    return <div className="px-6 py-10 text-center text-on-surface-variant text-sm">Загрузка...</div>;
  }

  const rows = [data.company, ...data.constructions];
  // Диалог ищет контур среди ВСЕХ: фильтр, включённый под открытым диалогом, не должен его гасить.
  const current = dialog && rows.find(r => r.constructionId === dialog.constructionId);
  // Пока фильтр не известен, строк строек нет: ни полных, ни урезанных (см. SiteFilterValue). Компания
  // и её кнопки — сразу: сбой настроек с повторами держал бы «загрузку» всей страницы (ревью PR #1333).
  const sitesKnown = filter.status !== 'loading';
  // Строка периода — сама стройка, и живая: сервер отдаёт границы только существующих. Поэтому здесь
  // «в избранном ли», а не правило объекта со ссылкой (`visible`): там стройка, которой нет в кэше
  // справочника, считается потерянной ссылкой и видна — а новая, заведённая с другого компьютера,
  // встала бы в таблицу мимо фильтра и занизила число скрытых (ревью PR #1333).
  const hiddenIds = new Set(filter.active
    ? data.constructions.map(s => s.constructionId!).filter(id => !filter.favorites.has(id)) : []);
  const shownSites = sitesKnown ? data.constructions.filter(s => !hiddenIds.has(s.constructionId!)) : [];
  const sitesOf = (states: PeriodContourState[]): PeriodSites =>
    ({ total: states.length, hidden: states.filter(s => hiddenIds.has(s.constructionId!)).length });
  const sites = sitesOf(data.constructions);
  // Отмена закрытия компании открывает дни всем стройкам, кроме закрытых своим закрытием.
  const kept = new Set(current?.reopenable?.keptClosed ?? []);
  const reopened = sitesOf(data.constructions.filter(s => !kept.has(s.constructionId!)));

  return (
    <div className="px-6 py-4 max-w-5xl">
      <PageHeader icon={<Icon icon={CalendarCheck} size={18} aria-hidden />} title="Учётный период" className="mb-1" />
      <p className="text-note text-on-surface-variant mb-4 max-w-3xl">
        Граница закрытого учёта компании и строек. Закрытие компании закрывает все стройки; стройку
        можно закрыть и дальше компании. Сегодня по часам компании — {ruDate(data.today)}:
        закрываются только прошедшие дни.
      </p>

      {/* Отбор «Стройки» (#1432) — над таблицей, которую он урезает. */}
      <div className="mb-2 flex"><SitesFilter hidden={sites.hidden} /></div>
      <DataGrid rows={[data.company, ...shownSites]} rowKey={state => state.constructionId ?? 'company'} columns={[
        { key: 'contour', label: 'Контур',
          // Число — и в шапке: строка под таблицей уходит за сгиб, а урезанную таблицу видно сразу.
          header: <>
            Контур
            {sites.hidden > 0 && (
              <span className="ml-2 font-normal" data-testid="period-sites-count">
                {shownSitesCount(shownSites.length, sites.total)}
              </span>
            )}
          </>,
          cell: state => <span className={state.constructionId ? '' : 'font-medium'}>{nameOf(state.constructionId)}</span> },
        { key: 'closed', label: 'Закрыто', width: 'w-72', cell: state => <Closed state={state} /> },
        { key: 'actions', label: '', width: 'w-72',
          cell: state => (
            <div className="flex items-center justify-end gap-1">
              {state.reopenable && (
                <Button size="sm" icon={<Icon icon={Undo} size={18} />}
                  onClick={() => setDialog({ kind: 'reopen', constructionId: state.constructionId })}>
                  Отменить закрытие
                </Button>
              )}
              <Button size="sm" variant="tonal" icon={<Icon icon={LockIcon} size={18} />}
                onClick={() => setDialog({ kind: 'close', constructionId: state.constructionId })}>
                Закрыть период
              </Button>
            </div>
          ) },
      ]} />
      {!sitesKnown && <p className="mt-2 text-note text-on-surface-variant">Загрузка строек...</p>}

      <History records={history} nameOf={nameOf} />

      {dialog?.kind === 'close' && current && (
        <CloseDialog state={current} name={nameOf(current.constructionId)} today={data.today}
          sites={sites} onDone={() => setDialog(null)} />
      )}
      {/* Отменять стало нечего (закрытие отменил кто-то другой) — диалог исчезает сам. */}
      {dialog?.kind === 'reopen' && current?.reopenable && (
        <ReopenDialog state={current} name={nameOf(current.constructionId)} nameOf={nameOf}
          sites={reopened} onDone={() => setDialog(null)} />
      )}
    </div>
  );
}

function Closed({ state }: { state: PeriodContourState }) {
  if (!state.closedThrough) return <span className="text-on-surface-variant">не закрыто ничего</span>;

  // Стройка, закрытая чужим закрытием, обязана это сказать: «Отменить» у неё нет, и без пояснения
  // строка выглядит сломанной — закрыта, а открыть нечем.
  const byCompany = state.constructionId && state.ownClosedThrough !== state.closedThrough;
  return (
    <span className="text-on-surface tabular-nums">
      по {ruDate(state.closedThrough)}
      {byCompany && <span className="text-on-surface-variant"> — закрытием компании</span>}
    </span>
  );
}

export function CloseDialog({ state, name, today, sites, onDone }: {
  state: PeriodContourState; name: string; today: string;
  /** Сколько строек закроет закрытие компании и сколько из них скрыто фильтром; нет — число не называется. */
  sites?: PeriodSites;
  onDone: () => void;
}) {
  const toast = useToast();
  const close = useClosePeriod();
  const [through, setThrough] = useState(() => suggestThrough(today, state.closedThrough));
  // Начало первого закрытия называет человек; у следующих оно задано границей и не правится.
  const [firstFrom, setFirstFrom] = useState(() => suggestFirstFrom(suggestThrough(today, state.closedThrough)));
  // Даты чаще выбирают в календаре, а его окно само не замечает: признак правок — явно.
  const edited = useChangedSince([through, firstFrom]);
  const [error, setError] = useState<string | null>(null);
  // Перечень, который человек видел, когда сервер ответил «данные изменились», — и о каких датах он
  // был: новый перечень называет рядом с изменившимися строками прежние числа. Сменили даты —
  // сравнивать уже не с чем.
  const [refused, setRefused] = useState<{ dates: string; sections: ClosingSection[] } | null>(null);

  const from = state.expectedFrom ?? firstFrom;
  const dates = `${from}|${through}`;
  // Конец раньше начала: перечень за такие даты не спрашивается (см. useClosingPreview), и сказать об
  // этом — дело диалога. Так бывает, когда период по этот день закрыл кто-то другой, пока диалог был
  // открыт; своё удавшееся закрытие сюда не доходит — диалог закрывается раньше (см. useClosePeriod).
  const backwards = inverted(from, through);
  const preview = useClosingPreview(state, from, through);
  // Отказ перечня, пока закрытие досчитывается, не показываем: после отказа закрытия перечитывается и
  // состояние контура, и перечень с прежними датами, и придут они в любом порядке. Перечень, пришедший
  // первым, ответил бы «уже закрыт» — красной строкой на миг, до того как диалог узнает о сдвиге границы.
  const previewFailed = preview.isError && !close.isPending;
  // Закрыть можно только то, что видел: перечень получен, он — про эти даты, а не про прежние, и он
  // сейчас на экране. Упавший перезапрос оставляет прежние data, а показывает уже отказ — отпечаток
  // того, чего на экране нет, подтверждать нечем.
  const seen = preview.data && !preview.isError && !preview.isPlaceholderData && !preview.isFetching
    ? preview.data : null;
  const ready = !!from && !!through && !!seen;
  // То, что останется после закрытия как есть, — рядом с кнопкой: перечень длинный, и кнопка бывает
  // видна без него.
  const unfinished = seen ? unfinishedSummary(seen.sections) : null;
  const before = seen && refused?.dates === dates ? refused.sections : null;

  return (
    <Modal open onOpenChange={o => { if (!o) onDone(); }} title={`Закрыть период: ${name}`}
      isDirty={edited} busy={close.isPending}
      footer={
        <div className="flex flex-wrap items-center justify-end gap-x-3 gap-y-2">
          {unfinished && (
            <span className="mr-auto text-note text-on-surface-variant">
              Не завершено: <span className="font-medium text-on-surface">{unfinished}</span>
            </span>
          )}
          <ModalExit />
          <Button variant="filled" disabled={!ready} loading={close.isPending}
            onClick={async () => {
              setError(null);
              const shown = seen!;
              // Под «не удалось закрыть» — только само закрытие. Сбой после него (уведомление, закрытие
              // диалога) отказом закрытия не является, и называть его так нельзя: период уже закрыт.
              try {
                await close.mutateAsync({ state, from, through, report: shown.stamp });
              } catch (e) {
                // 409 — границу или данные тем временем изменили: перечень перечитывается, и новому есть
                // с чем сравниться.
                if ((e as { response?: { status?: number } })?.response?.status === 409)
                  setRefused({ dates, sections: shown.sections });
                setError(apiError(e, 'Не удалось закрыть период.'));
                return;
              }
              // Сначала закрыть диалог: период закрыт, и сбой уведомления не должен оставить его открытым
              // и молчащим.
              onDone();
              const left = unfinishedSummary(shown.sections);
              toast.success(`${name}: период закрыт по ${ruDate(through)}.${left ? ` Не завершено: ${left}.` : ''}`);
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
            <DateField label="С" value={firstFrom} onChange={v => { setFirstFrom(v); setError(null); }} required
              hint="всё до этого дня закроется тоже" />
          )}
          {/* Конец периода не раньше начала — календарь не даст его выбрать (#1464); набранное проверит сервер. */}
          <DateField label="По (включительно)" value={through} onChange={v => { setThrough(v); setError(null); }} required
            min={(state.expectedFrom ?? firstFrom) || undefined} />
        </div>
        <p className="text-note text-on-surface-variant">
          {through
            ? <>Будет закрыто всё по {ruDate(through)} включительно.</>
            : <>Назовите последний день периода.</>}
          {!state.constructionId && ` ${companyClosingNote(sites)}`}
        </p>
        {backwards ? (
          <p role="alert" className="text-note text-error">
            {state.expectedFrom
              ? <>Период по {ruDate(through)} уже закрыт: следующий начинается {ruDate(from)}. Назовите конец
                не раньше этого дня.</>
              : <>Конец периода раньше начала. Поправьте одну из дат.</>}
          </p>
        ) : previewFailed ? (
          <p role="alert" className="text-note text-error">
            {apiError(preview.error, 'Не удалось узнать, что попадёт в период.')} Без перечня закрыть нельзя.
          </p>
        ) : preview.data ? (
          <>
            <ClosingSections sections={preview.data.sections} stale={!seen} before={before} />
            {/* Сервер сверяет весь перечень, а на экране — не всё: суммы бывают скрыты правом. */}
            {before && seen && !visiblyChanged(before, seen.sections) && (
              <p role="note" className="text-note text-on-surface-variant">
                Числа на экране прежние: изменилось то, чего здесь не видно, — например, суммы, скрытые
                правом. Перечень перечитан — закрыть можно.
              </p>
            )}
          </>
        ) : (
          from && through && <p className="text-note text-on-surface-variant">Считаем, что попадёт в период…</p>
        )}
        {/* Отказ закрытия — о датах, с которыми его отправляли. Сменили дату — он уже не о том. А когда
            период «наоборот», причина названа выше, и точнее: рядом с «период уже закрыт» строка «не
            удалось закрыть» противоречила бы ей — так бывает, когда закрытие прошло, а ответ потерялся. */}
        {error && !backwards && <p role="alert" className="text-note text-error">{error}</p>}
      </div>
    </Modal>
  );
}

export function ReopenDialog({ state, name, nameOf, sites, onDone }: {
  state: PeriodContourState; name: string; nameOf: (id: string | null) => string;
  /** Скольким стройкам отмена закрытия компании откроет дни и сколько из них скрыто фильтром. */
  sites?: PeriodSites;
  onDone: () => void;
}) {
  const toast = useToast();
  const reopen = useReopenPeriod();
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);
  const target = state.reopenable!;
  // Отмена закрытия компании открывает дни и скрытым фильтром стройкам — как закрытие их закрывало.
  const reopens = !state.constructionId && sites ? companyReopenNote(sites) : null;

  return (
    <Modal open onOpenChange={o => { if (!o) onDone(); }} title={`Отменить закрытие: ${name}`}
      busy={reopen.isPending}
      footer={
        <div className="flex items-center justify-end gap-2">
          <ModalExit>Не отменять</ModalExit>
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
        <p className="text-note text-on-surface-variant">
          Отменяется последнее закрытие — с {ruDate(target.from)} по {ruDate(target.through)}. Эти
          дни снова станут открытыми. Учётные даты, записанные, пока период был закрыт, останутся как
          легли. Запись о закрытии не стирается: отмена записывается рядом, с причиной.
          {reopens && ` ${reopens}`}
        </p>
        {target.keptClosed.length > 0 && (
          <p role="note" className="text-note text-warning">
            У этих строек дни останутся закрытыми — они закрыты своим закрытием, и его нужно
            отменить отдельно: {target.keptClosed.map(nameOf).join(', ')}.
          </p>
        )}
        <TextAreaField label="Причина отмены" value={reason} onChange={e => setReason(e.target.value)}
          rows={3} required hint="её увидят в истории и в журнале действий" />
        {error && <p role="alert" className="text-note text-error">{error}</p>}
      </div>
    </Modal>
  );
}

function History({ records, nameOf }: {
  records: PeriodClosureRecord[]; nameOf: (id: string | null) => string;
}) {
  // Раскрытые записи — по идентификатору: список перечитывается после каждого закрытия.
  const [open, setOpen] = useState<ReadonlySet<string>>(new Set());
  if (records.length === 0) return null;

  const toggle = (id: string) => setOpen(was => toggleInSet(was, id));

  return (
    <>
      <h2 className="text-sm font-semibold text-on-surface-variant mt-6 mb-2">История</h2>
      <DataGrid rows={records} rowKey={r => r.id} columns={[
        { key: 'at', label: 'Когда', width: 'w-36',
          cell: r => <span className="text-on-surface-variant tabular-nums">{formatDateTime(r.at)}</span> },
        { key: 'byName', label: 'Кто', width: 'w-52', wrap: true, cell: r => <span className="text-on-surface-variant break-all">{r.byName}</span> },
        { key: 'what', label: 'Что', wrap: true,
          cell: r => (
            <>
              <div>
                {r.kind === 'Close' ? 'Закрыт период' : 'Отменено закрытие'}
                {' '}с {ruDate(r.from)} по {ruDate(r.through)} — {nameOf(r.constructionId)}
              </div>
              {r.reason && <div className="text-on-surface-variant text-xs">{r.reason}</div>}
              {r.kind === 'Close' && (r.report ? (
                <>
                  <button type="button" aria-expanded={open.has(r.id)} onClick={() => toggle(r.id)}
                    className="mt-0.5 inline-flex items-center gap-0.5 text-xs text-primary hover:underline">
                    {open.has(r.id) ? <Icon icon={KeyboardArrowDown} size={18} /> : <Icon icon={ChevronRight} size={18} />}
                    Что показал диалог при закрытии
                  </button>
                  {open.has(r.id) && (
                    <div className="mt-1.5 mb-1 border-l-2 border-outline-variant pl-3 whitespace-normal">
                      {/* Записанное — цитата, его не трогаем; меняется подпись над ним (E2, разбор Дизайнера, п. 8).
                          Она верна для любой записи: старые отличать не нужно. Модулей экран ядра не знает —
                          поэтому «документы», а не «счета». */}
                      <p className="text-xs text-on-surface-variant mb-2">
                        Так диалог выглядел в день закрытия — и числа, и пояснения. С тех пор могли измениться и
                        данные, и правила: документ закрытого периода бывает исправлен после закрытия.
                      </p>
                      <ClosingSections sections={r.report} />
                    </div>
                  )}
                </>
              ) : (
                // Закрытие сделано до появления перечня: молчание выглядело бы как «незавершённого не было».
                <div className="text-on-surface-variant text-xs">Перечень не записывался.</div>
              ))}
            </>
          ) },
      ]} />
    </>
  );
}
