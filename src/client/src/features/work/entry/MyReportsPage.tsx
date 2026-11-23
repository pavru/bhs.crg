import { useQuery } from '@tanstack/react-query';
import { entrySitesQuery, useOwnReports, type WorkReport } from '@/shared/api/workReports';
import { formatDatePrecise } from '@/shared/format/format';
import { Banner } from '@/shared/ui/Banner';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { EmptyState } from '@/shared/ui/EmptyState';
import { Icon } from '@/shared/ui/Icon';
import { Assignment, Schedule } from '@/shared/ui/icons';
import { useGuardedNavigate } from '@/shared/ui/NavigationGuard';
import { PageHeader } from '@/shared/ui/PageHeader';
import { PALETTE_ACTIONS } from '@/shared/ui/paletteActions';
import { PrimaryActionButton } from '@/shared/ui/PrimaryActionButton';
import { StatusText } from '@/shared/ui/StatusText';
import { usePaletteIntent } from '@/shared/ui/usePaletteIntent';
import { apiError } from '@/shared/utils/apiError';
import { assignmentReason, automaticSubmission, reportShifts, reportStatuses } from './reportPresentation';

const columns: DataGridColumn<WorkReport>[] = [
  { key: 'date', label: 'Дата и смена', rowHeader: true, cell: row => `${formatDatePrecise(row.date)} · ${reportShifts[row.shift]}` },
  { key: 'constructionName', label: 'Стройка', wrap: true, cell: row => row.constructionName ?? 'Стройка недоступна' },
  { key: 'status', label: 'Статус', cell: row => <StatusText icon={row.status === 'draft' ? Schedule : Assignment}
    tone={row.status === 'returned' ? 'warning' : row.status === 'accepted' ? 'success' : 'neutral'}>
    {reportStatuses[row.status]}
  </StatusText> },
  { key: 'note', label: 'Автоподача или комментарий', wrap: true, cell: row => automaticSubmission(row) ?? row.returnComment ?? '—' },
];

export function MyReportsPage() {
  const go = useGuardedNavigate();
  const sites = useQuery(entrySitesQuery('own'));
  const reason = sites.data?.boundary.reason;
  const reports = useOwnReports(!!sites.data && reason !== 'not-linked' && reason !== 'directory-missing');
  const recommended = sites.data?.items[0]?.suggested;
  const create = () => go('/my-reports/new');
  usePaletteIntent(PALETTE_ACTIONS.workReportNew, create);
  const problem = assignmentReason(reason);

  return <div className="h-full min-w-0 space-y-4 overflow-auto px-4 py-4 md:px-6">
    <PageHeader title="Мои отчёты" subtitle={sites.data?.boundary.text}
      actions={recommended && <PrimaryActionButton action={PALETTE_ACTIONS.workReportNew} label={recommended.label} onClick={create} />} />
    {problem && <Banner tone="info">{problem}</Banner>}
    {sites.isError && <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void sites.refetch() }]}>
      {apiError(sites.error, 'Не удалось загрузить стройки.')}
    </Banner>}
    {reports.isError && <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void reports.refetch() }]}>
      {apiError(reports.error, 'Не удалось загрузить отчёты.')}
    </Banner>}
    {(sites.isPending || reports.isLoading) && <p role="status" className="text-body-medium">Загрузка отчётов…</p>}
    {reports.data && (reports.data.length > 0
      ? <DataGrid rows={reports.data} columns={columns} rowKey={row => row.id}
        onRowOpen={row => go(`/my-reports/${row.id}`)} density="compact" />
      : !problem && <EmptyState icon={<Icon icon={Assignment} size={24} />} title="У вас пока нет отчётов" />)}
  </div>;
}
