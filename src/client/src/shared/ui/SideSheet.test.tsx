// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useState, type ReactNode } from 'react';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { BottomSheet } from './BottomSheet';
import { Modal } from './Modal';
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

/** Кнопка-якорь и лист, который она открывает; `inner` — что лежит в листе рядом с полями. */
function Host({ variant, bottom = false, inner }: { variant?: 'standard' | 'modal'; bottom?: boolean; inner?: ReactNode }) {
  const [open, setOpen] = useState(false);
  const close = () => setOpen(false);
  return (
    <div className="flex">
      <button type="button" onClick={() => setOpen(true)}>Открыть</button>
      {bottom
        ? <BottomSheet open={open} onClose={close} title="Состояние счёта"><p>факты</p></BottomSheet>
        : <SideSheet variant={variant!} open={open} onClose={close} title="Строка: счёт" closeLabel="Закрыть строку"><p>поля</p>{inner}</SideSheet>}
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

  it('Esc, уже обработанный внутри (меню, поле), панель не закрывает', () => {
    mockWidth(true);
    render(<Host variant="standard" inner={<input aria-label="поиск" onKeyDown={e => { if (e.key === 'Escape') e.preventDefault(); }} />} />);
    open();
    fireEvent.keyDown(screen.getByRole('textbox', { name: 'поиск' }), { key: 'Escape' });
    expect(screen.getByRole('complementary')).toBeTruthy();
    fireEvent.keyDown(screen.getByRole('textbox', { name: 'поиск' }), { key: 'Escape', isComposing: true });
    expect(screen.getByRole('complementary')).toBeTruthy();
  });

  it('уже 768 — во весь экран окном: страница под ним скрыта, «Назад» закрывает лист, а не страницу', async () => {
    mockWidth(false);
    render(<Host variant="standard" />);
    const anchor = open();
    await settle();
    const sheet = screen.getByRole('dialog', { name: 'Строка: счёт' });
    expect(sheet.className).toContain('fixed inset-0');
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Закрыть строку' }));
    // Якорь — на странице под листом: для читалки его нет.
    expect(screen.queryByRole('button', { name: 'Открыть' })).toBeNull();
    expect((window.history.state as { depth?: number }).depth).toBe(1);

    window.history.back();
    await settle();
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(document.activeElement).toBe(anchor);
  });
});

describe('SideSheet standard внутри окна', () => {
  /** Окно выбора: поиск и строка, по выбору которой справа открывается лист (карточка позиции, Р9-А). */
  function Picker({ onOpenChange }: { onOpenChange: (open: boolean) => void }) {
    const [sheet, setSheet] = useState(false);
    return (
      <Modal open onOpenChange={onOpenChange} title="Позиция номенклатуры">
        <div className="flex">
          <input aria-label="Поиск" data-search onKeyDown={e => { if (e.key === 'Enter') setSheet(true); }} />
          <SideSheet variant="standard" open={sheet} onClose={() => setSheet(false)} title="Кабель ВВГнг(А)-LS 3×2,5"
            autoFocus={false}><p>поля позиции</p></SideSheet>
        </div>
      </Modal>
    );
  }
  const esc = async () => {
    fireEvent.keyDown(document.activeElement!, { key: 'Escape' });
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
  };

  it('autoFocus={false}: фокус остаётся в поиске; первый Esc закрывает лист, второй — окно', async () => {
    mockWidth(true);
    const onOpenChange = vi.fn();
    render(<Picker onOpenChange={onOpenChange} />);
    const search = screen.getByRole('textbox', { name: 'Поиск' });
    search.focus();
    fireEvent.keyDown(search, { key: 'Enter' });
    expect(screen.getByRole('complementary', { name: 'Кабель ВВГнг(А)-LS 3×2,5' })).toBeTruthy();
    expect(document.activeElement).toBe(search);

    await esc();
    expect(screen.queryByRole('complementary')).toBeNull();
    expect(onOpenChange).not.toHaveBeenCalled();
    expect(document.activeElement).toBe(search);

    await esc();
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });

  it('Esc из самого листа закрывает лист, а не окно', async () => {
    mockWidth(true);
    const onOpenChange = vi.fn();
    render(<Picker onOpenChange={onOpenChange} />);
    fireEvent.keyDown(screen.getByRole('textbox', { name: 'Поиск' }), { key: 'Enter' });
    within(screen.getByRole('complementary')).getByRole('button', { name: 'Закрыть' }).focus();
    await esc();
    expect(screen.queryByRole('complementary')).toBeNull();
    expect(onOpenChange).not.toHaveBeenCalled();
  });

  it('Esc, остановленный полем окна, листа не закрывает', async () => {
    mockWidth(true);
    render(<Modal open onOpenChange={vi.fn()} title="Окно">
      <input aria-label="Правка" onKeyDown={e => { if (e.key === 'Escape') e.stopPropagation(); }} />
      <SideSheet variant="standard" open onClose={vi.fn()} title="Лист" autoFocus={false}><p>поля</p></SideSheet>
    </Modal>);
    const field = screen.getByRole('textbox', { name: 'Правка' });
    field.focus();
    await esc();
    expect(screen.getByRole('complementary', { name: 'Лист' })).toBeTruthy();
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

  it('ручка — кнопка «Закрыть»: касание без жеста закрывает лист и указатель не захватывает', async () => {
    const capture = vi.fn();
    HTMLElement.prototype.setPointerCapture = capture;
    try {
      render(<Host bottom />);
      open();
      // Захват с нажатия увёл бы щелчок с ручки на шапку — в браузере, не в jsdom: сверяем, что захвата нет.
      fireEvent.pointerDown(handle(), { pointerId: 1, button: 0, clientY: 100 });
      fireEvent.pointerMove(handle(), { pointerId: 1, clientY: 102 });
      fireEvent.pointerUp(handle(), { pointerId: 1, clientY: 102 });
      expect(capture).not.toHaveBeenCalled();
      fireEvent.click(handle());
      await settle();
      expect(screen.queryByRole('dialog')).toBeNull();
    } finally {
      delete (HTMLElement.prototype as Partial<HTMLElement>).setPointerCapture;
    }
  });

  it('жест захватывает указатель, когда начался, — дальше половины тянут и за пределами шапки', () => {
    const capture = vi.fn();
    HTMLElement.prototype.setPointerCapture = capture;
    try {
      render(<Host bottom />);
      open();
      pull(150);
      expect(capture).toHaveBeenCalledTimes(1);
    } finally {
      delete (HTMLElement.prototype as Partial<HTMLElement>).setPointerCapture;
    }
  });
});
