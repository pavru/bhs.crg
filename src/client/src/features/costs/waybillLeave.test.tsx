// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { NavigationGuardProvider } from '@/shared/ui/NavigationGuardProvider';
import { LeaveGuard } from '@/shared/ui/LeaveGuard';
import { waybillLeave, type WaybillLeaveInput } from './waybillLeave';

/**
 * Уход из формы накладной (ревью PR #1817): охрана держит правки и причину исправления, вопрос стоит до конца
 * записи, а в исправлении записи у вопроса нет — оно пишется своей кнопкой.
 */

afterEach(cleanup);

const base: WaybillLeaveInput = { dirty: false, correcting: false, reason: '' };

describe('waybillLeave', () => {
  it('нечего терять — охрана молчит', () => {
    expect(waybillLeave(base).guard).toBe(false);
    expect(waybillLeave({ ...base, correcting: true, reason: '   ' }).guard).toBe(false);
  });

  it('правки — охрана держит, вопрос пишет черновик', () => {
    expect(waybillLeave({ ...base, dirty: true })).toEqual({ guard: true, savable: true });
  });

  it('набранная причина исправления — тоже правка', () => {
    expect(waybillLeave({ ...base, correcting: true, reason: 'опечатка' }).guard).toBe(true);
  });

  it('в исправлении вопрос не пишет и называет, чем оно записывается', () => {
    const leave = waybillLeave({ dirty: true, correcting: true, reason: '' });
    expect(leave.savable).toBe(false);
    expect(leave.question).toContain('«Записать исправление»');
  });
});

/** Форма накладной так и зовёт вопрос: `LeaveGuard` по `waybillLeave`, уход — со страницы списка. */
function mount(input: WaybillLeaveInput, save: () => Promise<boolean>) {
  let ask: ((proceed: () => void) => void) | null = null;
  const go = vi.fn();
  const leave = waybillLeave(input);
  render(
    <NavigationGuardProvider>
      <LeaveGuard active={leave.guard} message={leave.question} save={leave.savable ? save : undefined}
        onAsk={a => { ask = a; }} />
    </NavigationGuardProvider>,
  );
  act(() => { ask!(go); });
  return go;
}

const saveButton = () => screen.queryByRole('button', { name: 'Сохранить и перейти' });

describe('вопрос накладной', () => {
  it('черновик: вопрос стоит, пока идёт запись; отказ уход отменяет', async () => {
    let finish!: (ok: boolean) => void;
    const save = vi.fn(() => new Promise<boolean>(resolve => { finish = resolve; }));
    const go = mount({ ...base, dirty: true }, save);
    fireEvent.click(saveButton()!);
    expect(screen.getByRole('dialog', { name: 'Несохранённые изменения' })).toBeTruthy();
    expect(saveButton()!.hasAttribute('disabled')).toBe(true);
    await act(async () => { finish(false); });
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(go).not.toHaveBeenCalled();
  });

  it('исправление: кнопки записи нет, причина названа текстом', () => {
    const save = vi.fn(async () => true);
    const go = mount({ dirty: true, correcting: true, reason: 'опечатка' }, save);
    expect(saveButton()).toBeNull();
    expect(screen.getByText(/«Записать исправление»/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Не сохранять' }));
    expect(go).toHaveBeenCalledOnce();
    expect(save).not.toHaveBeenCalled();
  });
});
