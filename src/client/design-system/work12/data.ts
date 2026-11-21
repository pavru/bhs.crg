import { AxiosError, type InternalAxiosRequestConfig } from 'axios';
import { apiClient } from '@/shared/api/client';
import type { Assignment, AssignmentsPage, BulkAssign } from '@/shared/api/workAssignments';
import type { AssignmentCandidate } from '@/shared/api/work';
import { installStoryApi, mockApi } from '../story-api';

export const TODAY = '2026-11-22';
export const SITE = 'ЖК «Северный парк» · Корпус 1';
export const scenarios = [
  { value: 'normal', label: 'Рабочая бригада' }, { value: 'readonly', label: 'Без права назначать' },
  { value: 'empty', label: 'Пока нет назначений' }, { value: 'missing', label: 'Нет справочника' },
  { value: 'conflict', label: '409 при правке периода' }, { value: 'refused', label: '422 при назначении пачки' },
];
const people = [
  ['Орлов Алексей Сергеевич', '0012'], ['Белов Дмитрий Игоревич', '0048'],
  ['Волков Сергей Петрович', '0073'], ['Иванов Николай Андреевич', '0081'],
  ['Козлов Павел Олегович', '0092'], ['Морозов Илья Викторович', '0104'],
  ['Соколов Артём Денисович', '0116'], ['Фёдоров Максим Ильич', '0128'],
];
export const constructionId = 'work12-demo';
export function seedRows(): Assignment[] {
  return people.slice(0, 6).map(([employeeName, personnelNumber], i) => ({
    id: `assignment-${i}`, employeeId: `employee-${i}`, employeeName, personnelNumber, constructionId,
    kind: i === 0 ? 'manager' : 'installer', dateFrom: i === 0 ? '2026-09-01' : i === 4 ? '2026-12-01' : '2026-11-01',
    dateTo: i === 0 ? null : i === 4 ? '2026-12-31' : i === 5 ? '2026-11-15' : '2026-11-30',
    active: i < 4, version: '1', canChange: true, changeReason: null,
    canRemove: i !== 0, removeReason: i === 0 ? 'На стройке не останется менеджера.' : null,
  }));
}

/** In-memory responses for a visual experiment. No app server or real employees are used. */
export function installWork12Api(scenario: string) {
  installStoryApi();
  let items = scenario === 'empty' ? [] : seedRows();
  let conflict = scenario === 'conflict';
  let sequence = 20;
  const canAssign = scenario !== 'readonly';
  const page = (): AssignmentsPage => ({
    construction: { id: constructionId, name: SITE, timeZoneId: 'Asia/Vladivostok', timeZoneInherited: true,
      today: TODAY, canAssign, reason: canAssign ? null : 'Вы не менеджер этой стройки. Назначения доступны только для просмотра.' },
    items: structuredClone(items),
  });
  const fail = (config: InternalAxiosRequestConfig, status: number, data: object): never => {
    throw new AxiosError('Демонстрационный отказ', AxiosError.ERR_BAD_RESPONSE, config, undefined,
      { config, status, statusText: 'Demo refusal', headers: {}, data });
  };
  apiClient.defaults.adapter = async config => {
    const path = config.url ?? '';
    const method = config.method ?? 'get';
    const body = typeof config.data === 'string' ? JSON.parse(config.data) : config.data;
    const response = (data: unknown) => ({ config, status: 200, statusText: 'OK', headers: {}, data });
    if (!path.startsWith('/work/assignments')) return response(mockApi(path, method));
    if (scenario === 'missing') fail(config, 409, { code: 'directory-missing', error: 'Справочник «Сотрудник» не заведён.' });
    if (method !== 'get' && !canAssign) fail(config, 403, { error: 'Вы не менеджер этой стройки.' });
    if (path.endsWith('/candidates')) {
      const p = config.params ?? {};
      const candidates: AssignmentCandidate[] = people.map(([name, personnelNumber], i) => {
        const assigned = items.find(a => a.employeeId === `employee-${i}` && a.kind === p.kind &&
          (!a.dateTo || a.dateTo >= p.dateFrom) && (!p.dateTo || a.dateFrom <= p.dateTo));
        return { employeeId: `employee-${i}`, name, personnelNumber, eligible: i !== 5 && !assigned,
          reason: i === 5 ? 'Уволен с 16.11.2026' : assigned ? 'Уже назначен на пересекающийся период' : null,
          assigned: assigned ? { id: assigned.id, dateFrom: assigned.dateFrom, dateTo: assigned.dateTo } : null,
          account: { state: i === 6 ? 'none' : 'linked' } };
      });
      return response({ items: candidates.filter(c => `${c.name} ${c.personnelNumber}`.toLowerCase().includes((p.q ?? '').toLowerCase())), inArchive: 1, limit: 200, more: false });
    }
    if (method === 'get') return response(page());
    if (path.endsWith('/bulk')) {
      const b = body as BulkAssign;
      if (scenario === 'refused') {
        const id = b.employeeIds[0];
        fail(config, 422, { error: 'Ни одно назначение не заведено. Проверьте причины у сотрудников.',
          refused: [{ employeeId: id, name: people[Number(id.split('-')[1])]?.[0], reason: 'Период работы изменился. Сотрудник доступен только до 25.11.2026.' }] });
      }
      const added: Assignment[] = b.employeeIds.map(id => {
        const person = people[Number(id.split('-')[1])];
        return { id: `assignment-${sequence++}`, employeeId: id, employeeName: person[0], personnelNumber: person[1],
          constructionId, kind: b.kind, dateFrom: b.dateFrom, dateTo: b.dateTo,
          active: b.dateFrom <= TODAY && (!b.dateTo || b.dateTo >= TODAY), version: '1',
          canChange: true, changeReason: null, canRemove: true, removeReason: null };
      });
      items = [...items, ...added]; return response({ items: added });
    }
    const row = items.find(a => path.endsWith('/' + a.id));
    if (!row) return fail(config, 404, { error: 'Назначение тем временем сняли.' });
    if (String(config.headers.get('If-Match')) !== row.version) fail(config, 409, { error: 'Назначение тем временем изменили — перечитайте.' });
    if (method === 'delete') {
      if (!row.canRemove) fail(config, 409, { error: 'На стройке не останется менеджера.' });
      items = items.filter(a => a.id !== row.id); return response({});
    }
    if (conflict) {
      conflict = false; row.version = String(Number(row.version) + 1);
      fail(config, 409, { error: 'Назначение тем временем изменили. Перечитайте и проверьте период.' });
    }
    Object.assign(row, { dateFrom: body.dateFrom, dateTo: body.dateTo, version: String(Number(row.version) + 1),
      active: body.dateFrom <= TODAY && (!body.dateTo || body.dateTo >= TODAY) });
    return response(structuredClone(row));
  };
}
