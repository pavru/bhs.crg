import { useEffect, useMemo, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useLocation, useNavigate, useParams } from 'react-router';
import { removeWorkPhoto } from '@/shared/api/workPhotos';
import {
  createWorkReport, entrySitesQuery, invalidateWorkProgress, ownReportsKey, reportQuery, submitWorkReport, updateWorkReport,
  type EntrySite, type ReportHeader, type ReportInput, type ReportMode, type RequiredReportField, type WorkReport,
} from '@/shared/api/workReports';
import { WORK_SUMMARY_KEY } from '@/shared/api/workSummary';
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
import { TextField } from '@/shared/ui/TextField';
import { apiError } from '@/shared/utils/apiError';
import { newUuid } from '@/shared/utils/localId';
import { EntryEmployeePicker } from './EntryEmployeePicker';
import { ReportLines } from './ReportLines';
import { contentsOf, contentsProblem, rebaseContents, reportInput, sameContent, type ReportContents } from './reportContents';
import { assignmentReason, automaticSubmission, reportFailure, reportShifts, reportStatuses, requiredFieldTitle } from './reportPresentation';
import { usePhotoUploads, type PhotoUploads } from './usePhotoUploads';

const root = (mode: ReportMode) => mode === 'own' ? '/my-reports' : '/work-review/entry';
const backTo = (mode: ReportMode) => mode === 'own' ? '/my-reports' : '/work-review';
const headerOf = (row: WorkReport): ReportHeader => ({ constructionId: row.constructionId, date: row.date, shift: row.shift });
const reportLocked = (row?: WorkReport) => !!row && (row.periodClosed || row.status === 'accepted');
const reportKeyLocked = (row?: WorkReport) => reportLocked(row) || row?.status === 'submitted';
const suggestionOf = (site?: EntrySite, date?: string): ReportHeader => ({
  constructionId: site?.id ?? '', date: date ?? site?.suggested.date ?? '', shift: site?.suggested.shift ?? 'day',
});
const rebaseHeader = (base: ReportHeader, local: ReportHeader, fresh: ReportHeader): ReportHeader => ({
  constructionId: base.constructionId === local.constructionId ? fresh.constructionId : local.constructionId,
  date: base.date === local.date ? fresh.date : local.date,
  shift: base.shift === local.shift ? fresh.shift : local.shift,
});
type Employee = { id: string; name: string };

export function ReportForm({ mode }: { mode: ReportMode }) {
  const { reportId = 'new' } = useParams();
  const location = useLocation();
  const visit = reportId === 'new' ? location.key : location.state?.reportEntryVisit ?? reportId;
  return <ReportVisit key={`${mode}/${visit}`} id={reportId} mode={mode} />;
}

function ReportVisit({ id, mode }: { id: string; mode: ReportMode }) {
  const uploads = usePhotoUploads();
  return id === 'new' ? <NewReport mode={mode} uploads={uploads} />
    : <ExistingReport key={id} mode={mode} id={id} uploads={uploads} />;
}

function NewReport({ mode, uploads }: { mode: ReportMode; uploads: PhotoUploads }) {
  const [employee, setEmployee] = useState<Employee>();
  const [date, setDate] = useState('');
  const go = useGuardedNavigate();
  const sites = useQuery(entrySitesQuery(mode, employee?.id, date || undefined));
  if (mode === 'entry' && !employee) return <div className="h-full space-y-4 overflow-auto p-4 md:px-6">
    <PageHeader title="Ввести за сотрудника" icon={<IconButton icon={ArrowBack} label="К приёмке" onClick={() => go(backTo(mode))} />} />
    <EntryEmployeePicker date={date} onDate={setDate} onPick={setEmployee} />
  </div>;
  if (!sites.data) return <ReportLoading mode={mode} error={sites.error} retry={() => void sites.refetch()} />;
  return <Editor mode={mode} employee={employee} recommended={sites.data.items[0]} requestedDate={date || undefined}
    uploads={uploads} onPickAnother={employee ? () => setEmployee(undefined) : undefined} />;
}

