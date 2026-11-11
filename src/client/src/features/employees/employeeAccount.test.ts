import { describe, expect, it } from 'vitest';
import type { Employee, EmployeeAccount, LinkRowResult } from '@/shared/api/employees';
import {
  accountView, batchSummary, candidateBlocked, filterCounts, inFilter, matchReasonText, matchesQuery,
  personnelNumber, requisiteRows, rowOutcomeText, rowStateText, ruDate, surnameOf,
} from './employeeAccount';
import type { SchemaField } from '@/shared/api/schema';

const linked: EmployeeAccount = { state: 'linked', name: 'Пробнов Пётр', login: 'p.probnov@test.local', dismissedOn: null };
const none: EmployeeAccount = { state: 'none', name: null, login: null, dismissedOn: null };
const deleted: EmployeeAccount = { state: 'deleted', name: 'Удалёнов У.', login: null, dismissedOn: null };

function employee(account: EmployeeAccount, archived = false, displayName = 'Пробнов Пётр Петрович'): Employee {
  return {
    id: displayName, displayName, aliases: [], compositeTypeId: 't', data: {}, scope: 'System', scopeId: null,
    createdAt: '', updatedAt: '', archived, version: '1', account,
  } as Employee;
}

describe('блок «Учётная запись» строится по account.state', () => {
  it('связан — имя и логин', () => {
    expect(accountView(linked)).toMatchObject({ headline: 'Связан: Пробнов Пётр', supporting: 'p.probnov@test.local', deleted: false });
  });

  it('не связан — обычное положение, без тревоги', () => {
    expect(accountView(none)).toMatchObject({ headline: 'Не связан', deleted: false, dismissed: null });
  });

  it('удалена — имя снимком и плашка', () => {
    expect(accountView(deleted)).toMatchObject({ headline: 'Учётная запись удалена: Удалёнов У.', deleted: true });
  });

  it('«уволен, но связан» — только по dismissedOn от сервера', () => {
    expect(accountView({ ...linked, dismissedOn: '2026-11-03' }).dismissed).toContain('Уволен с 03.11.2026');
    expect(accountView(linked).dismissed).toBeNull();
    // У несвязанного пометки нет: сервер её и не пришлёт, а экран не выдумывает.
    expect(accountView({ ...none, dismissedOn: '2026-11-03' }).dismissed).toBeNull();
  });

  it('скрытый текст строки — словами состояния, у «не связан» — ничего', () => {
    expect(rowStateText(linked)).toBe('связан с Пробнов Пётр');
    expect(rowStateText(deleted)).toBe('учётная запись удалена');
    expect(rowStateText(none)).toBeNull();
  });
});

describe('отборы списка', () => {
  const list = [employee(linked), employee(none), employee(none), employee(deleted), employee(none, true)];

  it('«Все» — без архива, архив — свой отбор', () => {
    expect(filterCounts(list)).toEqual({ all: 4, unlinked: 2, deleted: 1, archived: 1 });
    expect(inFilter(list[4], 'unlinked')).toBe(false);
  });

  it('поиск — по имени и псевдонимам, без учёта регистра', () => {
    const e = { ...employee(none), aliases: ['Петя'] };
    expect(matchesQuery(e, 'пробнов')).toBe(true);
    expect(matchesQuery(e, 'ПЕТЯ')).toBe(true);
    expect(matchesQuery(e, 'Иванов')).toBe(false);
  });
});

describe('кандидаты и пачка', () => {
  it('занятая другим не выбирается и называет, с кем связана', () => {
    const c = { userId: 'u', name: 'Белова', login: 'b', linkedTo: { id: 'e2', name: 'Белова И. К.' } };
    expect(candidateBlocked(c, 'e1')).toBe('уже связана с Белова И. К.');
    expect(candidateBlocked(c, 'e2')).toBeNull();
    expect(candidateBlocked({ ...c, linkedTo: null }, 'e1')).toBeNull();
  });

  it('поиск окна заполнен фамилией', () => {
    expect(surnameOf('  Белов Андрей Сергеевич')).toBe('Белов');
  });

  it('итог пачки — «Связано N из M», причина отказа — словами сервера', () => {
    expect(batchSummary({ linked: 9, total: 11, results: [] })).toBe('Связано 9 из 11');
    const row = (outcome: string, error: string | null): LinkRowResult =>
      ({ employeeId: 'e', outcome, error, linkedTo: null, accountName: null, version: null });
    expect(rowOutcomeText(row('linked', null))).toBe('Связан');
    expect(rowOutcomeText(row('taken', 'Учётная запись уже связана с сотрудником «Карпова Е. Н.».')))
      .toBe('Не связан: Учётная запись уже связана с сотрудником «Карпова Е. Н.».');
  });

  it('причина без предложения', () => {
    expect(matchReasonText('no-match')).toBe('нет совпадения');
    expect(matchReasonText('ambiguous')).toContain('несколько совпадений');
    expect(matchReasonText(null)).toBeNull();
  });

  it('дата — по-русски', () => {
    expect(ruDate('2026-01-09')).toBe('09.01.2026');
  });
});

describe('реквизиты карточки', () => {
  const f = (key: string, type: SchemaField['type'], tags?: string[]): SchemaField => ({ key, title: key, type, required: false, tags });
  const fields = [
    f('Фамилия', 'string'), f('ТабельныйНомер', 'string', ['identity:1', 'employee.personnelNumber']),
    f('ПринятС', 'date'), f('УчётнаяЗапись', 'string', ['employee.account']), f('Адрес', 'complex'),
  ];

  it('простые поля по порядку схемы; поле связи и составные — нет', () => {
    expect(requisiteRows(fields, { Фамилия: 'Пробнов', ПринятС: '2024-03-12', УчётнаяЗапись: 'guid' }))
      .toEqual([
        { key: 'Фамилия', title: 'Фамилия', text: 'Пробнов' },
        { key: 'ТабельныйНомер', title: 'ТабельныйНомер', text: '—' },
        { key: 'ПринятС', title: 'ПринятС', text: '12.03.2024' },
      ]);
  });

  it('табельный номер — по тэгу, а не по ключу', () => {
    const renamed = [f('Номер', 'string', ['employee.personnelNumber'])];
    expect(personnelNumber(renamed, { Номер: ' 0117 ' })).toBe('0117');
    expect(personnelNumber(renamed, {})).toBeNull();
  });
});
