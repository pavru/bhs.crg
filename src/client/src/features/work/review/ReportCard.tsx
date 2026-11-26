import { useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useParams, useSearchParams } from 'react-router';
import { REVIEW_CARD_KEY, REVIEW_JOURNAL_KEY, REVIEW_QUEUE_KEY, useReviewCard, type ReviewFullCard } from '@/shared/api/workReview';
import { CREW_GROUPS_KEY } from '@/shared/api/workCrew';
import { invalidateWorkProgress } from '@/shared/api/workReports';
import { WORK_SUMMARY_KEY } from '@/shared/api/workSummary';
import { formatDate, formatDateTime, formatDayTime, formatNumber } from '@/shared/format/format';
import { Banner } from '@/shared/ui/Banner';
import { Button, IconButton } from '@/shared/ui/Button';
import { useDocumentTitle } from '@/shared/ui/DocumentTitle';
import { EmptyState } from '@/shared/ui/EmptyState';
import { Icon } from '@/shared/ui/Icon';
import { Assignment, MoreVert } from '@/shared/ui/icons';
import { Menu, MenuItem } from '@/shared/ui/Menu';
import { useGuardedNavigate } from '@/shared/ui/NavigationGuard';
import { PageHeader } from '@/shared/ui/PageHeader';
import { StatusText } from '@/shared/ui/StatusText';
import { apiError } from '@/shared/utils/apiError';
import { ACCEPT_MARK_WARNING, AcceptDialog } from './AcceptDialog';
import { ReportJournal } from './ReportJournal';
import { ReportLineEdit } from './ReportLineEdit';
import { ReportLines } from './ReportLines';
import { ReportVersionBar } from './ReportVersionBar';
import { ReturnDialog } from './ReturnDialog';
import { reviewMarkStyle } from './reviewMarks';

const notFound = 'Отчёт не найден: его нет, или он вне ваших строек';

export function ReportCard() {
  useDocumentTitle('Карточка отчёта');
  const { reportId } = useParams();
  const id = reportId ?? '';
  const [params, setParams] = useSearchParams();
  const selected = params.get('number');
  const number = selected === null ? undefined : Number(selected);
  const card = useReviewCard(id, number);
  const data = card.data;
  const navigate = useGuardedNavigate();
  const queryClient = useQueryClient();
  const back = new URLSearchParams(params);
  back.delete('number');

  function choose(next: number | undefined) {
    const update = new URLSearchParams(params);
    if (next === undefined) update.delete('number'); else update.set('number', String(next));
    setParams(update, { replace: true });
  }

  function updateAfterWrite(constructionId: string) {
    void queryClient.invalidateQueries({ queryKey: [...REVIEW_CARD_KEY, id] });
    void queryClient.invalidateQueries({ queryKey: [...REVIEW_JOURNAL_KEY, id] });
    void queryClient.invalidateQueries({ queryKey: REVIEW_QUEUE_KEY });
    void queryClient.invalidateQueries({ queryKey: CREW_GROUPS_KEY });
    void queryClient.invalidateQueries({ queryKey: WORK_SUMMARY_KEY });
    void invalidateWorkProgress(queryClient, constructionId);
  }

  const missing = (card.error as { response?: { status?: number } } | null)?.response?.status === 404;
  return <div className="h-full min-w-0 overflow-auto px-6 py-4 space-y-4">
    <Button variant="text" onClick={() => navigate(`/work-review${back.size ? `?${back}` : ''}`)}>
      {back.get('tab') === 'crew' ? 'К сверке бригад' : 'К очереди приёмки'}
    </Button>
    {missing ? <EmptyState icon={<Icon icon={Assignment} size={24} />} title={notFound} />
      : card.isPending ? <p role="status" className="text-body-medium">Загрузка отчёта…</p>
        : card.isError ? <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void card.refetch() }]}>
          {apiError(card.error, 'Не удалось загрузить отчёт.')}
        </Banner> : data?.status === 'draft' ? <>
          <PageHeader title={data.employeeName} subtitle={data.constructionName} />
          <StatusText>Черновик</StatusText>
          {data.autoSubmitAt && <p className="text-body-medium">Автоподача {data.timeZoneId
            ? formatDayTime(data.autoSubmitAt, data.timeZoneId) : formatDateTime(data.autoSubmitAt)}
            {data.timeZoneId && ' по времени стройки'}</p>}
        </> : data && <ReportContent key={`${id}:${data.viewedVersion}`} card={data} onVersion={choose}
          onStored={() => updateAfterWrite(data.report.constructionId)} onReread={async () => {
            await card.refetch({ throwOnError: true });
            void queryClient.invalidateQueries({ queryKey: [...REVIEW_JOURNAL_KEY, id] });
            void queryClient.invalidateQueries({ queryKey: REVIEW_QUEUE_KEY });
          }} />}
  </div>;
}

