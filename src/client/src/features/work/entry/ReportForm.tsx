import { useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useParams } from 'react-router';
import {
  createWorkReport, entrySitesQuery, ownReportsKey, reportQuery, submitWorkReport, updateWorkReport,
  type EntrySite, type ReportHeader, type ReportMode, type WorkReport,
} from '@/shared/api/workReports';
import { formatDatePrecise } from '@/shared/format/format';
import { useAuth } from '@/shared/hooks/useAuth';
import { Banner } from '@/shared/ui/Banner';
import { Button, IconButton } from '@/shared/ui/Button';
import { Card } from '@/shared/ui/Card';
import { ArrowBack } from '@/shared/ui/icons';
import { LeaveGuard } from '@/shared/ui/LeaveGuard';
import { useGuardedLeave, useGuardedNavigate } from '@/shared/ui/NavigationGuard';
import { PageHeader } from '@/shared/ui/PageHeader';
import { ShiftDateHeader } from '@/shared/ui/ShiftDateHeader';
import { SitePicker } from '@/shared/ui/SitePicker';
import { apiError } from '@/shared/utils/apiError';
import { newUuid } from '@/shared/utils/localId';
import { EntryEmployeePicker } from './EntryEmployeePicker';
import { assignmentReason, automaticSubmission, reportFailure, reportShifts, reportStatuses } from './reportPresentation';

const root = (mode: ReportMode) => mode === 'own' ? '/my-reports' : '/work-review/entry';
const backTo = (mode: ReportMode) => mode === 'own' ? '/my-reports' : '/work-review';
const headerOf = (row: WorkReport): ReportHeader => ({ constructionId: row.constructionId, date: row.date, shift: row.shift });
const suggestionOf = (site?: EntrySite): ReportHeader => ({
  constructionId: site?.id ?? '', date: site?.suggested.date ?? '', shift: site?.suggested.shift ?? 'day',
});
type Employee = { id: string; name: string };

export function ReportForm({ mode }: { mode: ReportMode }) {
  const { reportId = 'new' } = useParams();
  return reportId === 'new' ? <NewReport key={mode} mode={mode} /> : <ExistingReport key={`${mode}/${reportId}`} mode={mode} id={reportId} />;
}

function NewReport({ mode }: { mode: ReportMode }) {
  const [employee, setEmployee] = useState<Employee>();
  const go = useGuardedNavigate();
  const sites = useQuery(entrySitesQuery(mode, employee?.id));
  if (mode === 'entry' && !employee) return <div className="h-full space-y-4 overflow-auto p-4 md:px-6">
    <PageHeader title="Ввести за сотрудника" icon={<IconButton icon={ArrowBack} label="К приёмке" onClick={() => go(backTo(mode))} />} />
    <EntryEmployeePicker onPick={setEmployee} />
  </div>;
  if (!sites.data) return <ReportLoading mode={mode} error={sites.error} retry={() => void sites.refetch()} />;
  return <Editor mode={mode} employee={employee} recommended={sites.data.items[0]}
    onPickAnother={employee ? () => setEmployee(undefined) : undefined} />;
}

function ExistingReport({ mode, id }: { mode: ReportMode; id: string }) {
  const report = useQuery({ ...reportQuery(mode, id), retry: false });
  if (!report.data) return <ReportLoading mode={mode} error={report.error} retry={() => void report.refetch()} />;
  return <Editor mode={mode} initial={report.data} employee={{ id: report.data.employeeId, name: report.data.employeeName ?? 'Сотрудник' }} />;
}

function ReportLoading({ mode, error, retry }: { mode: ReportMode; error: unknown; retry: () => void }) {
  const go = useGuardedNavigate();
  return <div className="space-y-4 p-4 md:px-6">
    <PageHeader title="Отчёт о работах" icon={<IconButton icon={ArrowBack} label="К отчётам" onClick={() => go(backTo(mode))} />} />
    {error ? <Banner tone="error" actions={[{ label: 'Повторить', onClick: retry }]}>
      {reportFailure(error, 'Не удалось загрузить отчёт.').text}
    </Banner> : <p role="status" className="text-body-medium">Загрузка…</p>}
  </div>;
}

