// @vitest-environment jsdom
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { useState } from 'react';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import * as Popover from './Popover';
import { Menu, MenuItem } from './Menu';
import { Modal } from './Modal';
import { resetHistoryLayers } from './historyLayers';
// Слушатель `popstate` — общий с охраной шага: без него слои не слышат «Назад».
import './NavigationGuard';

/**
 * Поповер — слой истории ([UI-96], #1418): на телефоне «Назад» закрывает его, а не окно или страницу
 * под ним. Роутер здесь — слушатель `popstate` после нашего: что дошло до него, увидел бы и роутер.
 */

const routerSaw: unknown[] = [];
const router = (e: PopStateEvent) => routerSaw.push(e.state);
/** jsdom ходит по истории не сразу — как браузер. */
const settle = () => act(() => new Promise(r => setTimeout(r, 30)));
const state = () => window.history.state as { idx?: number; layer?: string; depth?: number } | null;

function mockWidth(wide: boolean) {
  // Ширину спрашивают двумя вопросами — «шире 768» (WIDE) и «уже 768» (NARROW), — и ответ у них обратный.
  window.matchMedia = vi.fn().mockImplementation((q: string) => ({
    matches: q.includes('min-width') ? wide : !wide, addEventListener() {}, removeEventListener() {},
  })) as unknown as typeof window.matchMedia;
}

/** Слой — селектором: после измерения в jsdom Radix может прятать слой, и поиск по роли его не видит. */
const layer = () => document.querySelector<HTMLElement>('[data-testid="слой"]');

/** Наблюдатели видимости: тест сам говорит, что якорь ушёл из видимой части. */
type Seen = (entries: Array<{ isIntersecting: boolean }>) => void;
const seen: Array<{ cb: Seen; target?: Element }> = [];
class FakeIntersectionObserver {
  private own: { cb: Seen; target?: Element };
  constructor(cb: Seen) { this.own = { cb }; seen.push(this.own); }
  observe(target: Element) { this.own.target = target; }
  unobserve() {} disconnect() {}
}

const ownMatchMedia = window.matchMedia;
const ownIntersectionObserver = globalThis.IntersectionObserver;

beforeAll(() => {
  globalThis.IntersectionObserver = FakeIntersectionObserver as unknown as typeof IntersectionObserver;
  // jsdom не знает ни наблюдателя размеров, ни захвата указателя.
  globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.scrollIntoView ??= () => {};
});

afterAll(() => {
  window.matchMedia = ownMatchMedia;
  globalThis.IntersectionObserver = ownIntersectionObserver;
});

beforeEach(() => {
  resetHistoryLayers();
  seen.length = 0;
  mockWidth(false);
  window.history.replaceState({ idx: 0, key: 'base' }, '');
  routerSaw.length = 0;
  window.addEventListener('popstate', router);
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  window.removeEventListener('popstate', router);
});

/** Управляемый поповер в окне: кнопка «Условие», внутри — «Применить» (выбор) и меню. */
function InWindow({ onClose, onBack, menu = false }: { onClose: () => void; onBack?: () => boolean; menu?: boolean }) {
  const [open, setOpen] = useState(false);
  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Окно">
      <Popover.Root open={open} onOpenChange={setOpen} onBack={onBack}>
        <Popover.Trigger asChild><button type="button">Условие</button></Popover.Trigger>
        <Popover.Portal>
          <Popover.Content data-testid="слой" aria-label="Условие">
            <button type="button" onClick={() => setOpen(false)}>Применить</button>
            {menu && <Menu trigger={<button type="button">Действия</button>}><MenuItem>Переименовать</MenuItem></Menu>}
          </Popover.Content>
        </Popover.Portal>
      </Popover.Root>
    </Modal>
  );
}

async function open(name = 'Условие') {
  fireEvent.click(screen.getByRole('button', { name }));
  await settle();
  expect(layer()).not.toBeNull();
}

