// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { NavigationGuardProvider } from '@/shared/ui/NavigationGuardProvider';
import { useNavigationGuard } from '@/shared/ui/NavigationGuard';
import { leaveQuestion, useInvoiceLeave, type InvoiceUnsaved } from './InvoiceLeaveGuard';

/**
 * Уход из формы счёта с несохранённым (#1653, `costs-invoice-lines-leave-guard`, [COST-7]): охрана видит строки
 * наравне с реквизитами (1.1), вопрос называет, что не сохранено, и «Сохранить и перейти» пишет всё (1.2, D2 «а»),
 * в исправлении — не обещает записи (1.4, D3).
 */

afterEach(cleanup);

const NONE: InvoiceUnsaved = { header: false, lines: false, correcting: false };

function Form({ unsaved, save }: { unsaved: InvoiceUnsaved; save: () => Promise<boolean> }) {
  const held = unsaved.header || unsaved.lines || unsaved.correcting;
  return <>{useInvoiceLeave({ held, unsaved, saving: false, save })}</>;
}

/** Пункт навигации: уходит через охрану, как ссылки обвязки. */
function NavItem({ onGo }: { onGo: () => void }) {
  const guard = useNavigationGuard();
  return <button type="button" onClick={() => { if (!guard?.attempt(onGo)) onGo(); }}>Счета</button>;
}

function mount(unsaved: InvoiceUnsaved, save: () => Promise<boolean> = vi.fn(async () => true)) {
  const go = vi.fn();
  render(
    <NavigationGuardProvider>
      <Form unsaved={unsaved} save={save} />
      <NavItem onGo={go} />
    </NavigationGuardProvider>,
  );
  return { go, save, leave: () => fireEvent.click(screen.getByRole('button', { name: 'Счета' })) };
}

const dialog = () => screen.queryByRole('dialog', { name: 'Несохранённые изменения' });

describe('охрана ухода видит строки (1.1)', () => {
  it('несохранённые строки — уход спрашивает и не идёт до ответа', () => {
    const { go, leave } = mount({ ...NONE, lines: true });
    leave();
    expect(dialog()).toBeTruthy();
    expect(go).not.toHaveBeenCalled();
  });

  it('всё сохранено — уход без вопроса', () => {
    const { go, leave } = mount(NONE);
    leave();
    expect(dialog()).toBeNull();
    expect(go).toHaveBeenCalledOnce();
  });

  it('перезагрузку вкладки держит вопрос браузера', () => {
    mount({ ...NONE, lines: true });
    const event = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(true);
  });
});

describe('вопрос называет, что не сохранено (1.2)', () => {
  it('строки, реквизиты, оба', () => {
    expect(leaveQuestion({ ...NONE, lines: true })).toBe('Не сохранены строки счёта. Сохранить их перед переходом?');
    expect(leaveQuestion({ ...NONE, header: true })).toBe('Не сохранены реквизиты счёта. Сохранить их перед переходом?');
    expect(leaveQuestion({ ...NONE, header: true, lines: true })).toMatch(/^Не сохранены реквизиты и строки счёта\..*затем строки/);
  });

  it('диалог показывает этот текст', () => {
    const { leave } = mount({ ...NONE, lines: true });
    leave();
    expect(screen.getByText('Не сохранены строки счёта. Сохранить их перед переходом?')).toBeTruthy();
  });

  it('«Сохранить и перейти» пишет и уходит после записи', async () => {
    const { go, save, leave } = mount({ ...NONE, header: true, lines: true });
    leave();
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Сохранить и перейти' })); });
    expect(save).toHaveBeenCalledOnce();
    expect(go).toHaveBeenCalledOnce();
  });

  it('отказ записи уход отменяет', async () => {
    const { go, leave } = mount({ ...NONE, lines: true }, vi.fn(async () => false));
    leave();
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Сохранить и перейти' })); });
    expect(go).not.toHaveBeenCalled();
  });

  it('«Не сохранять» уходит без записи; «Отмена» остаётся', () => {
    const first = mount({ ...NONE, lines: true });
    first.leave();
    fireEvent.click(screen.getByRole('button', { name: 'Отмена' }));
    expect(first.go).not.toHaveBeenCalled();
    expect(dialog()).toBeNull();
    first.leave();
    fireEvent.click(screen.getByRole('button', { name: 'Не сохранять' }));
    expect(first.go).toHaveBeenCalledOnce();
    expect(first.save).not.toHaveBeenCalled();
  });
});

describe('исправление (1.4, D3)', () => {
  it('строки, набранные в исправлении, уход держат; вопрос говорит об исправлении и записи не обещает', async () => {
    const { go, save, leave } = mount({ header: false, lines: true, correcting: true });
    leave();
    expect(screen.getByText(/^Исправление не записано: оно записывается только кнопкой «Проверить исправление…»/)).toBeTruthy();
    const button = screen.getByRole('button', { name: 'Сохранить и перейти' });
    expect(button.hasAttribute('disabled') || button.getAttribute('aria-disabled') === 'true').toBe(true);
    await act(async () => { fireEvent.click(button); });
    expect(save).not.toHaveBeenCalled();
    expect(go).not.toHaveBeenCalled();
  });
});