function Editor({ mode, initial, employee, recommended, onPickAnother }: {
  mode: ReportMode; initial?: WorkReport; employee?: Employee; recommended?: EntrySite; onPickAnother?: () => void;
}) {
  const { user } = useAuth();
  const cache = useQueryClient();
  const navigate = useNavigate();
  const go = useGuardedNavigate();
  const guardedLeave = useGuardedLeave();
  const [saved, setSaved] = useState(initial);
  const [baseline, setBaseline] = useState(() => initial ? headerOf(initial) : suggestionOf(recommended));
  const [header, setHeader] = useState(baseline);
  const [incomplete, setIncomplete] = useState<string | null>(null);
  const [failure, setFailure] = useState<ReturnType<typeof reportFailure>>();
  const [busy, setBusy] = useState<'save' | 'submit' | 'read'>();
  const protocol = useRef<{ key: string; id: string } | null>(null);
  const [newId] = useState(newUuid);
  const sites = useQuery(entrySitesQuery(mode, employee?.id, header.date || undefined));
  const defaultSites = useQuery(entrySitesQuery(mode, employee?.id));
  const choices = sites.data?.items ?? [];
  const currentSite = choices.find(site => site.id === header.constructionId);
  const today = currentSite?.today ?? defaultSites.data?.items.find(site => site.id === header.constructionId)?.today
    ?? (saved?.constructionId === header.constructionId ? saved?.today ?? undefined : undefined);
  const unchangedKey = baseline.date === header.date && baseline.constructionId === header.constructionId;
  const knownSite = !!currentSite || (unchangedKey && !!saved);
  const dirty = !!incomplete || baseline.shift !== header.shift || !unchangedKey;
  const locked = !!saved && (saved.periodClosed || saved.status === 'submitted' || saved.status === 'accepted');
  const formProblem = incomplete ?? (!header.date ? 'Укажите дату смены.'
    : !header.constructionId ? 'Выберите стройку.'
    : !knownSite ? 'На эту дату выберите назначенную стройку.' : undefined);
  const reason = assignmentReason(sites.data?.boundary.reason, mode === 'entry');
  const selectable = saved && !choices.some(site => site.id === saved.constructionId)
    ? [...choices, { id: saved.constructionId, name: saved.constructionName ?? 'Стройка недоступна' }] : choices;

  function accept(row: WorkReport) {
    setSaved(row);
    setBaseline(headerOf(row));
    setHeader(headerOf(row));
    setFailure(undefined);
    protocol.current = null;
    cache.setQueryData(reportQuery(mode, row.id).queryKey, row);
    void cache.invalidateQueries({ queryKey: ownReportsKey });
    void cache.invalidateQueries({ queryKey: ['work', 'review-queue'] });
  }

  function changeId(key: string) {
    if (protocol.current?.key !== key) protocol.current = { key, id: newUuid() };
    return protocol.current.id;
  }

  async function save(redirect: boolean): Promise<boolean> {
    if (formProblem || locked || busy || failure?.stale) return false;
    setBusy('save');
    try {
      const row = saved
        ? await updateWorkReport(mode, saved, header, changeId(JSON.stringify([saved.version, header])))
        : await createWorkReport(mode, newId, mode === 'entry' ? employee?.id ?? null : null, header);
      accept(row);
      if (!saved && redirect) navigate(`${root(mode)}/${row.id}`, { replace: true });
      return true;
    } catch (error) {
      setFailure(reportFailure(error, 'Не удалось сохранить отчёт.'));
      return false;
    } finally { setBusy(undefined); }
  }

  async function submit() {
    if (!saved || dirty || locked || busy || failure?.stale) return;
    setBusy('submit');
    try {
      accept(await submitWorkReport(mode, saved, changeId(`submit/${saved.version}`)));
    } catch (error) { setFailure(reportFailure(error, 'Не удалось подать отчёт.')); }
    finally { setBusy(undefined); }
  }

  async function reread() {
    if (!saved || busy) return;
    setBusy('read');
    try {
      const row = await cache.fetchQuery({ ...reportQuery(mode, saved.id), staleTime: 0, retry: false });
      const next = headerOf(row);
      setSaved(row);
      setBaseline(next);
      setHeader({
        constructionId: header.constructionId !== baseline.constructionId ? header.constructionId : next.constructionId,
        date: header.date !== baseline.date ? header.date : next.date,
        shift: header.shift !== baseline.shift ? header.shift : next.shift,
      });
      setFailure(undefined);
      protocol.current = null;
    } catch (error) { setFailure({ text: reportFailure(error, 'Не удалось перечитать отчёт.').text, stale: true }); }
    finally { setBusy(undefined); }
  }

  return <div className="h-full min-w-0 space-y-4 overflow-auto px-4 py-4 md:px-6">
    <LeaveGuard active={dirty || !!busy} save={locked ? undefined : () => save(false)} saving={!!busy}
      saveBlocked={formProblem ?? (failure?.stale ? 'Сначала перечитайте отчёт.' : undefined)}
      message="Сохранить изменения отчёта перед уходом?" />
    <PageHeader title={saved ? `${formatDatePrecise(saved.date)} · ${reportShifts[saved.shift]}` : 'Новый отчёт'}
      subtitle={mode === 'entry' ? `Вводит ${saved?.enteredBy?.name ?? user?.displayName ?? 'менеджер'} за сотрудника: ${saved?.employeeName ?? employee?.name ?? ''}`
        : saved?.constructionName}
      icon={<IconButton icon={ArrowBack} label="К отчётам" onClick={() => go(backTo(mode))} />} />
    {onPickAnother && <Button variant="text" onClick={() => goBackToEmployee()}>Выбрать другого сотрудника</Button>}
    {saved && <p className="text-body-medium text-on-surface-variant">
      {reportStatuses[saved.status]}{automaticSubmission(saved) && ` · ${automaticSubmission(saved)}`}
    </p>}
    {saved?.returnComment && <Banner tone="warning">{saved.returnComment}</Banner>}
    {saved?.periodClosed && <Banner tone="info">Период закрыт.</Banner>}
    {saved?.status === 'submitted' && !saved.periodClosed && <Banner tone="info">
      Изменения строк будут видны менеджеру как правка после подачи.
    </Banner>}
    {failure && <Banner tone="error" actions={failure.existingId
      ? [{ label: 'Открыть', onClick: () => go(`${root(mode)}/${failure.existingId}`) }]
      : failure.stale ? [{ label: 'Перечитать', onClick: () => void reread(), loading: busy === 'read' }] : []}>{failure.text}</Banner>}
    {sites.isError && <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void sites.refetch() }]}>
      {apiError(sites.error, 'Не удалось загрузить стройки на дату смены.')}
    </Banner>}
    {reason && <Banner tone="info">{reason}</Banner>}
    <Card title="Стройка и смена">
      <div className="grid items-start gap-3 md:grid-cols-3">
        <SitePicker sites={selectable} boundary={sites.data?.boundary.text} value={header.constructionId}
          disabled={locked || !!busy || sites.isPending}
          onChange={id => {
            const site = choices.find(item => item.id === id);
            if (site) setHeader(!saved && header.date === baseline.date && header.shift === baseline.shift
              ? suggestionOf(site) : { ...header, constructionId: id });
          }} />
        <ShiftDateHeader date={header.date} shift={header.shift} today={today} locked={locked} busy={!!busy}
          lockReason={saved?.status === 'submitted' && !saved.periodClosed ? 'Смена меняется только после возврата' : undefined}
          onDate={date => setHeader(old => ({ ...old, date }))} onShift={shift => setHeader(old => ({ ...old, shift }))}
          onIncomplete={setIncomplete} />
      </div>
    </Card>
    {!locked && <>
      {formProblem && !incomplete && <p className="text-body-small text-on-surface-variant">{formProblem}</p>}
      <div className="flex flex-wrap justify-end gap-2">
        <Button variant={saved ? 'outlined' : 'filled'} loading={busy === 'save'}
          disabled={!!busy || !!formProblem || failure?.stale || (!!saved && !dirty)} onClick={() => void save(true)}>
          {saved ? 'Сохранить' : 'Создать черновик'}
        </Button>
        {saved && <Button variant="filled" loading={busy === 'submit'}
          disabled={!!busy || dirty || failure?.stale} onClick={() => void submit()}>Подать отчёт</Button>}
      </div>
    </>}
  </div>;

  function goBackToEmployee() {
    if (!onPickAnother) return;
    // Шаг смены сотрудника сохраняет тот же адрес, поэтому вызывает общий гард напрямую.
    guardedLeave(onPickAnother);
  }
}