function ExistingReport({ mode, id, uploads }: { mode: ReportMode; id: string; uploads: PhotoUploads }) {
  const report = useQuery({ ...reportQuery(mode, id), retry: false, refetchOnMount: 'always' });
  if (!report.data) return <ReportLoading mode={mode} error={report.error} retry={() => void report.refetch()} />;
  return <Editor mode={mode} initial={report.data} uploads={uploads}
    employee={{ id: report.data.employeeId, name: report.data.employeeName ?? 'Сотрудник' }} />;
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

function Editor({ mode, initial, employee, recommended, requestedDate, onPickAnother, uploads }: {
  mode: ReportMode; initial?: WorkReport; employee?: Employee; recommended?: EntrySite; requestedDate?: string;
  onPickAnother?: () => void;
  uploads: PhotoUploads;
}) {
  const { user } = useAuth();
  const cache = useQueryClient();
  const navigate = useNavigate();
  const location = useLocation();
  const go = useGuardedNavigate();
  const guardedLeave = useGuardedLeave();
  const [saved, setSaved] = useState(initial);
  const [observed, setObserved] = useState(initial);
  const [baseline, setBaseline] = useState(() => initial ? headerOf(initial) : suggestionOf(recommended, requestedDate));
  const [header, setHeader] = useState(baseline);
  const [contents, setContents] = useState(() => contentsOf(initial));
  const savedContents = useMemo(() => contentsOf(saved), [saved]);
  const [incomplete, setIncomplete] = useState<string | null>(null);
  const [failure, setFailure] = useState<ReturnType<typeof reportFailure>>();
  const [errorFocus, setErrorFocus] = useState<RequiredReportField>();
  const [restoredLines, setRestoredLines] = useState<number[]>([]);
  const [mergeConflicts, setMergeConflicts] = useState<string[]>([]);
  const [headerRestored, setHeaderRestored] = useState(false);
  const [busy, setBusy] = useState<'save' | 'submit' | 'read' | 'photo'>();
  const protocol = useRef<{ key: string; id: string } | null>(null);
  const creation = useRef<{ input: ReportInput; header: ReportHeader; contents: ReportContents } | null>(null);
  const [newId] = useState(newUuid);
  const sites = useQuery(entrySitesQuery(mode, employee?.id, header.date || undefined));
  const defaultSites = useQuery(entrySitesQuery(mode, employee?.id, requestedDate));
  const choices = sites.data?.items ?? [];
  const currentSite = choices.find(site => site.id === header.constructionId);
  const today = currentSite?.today ?? defaultSites.data?.items.find(site => site.id === header.constructionId)?.today
    ?? (saved?.constructionId === header.constructionId ? saved?.today ?? undefined : undefined);
  const unchangedKey = baseline.date === header.date && baseline.constructionId === header.constructionId;
  const knownSite = !!currentSite || (unchangedKey && !!saved);
  const dirty = !!incomplete || baseline.shift !== header.shift || !unchangedKey || !sameContent(contents, savedContents);
  const locked = reportLocked(saved);
  const headerLocked = reportKeyLocked(saved);
  const formProblem = incomplete ?? (!header.date ? 'Укажите дату смены.'
    : !header.constructionId ? 'Выберите стройку.'
    : !knownSite ? 'На эту дату выберите назначенную стройку.' : contentsProblem(contents));
  const reason = assignmentReason(sites.data?.boundary.reason, mode === 'entry');
  const selectable = saved && !choices.some(site => site.id === saved.constructionId)
    ? [...choices, { id: saved.constructionId, name: saved.constructionName ?? 'Стройка недоступна' }] : choices;
  const { deliver, transfers, forget } = uploads;
  const pendingFiles = contents.lines.some(line => line.photos.some(photo => {
    const transfer = transfers[photo.id];
    return photo.state !== 'stored' && transfer && 'file' in transfer && !!transfer.file;
  }));
  useEffect(() => { if (saved) deliver(saved, contents.lines); }, [deliver, saved, contents.lines]);

  // Пока человек правит отчёт, черновик и If-Match остаются привязаны к одной серверной версии.
  if (initial && initial !== observed && !dirty && !busy && !failure?.stale) {
    const next = headerOf(initial);
    setObserved(initial);
    setSaved(initial);
    setBaseline(next);
    setHeader(next);
    setContents(contentsOf(initial));
  }

  async function accept(row: WorkReport, nextHeader = headerOf(row), nextContents = contentsOf(row)) {
    const { queryKey } = reportQuery(mode, row.id);
    await cache.cancelQueries({ queryKey, exact: true });
    setSaved(row);
    setBaseline(headerOf(row));
    setHeader(nextHeader);
    setContents(nextContents);
    setFailure(undefined);
    setErrorFocus(undefined);
    setRestoredLines([]);
    setMergeConflicts([]);
    setHeaderRestored(false);
    protocol.current = null;
    cache.setQueryData(queryKey, row);
    void cache.invalidateQueries({ queryKey: ownReportsKey });
    void cache.invalidateQueries({ queryKey: ['work', 'review-queue'] });
    void cache.invalidateQueries({ queryKey: WORK_SUMMARY_KEY });
  }

  function changeId(key: string) {
    if (protocol.current?.key !== key) protocol.current = { key, id: newUuid() };
    return protocol.current.id;
  }

  function openSaved(id: string) {
    navigate(`${root(mode)}/${id}`, { replace: true, state: { reportEntryVisit: location.key } });
  }

  async function save(redirect: boolean): Promise<boolean> {
    if (formProblem || locked || busy || failure?.stale) return false;
    setBusy('save');
    try {
      const input = reportInput(header, contents, saved?.note ?? null, savedContents);
      // После неоднозначного сбоя повторяем исходное создание. Новые правки накладываем на его ответ.
      const first = saved ? null : creation.current ??= { input, header, contents };
      const row = saved
        ? await updateWorkReport(mode, saved, input, changeId(JSON.stringify([saved.version, input])))
        : await createWorkReport(mode, newId, mode === 'entry' ? employee?.id ?? null : null, first?.input ?? input);
      const fresh = contentsOf(row);
      const replay = first ? rebaseContents(first.contents, contents, fresh, reportLocked(row)) : undefined;
      const nextContents = replay?.contents ?? fresh;
      const serverHeader = headerOf(row);
      const mergedHeader = first ? rebaseHeader(first.header, header, serverHeader) : serverHeader;
      const resetHeader = reportKeyLocked(row) && !sameContent(mergedHeader, serverHeader);
      const nextHeader = resetHeader ? serverHeader : mergedHeader;
      const needsSave = !sameContent(nextHeader, serverHeader) || !sameContent(nextContents, fresh);
      await accept(row, nextHeader, nextContents);
      setHeaderRestored(resetHeader);
      if (replay?.restored.length) setRestoredLines(replay.restored);
      if (replay?.conflicts.length) setMergeConflicts(replay.conflicts);
      if (needsSave || resetHeader || replay?.restored.length || replay?.conflicts.length) return false;
      if (!initial && redirect) openSaved(row.id);
      return true;
    } catch (error) {
      const status = (error as { response?: { status?: number } })?.response?.status;
      if (!saved && status && status >= 400 && status < 500 && status !== 408) creation.current = null;
      setFailure(reportFailure(error, 'Не удалось сохранить отчёт.'));
      return false;
    } finally { setBusy(undefined); }
  }

  async function submit() {
    if (!saved || dirty || headerLocked || busy || failure?.stale) return;
    setBusy('submit');
    try {
      const row = await submitWorkReport(mode, saved, changeId(`submit/${saved.version}`));
      void invalidateWorkProgress(cache, row.constructionId);
      await accept(row);
      if (!initial) openSaved(row.id);
    } catch (error) {
      const refusal = reportFailure(error, 'Не удалось подать отчёт.', 'submit');
      setFailure(refusal);
      setErrorFocus(refusal.requiredMissing?.[0]);
    }
    finally { setBusy(undefined); }
  }

  async function adoptFresh(row: WorkReport) {
    const next = headerOf(row);
    const mergedHeader = rebaseHeader(baseline, header, next);
    const keyLocked = reportKeyLocked(row);
    const rebased = rebaseContents(savedContents, contents, contentsOf(row), reportLocked(row));
    await accept(row, keyLocked ? next : mergedHeader, rebased.contents);
    setHeaderRestored(keyLocked && (!!incomplete || !sameContent(mergedHeader, next)));
    if (keyLocked) setIncomplete(null);
    setRestoredLines(rebased.restored);
    setMergeConflicts(rebased.conflicts);
  }

  async function removePhoto(lineId: string, photoId: string) {
    if (locked || busy || failure?.stale) return;
    const line = saved?.lines.find(line => line.id === lineId);
    if (!saved || !line?.photos.some(photo => photo.id === photoId)) {
      forget(photoId);
      setContents(current => ({ ...current, lines: current.lines.map(item => item.id === lineId
        ? { ...item, photos: item.photos.filter(photo => photo.id !== photoId) } : item) }));
      return;
    }
    setBusy('photo');
    try {
      const row = await removeWorkPhoto(saved, lineId, photoId, changeId(`photo/${saved.version}/${photoId}`));
      forget(photoId);
      await adoptFresh(row);
    } catch (error) {
      setFailure(reportFailure(error, 'Не удалось удалить фото.'));
    } finally { setBusy(undefined); }
  }

  async function reread() {
    if (!saved || busy) return;
    setBusy('read');
    try {
      const row = await cache.fetchQuery({ ...reportQuery(mode, saved.id), staleTime: 0, retry: false });
      await adoptFresh(row);
    } catch (error) { setFailure({ text: reportFailure(error, 'Не удалось перечитать отчёт.').text, stale: true }); }
    finally { setBusy(undefined); }
  }

  return <div className="h-full min-w-0 space-y-4 overflow-auto px-4 py-4 md:px-6">
    <LeaveGuard active={dirty || !!busy || pendingFiles} save={!locked && dirty ? () => save(false) : undefined} saving={!!busy}
      saveBlocked={formProblem ?? (failure?.stale ? 'Сначала перечитайте отчёт.' : undefined)}
      message={pendingFiles && dirty
        ? 'Сохранение запишет правки и список фото. Недоставленные файлы можно будет выбрать заново. При уходе без сохранения правки и новые объявления фото будут потеряны.'
        : pendingFiles ? 'Часть файлов ещё не доставлена. Если уйти, их придётся выбрать заново.'
        : 'Сохранить изменения отчёта перед уходом?'} />
    <PageHeader title={saved ? `${formatDatePrecise(saved.date)} · ${reportShifts[saved.shift]}` : 'Новый отчёт'}
      subtitle={mode === 'entry' ? `Вводит ${saved?.enteredBy?.name ?? user?.displayName ?? 'менеджер'} за сотрудника: ${saved?.employeeName ?? employee?.name ?? ''}`
        : saved?.constructionName}
      icon={<IconButton icon={ArrowBack} label="К отчётам" onClick={() => go(backTo(mode))} />} />
    {onPickAnother && !saved && <Button variant="text" onClick={() => goBackToEmployee()}>Выбрать другого сотрудника</Button>}
    {saved && <p className="text-body-medium text-on-surface-variant">
      {reportStatuses[saved.status]}{automaticSubmission(saved) && ` · ${automaticSubmission(saved)}`}
    </p>}
    {saved?.returnComment && <Banner tone="warning">{saved.returnComment}</Banner>}
    {saved?.periodClosed && <Banner tone="info">Период закрыт.</Banner>}
    {headerRestored && <Banner tone="warning">
      Стройка, дата и смена восстановлены из отчёта, поскольку его шапка уже закрыта для правки. Введённые правки строк и часов остались в форме.
    </Banner>}
    {mergeConflicts.length > 0 && <Banner tone="warning">
      <ul className="space-y-1">{mergeConflicts.map((conflict, index) => <li key={index}>{conflict}</li>)}</ul>
    </Banner>}
    {restoredLines.length > 0 && <Banner tone="warning">
      У строк {restoredLines.join(', ')} появились материалы, участники или фото. Проверьте строки и подтвердите удаление заново.
    </Banner>}
    {saved && !initial && dirty && <Banner tone="info">Черновик уже создан. Новые значения ещё не сохранены.</Banner>}
    {saved?.status === 'submitted' && !saved.periodClosed && <Banner tone="info">
      Изменения строк будут видны менеджеру как правка после подачи.
    </Banner>}
    {failure && <Banner tone="error" actions={failure.existingId
      ? [{ label: 'Открыть', onClick: () => go(`${root(mode)}/${failure.existingId}`) }]
      : failure.stale ? [{ label: 'Перечитать', onClick: () => void reread(), loading: busy === 'read' }] : []}>{failure.text}</Banner>}
    {!!failure?.requiredMissing?.length && <div className="flex flex-wrap gap-1">
      {failure.requiredMissing.map(field => <Button key={`${field.lineId}/${field.field}`} variant="text"
        onClick={() => setErrorFocus({ ...field })}>Строка {field.line} · {requiredFieldTitle[field.field]}</Button>)}
    </div>}
    {sites.isError && <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void sites.refetch() }]}>
      {apiError(sites.error, 'Не удалось загрузить стройки на дату смены.')}
    </Banner>}
    {reason && <Banner tone="info">{reason}</Banner>}
    <Card title="Стройка и смена">
      <div className="grid items-start gap-3 md:grid-cols-3">
        <SitePicker sites={selectable} boundary={sites.data?.boundary.text} value={header.constructionId}
          disabled={headerLocked || !!busy || sites.isPending}
          onChange={id => {
            const site = choices.find(item => item.id === id);
            if (site) setHeader(!saved && header.date === baseline.date && header.shift === baseline.shift
              ? suggestionOf(site, requestedDate) : { ...header, constructionId: id });
          }} />
        <ShiftDateHeader key={headerLocked ? 'locked' : 'editable'} date={header.date} shift={header.shift} today={today} locked={headerLocked} busy={!!busy}
          lockReason={saved?.status === 'submitted' && !saved.periodClosed ? 'Смена меняется только после возврата' : undefined}
          onDate={date => setHeader(old => ({ ...old, date }))} onShift={shift => setHeader(old => ({ ...old, shift }))}
          onIncomplete={setIncomplete} />
      </div>
      <TextField containerClassName="max-w-xs" label="Часы за смену" inputMode="decimal" value={contents.hours}
        readOnly={locked} disabled={!!busy} onChange={event => setContents(old => ({ ...old, hours: event.target.value }))} />
    </Card>
    <ReportLines lines={contents.lines} scope={{ siteId: header.constructionId, date: header.date, mode,
      reportId: saved?.constructionId === header.constructionId ? saved?.id : undefined, reportVersion: saved?.version }}
      siteReady={knownSite && !!header.date && !incomplete} disabled={locked || !!busy}
      savedLines={saved?.lines} photos={{ uploads, locked, closed: !!saved?.periodClosed,
        busy: !!busy || !!failure?.stale, remove: (lineId, photoId) => void removePhoto(lineId, photoId) }}
      refreshWorkTypes={saved?.status === 'returned'} errors={failure?.requiredMissing} focus={errorFocus}
      onChange={change => setContents(old => ({ ...old, lines: change(old.lines) }))} />
    {!locked && <>
      {formProblem && !incomplete && <p className="text-body-small text-on-surface-variant">{formProblem}</p>}
      <div className="flex flex-wrap justify-end gap-2">
        <Button variant={saved ? 'outlined' : 'filled'} loading={busy === 'save'}
          disabled={!!busy || !!formProblem || failure?.stale || (!!saved && !dirty)} onClick={() => void save(true)}>
          {saved ? 'Сохранить' : 'Создать черновик'}
        </Button>
        {saved && !headerLocked && <Button variant="filled" loading={busy === 'submit'}
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
