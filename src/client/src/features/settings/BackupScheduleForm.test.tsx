// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import type { BackupScheduleStatus } from '@/shared/api/types';
import { ScheduleForm } from './BackupScheduleForm';

const mutateAsync = vi.fn();
vi.mock('@/shared/api/backup', () => ({ useSaveBackupSchedule: () => ({ mutateAsync, isPending: false }) }));
vi.mock('@/shared/api/companyTimeZone', () => ({ useCompanyTimeZone: () => ({ data: undefined }) }));
vi.mock('@/shared/hooks/useLocale', async importOriginal => ({
  ...(await importOriginal<typeof import('@/shared/hooks/useLocale')>()),
  useLocale: () => ['ru-RU', () => {}, 'idle'],
}));

/** Форма расписания копий (issue #1468, ревью PR #1561): отказы называются здесь, а не сервером. */

const STATUS: BackupScheduleStatus = {
  enabled: true, timeOfDay: '03:00', keepCount: 7,
  lastRunAt: null, lastSuccessAt: null, lastFileName: null, lastError: null, lastErrorAt: null,
  nextRun: { at: '2026-10-22T03:00:00+03:00', due: false, today: '2026-10-21' }, running: false,
};

beforeEach(() => mutateAsync.mockReset().mockResolvedValue(undefined));
afterEach(cleanup);

const seg = (ph: 'ЧЧ' | 'ММ') =>
  within(screen.getByRole('group', { name: 'Время копии' })).getByPlaceholderText(ph) as HTMLInputElement;
const save = () => fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
const clearTime = () => {
  seg('ЧЧ').focus();
  fireEvent.keyDown(seg('ЧЧ'), { key: 'a', ctrlKey: true });
  fireEvent.keyDown(seg('ЧЧ'), { key: 'Delete' });
};

describe('ScheduleForm', () => {
  it('ближайшая копия — словами от сервера', () => {
    render(<ScheduleForm status={STATUS} />);
    expect(screen.getByText('завтра, 03:00')).toBeTruthy();
  });

  it('стёртое обязательное время не уходит на сервер — отказ с причиной', () => {
    render(<ScheduleForm status={STATUS} />);
    clearTime();
    save();
    expect(mutateAsync).not.toHaveBeenCalled();
    expect(screen.getByText('Время копии не задано — расписание не сохранено.')).toBeTruthy();
  });

  it('выключить расписание можно и со стёртым временем — пишется прежнее', () => {
    render(<ScheduleForm status={STATUS} />);
    clearTime();
    fireEvent.click(screen.getByRole('checkbox'));
    save();
    expect(mutateAsync).toHaveBeenCalledWith({ enabled: false, timeOfDay: '03:00', keepCount: 7 });
  });

  it('отказ по времени снимается, когда время исправлено', () => {
    render(<ScheduleForm status={STATUS} />);
    seg('ЧЧ').focus();
    fireEvent.change(seg('ЧЧ'), { target: { value: '25' } });
    save();
    expect(screen.getByText('Такого времени нет — расписание не сохранено.')).toBeTruthy();
    fireEvent.change(seg('ЧЧ'), { target: { value: '04' } });
    expect(screen.queryByText('Такого времени нет — расписание не сохранено.')).toBeNull();
  });

  it('выключили расписание — набранное с ошибкой уходит из поля', () => {
    render(<ScheduleForm status={STATUS} />);
    seg('ЧЧ').focus();
    fireEvent.change(seg('ЧЧ'), { target: { value: '25' } });
    fireEvent.click(screen.getByRole('checkbox'));
    expect(seg('ЧЧ').value).toBe('03');
    expect(seg('ЧЧ').getAttribute('aria-invalid')).toBeNull();
  });
});
