import type { EntrySites, ReportStatus, WorkReport } from '@/shared/api/workReports';
import { formatDayTime } from '@/shared/format/format';
import { apiError } from '@/shared/utils/apiError';

export const reportStatuses: Record<ReportStatus, string> = {
  draft: 'Черновик', returned: 'Возвращён', submitted: 'Подан', accepted: 'Принят',
};
export const reportShifts = { day: 'день', night: '☾ ночь' };

export function assignmentReason(reason?: EntrySites['boundary']['reason'], manager = false) {
  if (reason === 'not-linked') return 'Учётная запись не связана с сотрудником. Обратитесь к администратору.';
  if (reason === 'directory-missing') return 'Справочник сотрудников не заведён.';
  if (reason === 'not-assigned') return manager
    ? 'На эту дату у сотрудника нет назначений на стройки в вашем охвате.'
    : 'Вы не назначены ни на одну стройку — обратитесь к менеджеру проекта.';
  return null;
}

export function automaticSubmission(report: WorkReport) {
  if (report.status !== 'draft' || !report.autoSubmitAt) return null;
  if (!report.timeZoneId) return `Автоподача · ${formatDayTime(report.autoSubmitAt, 'UTC')} UTC`;
  const display = formatDayTime(report.autoSubmitAt, report.timeZoneId);
  return `Подастся ${display} по времени стройки`;
}

export function reportFailure(error: unknown, fallback: string) {
  const response = (error as { response?: { status?: number; data?: { existingId?: string } } })?.response;
  const text = apiError(error, fallback);
  if (response?.status === 404) return { text: 'Отчёт не найден: его нет, или он вне ваших строек.' };
  if (response?.status === 409 && response.data?.existingId)
    return { text: 'На эту смену отчёт уже есть.', existingId: response.data.existingId };
  if (response?.status === 409 && text.toLowerCase().includes('тем временем измен'))
    return { text: 'Отчёт тем временем изменили — перечитайте. Ваши изменения остались в форме.', stale: true };
  return { text };
}
