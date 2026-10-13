// @vitest-environment jsdom
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router';
import { CommandPalette } from './CommandPalette';
import { ConfirmDialog } from './ConfirmDialog';
import { Modal } from './Modal';
import { heldDialog } from './modalDismissal';
import { useLeaveGuard } from './NavigationGuard';
import { NavigationGuardProvider } from './NavigationGuardProvider';

/**
 * Командная палитра и охрана несохранённых правок (issue #1238).
 *
 * Палитра звала `navigate` напрямую и открывалась поверх чего угодно: счёт с правками терялся за два
 * нажатия (Ctrl+K, Enter), диалог с правками — так же, вместе со страницей.
 */

const logout = vi.fn();
vi.mock('@/shared/hooks/useAuth', () => ({ useAuth: () => ({ logout }) }));
vi.mock('@/shared/ui/themeContext', () => ({ useTheme: () => ({ setTheme: vi.fn() }) }));
vi.mock('@/shared/api/access', () => ({ useAccess: () => ({ data: undefined }), NO_ACCESS: {} }));
vi.mock('./navAccess', () => ({ visibleNav: (items: unknown[]) => items }));

afterEach(() => { cleanup(); vi.clearAllMocks(); });

/** Страница с несохранёнными правками: запоминает, куда её просили отпустить. */
function Page({ dirty, asked }: { dirty: boolean; asked: (proceed: () => void) => void }) {
  useLeaveGuard(dirty, asked);
  return <output aria-label="адрес">{useLocation().pathname}</output>;
}

function shown(dirty: boolean) {
  const asked = vi.fn<(proceed: () => void) => void>();
  function Palette() {
    const [open, setOpen] = useState(true);
    return <CommandPalette open={open} onOpenChange={setOpen} />;
  }
  render(
    <MemoryRouter initialEntries={['/invoices']}>
      <NavigationGuardProvider>
        <Page dirty={dirty} asked={asked} />
        <Palette />
      </NavigationGuardProvider>
    </MemoryRouter>,
  );
  return asked;
}

const address = () => screen.getByLabelText('адрес').textContent;
function choose(command: string) {
  const search = screen.getByRole('textbox');
  fireEvent.change(search, { target: { value: command } });
  fireEvent.keyDown(search, { key: 'Enter' });
}

describe('команда палитры при несохранённых правках', () => {
  it('без правок переходит сразу', () => {
    const asked = shown(false);
    choose('Учётный период');
    expect(address()).toBe('/periods');
    expect(asked).not.toHaveBeenCalled();
  });

  it('с правками не переходит, пока страница не отпустит', () => {
    const asked = shown(true);
    choose('Учётный период');

    expect(address()).toBe('/invoices');
    expect(asked).toHaveBeenCalledTimes(1);
    // Палитра закрылась: вопрос страницы не должен открываться под ней.
    expect(screen.queryByRole('dialog')).toBeNull();

    // Страница решила отпустить — переход тот самый, что был выбран.
    act(() => asked.mock.calls[0][0]());
    expect(address()).toBe('/periods');
  });

  it('выход из учётной записи ждёт того же', () => {
    const asked = shown(true);
    choose('Выйти');
    expect(logout).not.toHaveBeenCalled();
    act(() => asked.mock.calls[0][0]());
    expect(logout).toHaveBeenCalledTimes(1);
  });

  it('без правок выход исполняется сразу', () => {
    shown(false);
    choose('Выйти');
    expect(logout).toHaveBeenCalledTimes(1);
  });
});

describe('окно, поверх которого палитра не открывается', () => {
  const held = () => heldDialog()?.dataset.modalHeld ?? null;

  it('обычное и лёгкое окно себя не называют', () => {
    render(<Modal open onOpenChange={() => {}} title="Окно">текст</Modal>);
    expect(held()).toBeNull();
    cleanup();
    render(<Modal open onOpenChange={() => {}} title="Окно" nothingToLose>текст</Modal>);
    expect(held()).toBeNull();
  });

  it.each([
    ['dirty', { isDirty: true }],
    ['busy', { busy: true }],
    ['result', { acknowledge: 'Понятно' }],
  ] as const)('окно в состоянии %s называет себя', (state, props) => {
    render(<Modal open onOpenChange={() => {}} title="Окно" {...props}>текст</Modal>);
    expect(held()).toBe(state);
  });

  it('окно, в котором набирали, — тоже', () => {
    render(<Modal open onOpenChange={() => {}} title="Окно"><input aria-label="поле" /></Modal>);
    expect(held()).toBeNull();
    fireEvent.change(screen.getByLabelText('поле'), { target: { value: 'набрано' } });
    expect(held()).toBe('touched');
  });

  it('подтверждение: вопрос — нет, отказ — да', () => {
    render(<ConfirmDialog open onOpenChange={() => {}} title="Удалить?" confirmLabel="Удалить запись" onConfirm={() => {}} />);
    expect(held()).toBeNull();
    cleanup();
    render(<ConfirmDialog open onOpenChange={() => {}} title="Удалить?" confirmLabel="Удалить запись"
      blocked="Период закрыт." onConfirm={() => {}} />);
    expect(held()).toBe('result');
  });
});
