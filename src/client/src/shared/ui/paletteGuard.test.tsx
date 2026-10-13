// @vitest-environment jsdom
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router';
import { CommandPalette } from './CommandPalette';
import { ConfirmDialog } from './ConfirmDialog';
import { Modal, ModalExit } from './Modal';
import { paletteShown, refusedByDialog } from './modalDismissal';
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
  const search = screen.getByRole('textbox', { hidden: true });
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

describe('окно, которое держит человека', () => {
  /** Состояния всех открытых окон, снизу вверх. */
  const states = () => [...document.querySelectorAll<HTMLElement>('[data-modal-state]')].map(d => d.dataset.modalState);

  it('обычное и лёгкое окно уходу не мешают', () => {
    render(<Modal open onOpenChange={() => {}} title="Окно">текст</Modal>);
    expect(states()).toEqual(['free']);
    expect(refusedByDialog()).toBe(false);
    cleanup();
    render(<Modal open onOpenChange={() => {}} title="Окно" nothingToLose>текст</Modal>);
    expect(refusedByDialog()).toBe(false);
  });

  it.each([
    ['dirty', { isDirty: true }],
    ['busy', { busy: true }],
    ['result', { acknowledge: 'Понятно' }],
  ] as const)('окно в состоянии %s уйти не даёт', (state, props) => {
    render(<Modal open onOpenChange={() => {}} title="Окно" {...props}>текст</Modal>);
    expect(states()).toEqual([state]);
    expect(refusedByDialog()).toBe(true);
  });

  it('окно, в котором набирали, — тоже', () => {
    render(<Modal open onOpenChange={() => {}} title="Окно"><input aria-label="поле" /></Modal>);
    expect(refusedByDialog()).toBe(false);
    fireEvent.change(screen.getByLabelText('поле'), { target: { value: 'набрано' } });
    expect(states()).toEqual(['touched']);
    expect(refusedByDialog()).toBe(true);
  });

  it('подтверждение: вопрос — нет, отказ — да', () => {
    render(<ConfirmDialog open onOpenChange={() => {}} title="Удалить?" confirmLabel="Удалить запись" onConfirm={() => {}} />);
    expect(refusedByDialog()).toBe(false);
    cleanup();
    render(<ConfirmDialog open onOpenChange={() => {}} title="Удалить?" confirmLabel="Удалить запись"
      blocked="Период закрыт." onConfirm={() => {}} />);
    expect(refusedByDialog()).toBe(true);
  });

  it('отказ показывает выход ВЕРХНЕГО окна, а не того, что под затемнением', () => {
    render(<>
      <Modal open onOpenChange={() => {}} title="Форма" isDirty footer={<ModalExit />}>форма</Modal>
      <Modal open onOpenChange={() => {}} title="Выбор" nothingToLose>пикер</Modal>
    </>);
    expect(states()).toEqual(['dirty', 'free']);
    expect(refusedByDialog()).toBe(true);
    const ringed = document.querySelector('[data-modal-hint]');
    expect(ringed?.closest('[role=dialog]')?.textContent).toContain('Выбор');
  });

  it('при идущем запросе выхода нет — кольца тоже', () => {
    render(<Modal open onOpenChange={() => {}} title="Окно" busy footer={<ModalExit />}>текст</Modal>);
    expect(refusedByDialog()).toBe(true);
    expect(document.querySelector('[data-modal-hint]')).toBeNull();
  });

  it('кнопка под вопросом «Закрыть без сохранения?» недоступна — кольцо на неё не ложится', () => {
    render(<Modal open onOpenChange={() => {}} title="Окно" isDirty footer={<ModalExit />}>текст</Modal>);
    fireEvent.click(screen.getByRole('button', { name: 'Отмена' }));
    expect(screen.getByText('Закрыть без сохранения?')).toBeTruthy();
    expect(refusedByDialog()).toBe(true);
    expect(document.querySelector('[data-modal-hint]')).toBeNull();
  });
});

describe('команда палитры поверх окна, которое стало держать', () => {
  it('не уводит со страницы: окно получило результат, пока палитра была открыта', () => {
    const asked = shown(false);
    render(<Modal open onOpenChange={() => {}} title="Итог" acknowledge="Понятно">итог</Modal>);
    expect(paletteShown()).toBe(true);

    choose('Учётный период');
    expect(address()).toBe('/invoices');
    expect(asked).not.toHaveBeenCalled();
    // Палитра закрылась, окно показало свой выход.
    expect(paletteShown()).toBe(false);
    expect(document.querySelector('[data-modal-hint]')?.textContent).toBe('Понятно');
  });

  it('и не выходит из учётной записи', () => {
    shown(false);
    render(<Modal open onOpenChange={() => {}} title="Итог" acknowledge="Понятно">итог</Modal>);
    choose('Выйти');
    expect(logout).not.toHaveBeenCalled();
  });
});