const statusLabel = { submitted: 'Подан', accepted: 'Принят', returned: 'Возвращён на правку' } as const;

function ReportContent({ card, onVersion, onStored, onReread }: {
  card: ReviewFullCard; onVersion: (version: number | undefined) => void; onStored: () => void; onReread: () => Promise<unknown>;
}) {
  const [decision, setDecision] = useState<'accept' | 'return' | null>(null);
  const [editing, setEditing] = useState(false);
  const { report, actions } = card;
  const canAccept = actions?.includes('accept');
  const canReturn = actions?.includes('return');
  const canEdit = actions?.includes('edit');
  const shift = `${card.constructionName} · ${formatDate(report.date)} · ${report.shift === 'night' ? '☾ ночь' : 'день'}`;
  const dialog = { id: card.id, version: report.version, employeeName: card.employeeName,
    shiftLabel: shift, onClose: () => setDecision(null), onDecided: onStored, onReread };

  return <>
    <PageHeader title={card.employeeName} subtitle={`${shift}${report.hours == null ? '' : ` · ${formatNumber(report.hours)} ч`}`}
      actions={!editing && (canAccept || canReturn || canEdit) ? <div className="space-y-1">
        <div className="flex flex-wrap items-center justify-end gap-2">
          {canReturn && <Button variant="outlined" onClick={() => setDecision('return')}>Вернуть</Button>}
          {canAccept && <Button variant="filled" onClick={() => setDecision('accept')}>Принять</Button>}
          {canEdit && <Menu align="end" trigger={<IconButton icon={MoreVert} label="Действия отчёта" />}>
            <MenuItem onSelect={() => setEditing(true)}>Править строки</MenuItem>
          </Menu>}
        </div>
        {canAccept && report.acceptWouldMark && <StatusText tone="warning">{ACCEPT_MARK_WARNING}</StatusText>}
      </div> : undefined} />
    <div className="flex flex-wrap items-baseline gap-x-4 gap-y-1">
      <StatusText tone={card.status === 'accepted' ? 'success' : 'neutral'}>{statusLabel[card.status]}</StatusText>
      {report.marks.map(mark => <StatusText key={mark.code} {...reviewMarkStyle(mark.code)}>{mark.text}</StatusText>)}
    </div>
    {report.note && <p className="whitespace-pre-wrap text-body-medium">{report.note}</p>}
    {!editing && <ReportVersionBar card={card} onChange={onVersion} />}
    <div className="grid min-w-0 gap-6 lg:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">
      <section className="min-w-0 space-y-3" aria-label="Работы отчёта">
        <h2 className="text-title-medium">Работы</h2>
        {editing ? <ReportLineEdit card={card} onClose={() => setEditing(false)} onSaved={() => { setEditing(false); onStored(); }} />
          : <ReportLines card={card} />}
      </section>
      <ReportJournal id={card.id} />
    </div>
    {decision === 'accept' && <AcceptDialog {...dialog} acceptWouldMark={report.acceptWouldMark} />}
    {decision === 'return' && <ReturnDialog {...dialog} />}
  </>;
}
