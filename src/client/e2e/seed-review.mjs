// Синтетические отчёты очереди для Work-21. Справочник и назначения уже подготовил seed-employees.
import { randomUUID } from 'node:crypto';

export const REVIEW_READER_EMAIL = process.env.SMOKE_READER_EMAIL || 'ruk@bhs.local';
export const REVIEW_RETURN_RECIPIENT_EMAIL = 'work-review-recipient@bhs.local';

export const REVIEW_REPORTS = [
  { id: '187f44ed-6fc5-4234-9b24-e270df33a052', name: 'Крылов Максим Игоревич (посев)', number: 'E2E-21', shift: 'day' },
  { id: '2e874522-0737-4629-85ed-82904fd8a092', name: 'Сафонова Ирина Олеговна (посев)', number: 'E2E-22', shift: 'night' },
];

async function seedLateReport(api, body) {
  const invite = await api('POST', `/work/devices/employees/${body.employeeId}/invitations`,
    { serverUrl: 'https://field.example.test', transport: 'https-system' });
  const { accessToken } = await api('POST', '/field/join', { invitationCode: invite.invitationCode });
  const { minimumSenderVersionCode } = await api('GET', '/field/info');
  const headers = { Authorization: `Bearer ${accessToken}`, 'X-Field-Version-Code': String(minimumSenderVersionCode) };
  // Два дня назад окно уже закрыто в любом поддерживаемом поясе. Подача на часах телефона произошла до его конца.
  const date = new Date(Date.parse(body.date) - 2 * 86_400_000).toISOString().slice(0, 10);
  const draft = await api('POST', '/field/v1/reports', {
    ...body, date, changeId: randomUUID(), deviceTime: `${date}T05:00:00Z`,
  }, headers);
  const path = `/field/v1/reports/${draft.id}`;
  const submit = { changeId: randomUUID(), deviceTime: `${date}T04:00:00Z` };
  const send = version => api('POST', `${path}/submit`, submit, { ...headers, 'If-Match': version });
  return send(draft.version).catch(async error => {
    if (!/→ 409 .*version-conflict/.test(error.message)) throw error;
    // Если такт автоподачи обогнал телефон, перечитать версию и доставить то же действие.
    return send((await api('GET', path, undefined, headers)).version);
  });
}

export async function seedReview({ api, ensureUser, constructionId }) {
  await ensureUser(REVIEW_READER_EMAIL, 'Роман Руководителев', 'Executive');
  await ensureUser(REVIEW_RETURN_RECIPIENT_EMAIL, REVIEW_REPORTS[1].name, 'Installer');
  const recipient = (await api('GET', '/users')).find(user => user.email === REVIEW_RETURN_RECIPIENT_EMAIL);
  if (!recipient) throw new Error('Не найдена учётная запись получателя уведомления Work-30.');
  const { construction } = await api('GET', `/work/assignments?constructionId=${constructionId}`);
  if (!construction.today) throw new Error('У стройки посева нет текущей даты для отчётов Work-21.');
  const employees = await api('GET', '/employees');
  for (const report of REVIEW_REPORTS) {
    const employee = employees.find(item => item.displayName === report.name)
      ?? await api('POST', '/employees', { displayName: report.name, data: JSON.stringify({ ТабельныйНомер: report.number }) });
    if (report.id === REVIEW_REPORTS[1].id && employee.account?.userId !== recipient.id) {
      await api('PUT', `/employees/${employee.id}/account`, { userId: recipient.id }, { 'If-Match': employee.version });
    }
    const path = `/work/entry/reports/${report.id}`;
    const body = { id: report.id, employeeId: employee.id, constructionId, date: construction.today, shift: report.shift, hours: 8, lines: [] };
    let current = await api('GET', path).catch(error => {
      if (!/ → 404 /.test(error.message)) throw error;
      return report.shift === 'day' ? seedLateReport(api, body) : api('POST', '/work/entry/reports', body);
    });
    if (current.status === 'draft') current = await api('POST', `${path}/submit`, {}, { 'If-Match': current.version });
    if (current.status !== 'submitted') throw new Error(`Отчёт посева ${report.name} имеет статус ${current.status} вместо submitted.`);
    // Оба сотрудника без назначения на дату. Ночному отчёту добавляется вторая пометка серверной правкой.
    if (report.shift === 'night' && !current.marks.some(mark => mark.code === 'manager-edit')) {
      await api('PUT', path, { ...body, date: current.date, hours: 9, changeId: randomUUID() }, { 'If-Match': current.version });
    }
  }
  console.log('  + два поданных отчёта Work-21 с пометками');
}
