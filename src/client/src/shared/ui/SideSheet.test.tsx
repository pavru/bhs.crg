// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useState } from 'react';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { BottomSheet } from './BottomSheet';
import { SideSheet } from './SideSheet';
import { resetHistoryLayers } from './historyLayers';
// Слушатель `popstate` — общий с охраной шага: без него слои не слышат «Назад».
import './NavigationGuard';

/** Листы MD3 ([UI-77], sharedui-md3-13 PR 2б): standard, modal и bottom sheet; Esc, «Назад», жест. */

const realMatchMedia = window.matchMedia;
/** jsdom ходит по истории не сразу — как браузер. */
const settle = () => act(() => new Promise(r => setTimeout(r, 30)));

function mockWidth(wide: boolean) {
  window.matchMedia = vi.fn().mockImplementation((q: string) => ({
    matches: q.includes('min-width') ? wide : !wide, addEventListener() {}, removeEventListener() {},
  })) as unknown as typeof window.matchMedia;
}

beforeEach(() => {
  resetHistoryLayers();
  window.history.replaceState({ idx: 0, key: 'base' }, '');
});

afterEach(() => {
  cleanup();
  window.matchMedia = realMatchMedia;
});

/** Кнопка-якорь и лист, который она открывает. */
function Host({ variant, bottom = false }: { variant?: 'standard' | 'modal'; bottom?: boolean }) {
  const [open, setOpen] = useState(false);
  const close = () => setOpen(false);
  return (
    <div className="flex">
      <button type="button" onClick={() => setOpen(true)}>Открыть</button>
      {bottom
        ? <BottomSheet open={open} onClose={close} title="Состояние счёта"><p>факты</p></BottomSheet>
        : <SideSheet variant={variant!} open={open} onClose={close} title="Строка: счёт" closeLabel="Закрыть строку"><p>поля</p></SideSheet>}
    </div>
  );
}

function open() {
  const anchor = screen.getByRole('button', { name: 'Открыть' });
  anchor.focus();
  fireEvent.click(anchor);
  return anchor;
}

describe('SideSheet standard', () => {
  it('на столе — панель рядом с содержимым без затемнения; Esc закрывает, фокус — к якорю', async () => {
    mockWidth(true);
    render(<Host variant="standard" />);
    const anchor = open();
    const panel = screen.getByRole('complementary', { name: 'Строка: счёт' });
    expect(panel.className).toContain('border-l');
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(screen.getByRole('heading', { name: 'Строка: счёт' }).className).toContain('text-title-large');
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Закрыть строку' }));

    fireEvent.keyDown(document.activeElement!, { key: 'Escape' });
    await settle();
    expect(screen.queryByRole('complementary')).toBeNull();
    expect(document.activeElement).toBe(anchor);
  });

  it('уже 768 — во весь экран, и «Назад» закрывает лист, а не страницу', async () => {
    mockWidth(false);
    render(<Host variant="standard" />);
    const anchor = open();
    await settle();
    expect(screen.getByRole('complementary').className).toContain('fixed inset-0');
    expect((window.history.state as { depth?: number }).depth).toBe(1);

    window.history.back();
    await settle();
    expect(screen.queryByRole('complementary')).toBeNull();
    expect(document.activeElement).toBe(anchor);
  });
});

describe('SideSheet modal', () => {
  it('на столе — окно сбоку с ведущими углами 16 и уровнем 1; Esc закрывает, фокус — к якорю', async () => {
    mockWidth(true);
    render(<Host variant="modal" />);
    const anchor = open();
    const sheet = screen.getByRole('dialog', { name: 'Строка: счёт' });
    expect(sheet.className).toEqual(expect.stringContaining('rounded-l-lg'));
    expect(sheet.className).toEqual(expect.stringContaining('shadow-elevation-1'));

    fireEvent.keyDown(sheet, { key: 'Escape' });
    await settle();
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(document.activeElement).toBe(anchor);
  });

  it('уже 768 — лист снизу с ручкой; «Назад» закрывает его', async () => {
    mockWidth(false);
    render(<Host variant="modal" />);
    open();
    await settle();
    const sheet = screen.getByRole('dialog', { name: 'Строка: счёт' });
    expect(sheet.className).toContain('rounded-t-xl');
    expect(sheet.querySelector('[data-sheet-handle]')).not.toBeNull();

    window.history.back();
    await settle();
    expect(screen.queryByRole('dialog')).toBeNull();
  });
});

describe('BottomSheet: жест', () => {
  beforeEach(() => {
    mockWidth(false);
    vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(400);
  });
  afterEach(() => vi.restoreAllMocks());

  const handle = () => document.querySelector<HTMLElement>('[data-sheet-handle]')!;
  const pull = (to: number, end: 'pointerUp' | 'pointerCancel' = 'pointerUp') => {
    const h = handle();
    fireEvent.pointerDown(h, { pointerId: 1, button: 0, clientY: 100 });
    fireEvent.pointerMove(h, { pointerId: 1, clientY: 100 + to });
    fireEvent[end](h, { pointerId: 1, clientY: 100 + to });
  };

  it('стянут дальше половины высоты — закрыт, фокус вернулся к якорю', async () => {
    render(<Host bottom />);
    const anchor = open();
    pull(250);
    await settle();
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(document.activeElement).toBe(anchor);
  });

  it('отпущен раньше половины — остаётся, и щелчок в конце жеста его не закрывает', async () => {
    render(<Host bottom />);
    open();
    pull(150);
    fireEvent.click(handle());
    await settle();
    expect(screen.getByRole('dialog', { name: 'Состояние счёта' }).style.transform).toBe('');
  });

  it('pointercancel — лист на месте, даже стянутый дальше половины', async () => {
    render(<Host bottom />);
    open();
    pull(300, 'pointerCancel');
    await settle();
    expect(screen.getByRole('dialog')).toBeTruthy();
  });

  it('ручка — кнопка «Закрыть»: нажатие без жеста закрывает лист', async () => {
    render(<Host bottom />);
    open();
    fireEvent.click(handle());
    await settle();
    expect(screen.queryByRole('dialog')).toBeNull();
  });
});