describe('Popover: «Назад» на телефоне', () => {
  it('открытие кладёт запись; «Назад» закрывает поповер, окно остаётся', async () => {
    const onClose = vi.fn();
    render(<InWindow onClose={onClose} />);
    await settle();
    expect(state()?.depth).toBe(1);
    await open();
    expect(state()?.depth).toBe(2);

    window.history.back();
    await settle();
    expect(layer()).toBeNull();
    expect(onClose).not.toHaveBeenCalled();
    expect(state()?.depth).toBe(1);
    expect(routerSaw).toEqual([]);
  });

  it.each<[string, () => void]>([
    ['Esc', () => fireEvent.keyDown(layer()!, { key: 'Escape' })],
    ['выбор', () => fireEvent.click(screen.getByText('Применить'))],
  ])('%s закрывает поповер и снимает его запись — следующая «Назад» закрывает окно', async (_, close) => {
    const onClose = vi.fn();
    render(<InWindow onClose={onClose} />);
    await settle();
    await open();
    act(close);
    await settle();
    expect(layer()).toBeNull();
    expect(state()?.depth).toBe(1);
    expect(onClose).not.toHaveBeenCalled();

    window.history.back();
    await settle();
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('неуправляемый поповер — тоже слой', async () => {
    render(
      <Popover.Root>
        <Popover.Trigger asChild><button type="button">Пояснение</button></Popover.Trigger>
        <Popover.Portal><Popover.Content data-testid="слой">текст</Popover.Content></Popover.Portal>
      </Popover.Root>,
    );
    await open('Пояснение');
    expect(state()?.depth).toBe(1);
    window.history.back();
    await settle();
    expect(layer()).toBeNull();
    expect(state()?.layer).toBeUndefined();
    expect(routerSaw).toEqual([]);
  });

  it('`onBack` → `false` оставляет поповер открытым, запись кладётся заново', async () => {
    const onBack = vi.fn(() => false);
    render(<InWindow onClose={() => {}} onBack={onBack} />);
    await settle();
    await open();
    window.history.back();
    await settle();
    expect(onBack).toHaveBeenCalledTimes(1);
    expect(layer()).not.toBeNull();
    expect(state()?.depth).toBe(2);
  });

  it('меню внутри поповера закрывается первым, следующая «Назад» — поповер', async () => {
    const onClose = vi.fn();
    render(<InWindow onClose={onClose} menu />);
    await settle();
    await open();
    fireEvent.pointerDown(screen.getByRole('button', { name: 'Действия' }), { button: 0, ctrlKey: false });
    await settle();
    expect(screen.queryByRole('menuitem', { name: 'Переименовать' })).not.toBeNull();
    expect(state()?.depth).toBe(3);

    window.history.back();
    await settle();
    expect(screen.queryByRole('menuitem', { name: 'Переименовать' })).toBeNull();
    expect(layer()).not.toBeNull();

    window.history.back();
    await settle();
    expect(layer()).toBeNull();
    expect(onClose).not.toHaveBeenCalled();
    expect(state()?.depth).toBe(1);
  });
});

describe('Popover: на рабочем столе', () => {
  it('записи истории не кладёт', async () => {
    mockWidth(true);
    render(<InWindow onClose={() => {}} />);
    await settle();
    await open();
    expect(state()).toEqual({ idx: 0, key: 'base' });
  });
});

describe('Popover: якорь ушёл из видимой части', () => {
  function Detachable({ mode, anchor = false, modal = false, onCloseAutoFocus }: {
    mode: 'hide' | 'close'; anchor?: boolean; modal?: boolean; onCloseAutoFocus?: (e: Event) => void;
  }) {
    const trigger = <Popover.Trigger asChild><button type="button">Якорь</button></Popover.Trigger>;
    return (
      <Popover.Root modal={modal}>
        {anchor ? <Popover.Anchor asChild><div data-testid="поле">{trigger}</div></Popover.Anchor> : trigger}
        <Popover.Portal>
          <Popover.Content data-testid="слой" onCloseAutoFocus={onCloseAutoFocus}
            hideWhenDetached={mode === 'hide'} closeWhenDetached={mode === 'close'}>
            <button type="button">внутри</button>
          </Popover.Content>
        </Popover.Portal>
      </Popover.Root>
    );
  }
  // Модальный поповер прячет остальное от чтения с экрана (`aria-hidden`) — кнопку ищем и среди скрытого.
  const button = () => screen.getByRole('button', { name: 'Якорь', hidden: true });

  async function scrollAway() {
    expect(seen).toHaveLength(1);
    const [{ cb }] = seen;
    act(() => cb([{ isIntersecting: true }]));
    expect(layer()).not.toBeNull();
    act(() => cb([{ isIntersecting: false }]));
    await settle();
  }

  // Radix при закрытии сам фокусирует кнопку — обычным `focus()`, с прокруткой: его возврат обёртка гасит.
  // У модального ещё и ловушка фокуса: поставленный раньше закрытия фокус она вернула бы в слой.
  it.each<[string, 'hide' | 'close', boolean, boolean]>([
    ['hideWhenDetached на телефоне — закрывает', 'hide', false, false],
    ['closeWhenDetached на телефоне — закрывает', 'close', false, false],
    ['closeWhenDetached на рабочем столе — закрывает', 'close', true, false],
    ['модальный, hideWhenDetached на телефоне — закрывает', 'hide', false, true],
    ['модальный, closeWhenDetached на рабочем столе — закрывает', 'close', true, true],
  ])('%s; фокус на якоре без прокрутки, обработчик экрана вызван', async (_, mode, wide, modal) => {
    mockWidth(wide);
    const onCloseAutoFocus = vi.fn();
    render(<Detachable mode={mode} modal={modal} onCloseAutoFocus={onCloseAutoFocus} />);
    await open('Якорь');
    const focus = vi.spyOn(button(), 'focus');
    await scrollAway();
    expect(layer()).toBeNull();
    expect(focus).toHaveBeenCalledWith({ preventScroll: true });
    // Radix вернул бы фокус обычным `focus()` — с прокруткой; его возврат погашен.
    expect(focus).toHaveBeenCalledTimes(1);
    expect(document.activeElement).toBe(button());
    expect(onCloseAutoFocus).toHaveBeenCalledTimes(1);
    expect(state()?.layer).toBeUndefined();
  });

  it('hideWhenDetached на рабочем столе — прячет, как Radix: наблюдателя нет', async () => {
    mockWidth(true);
    render(<Detachable mode="hide" />);
    await open('Якорь');
    expect(seen).toEqual([]);
  });

  it('при `Anchor` и `Trigger` разом смотрится `Trigger`', async () => {
    render(<Detachable mode="hide" anchor />);
    await open('Якорь');
    expect(seen).toHaveLength(1);
    expect(seen[0].target).toBe(button());
  });

  it('без `Trigger` смотрится `Anchor`', async () => {
    function Anchored() {
      const [isOpen, setOpen] = useState(false);
      return (
        <Popover.Root open={isOpen} onOpenChange={setOpen}>
          <Popover.Anchor asChild><div data-testid="поле"><button type="button" onClick={() => setOpen(true)}>Якорь</button></div></Popover.Anchor>
          <Popover.Portal><Popover.Content data-testid="слой" closeWhenDetached>x</Popover.Content></Popover.Portal>
        </Popover.Root>
      );
    }
    render(<Anchored />);
    await open('Якорь');
    expect(seen).toHaveLength(1);
    expect(seen[0].target).toBe(screen.getByTestId('поле'));
  });
});

describe('Popover: перечень частей', () => {
  it('модуль экспортирует только названные части — без необёрнутых псевдонимов Radix', () => {
    expect(Object.keys(Popover).sort()).toEqual(['Anchor', 'Arrow', 'Close', 'Content', 'Portal', 'Root', 'Trigger']);
  });
});
