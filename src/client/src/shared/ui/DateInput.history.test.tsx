// @vitest-environment jsdom
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { useState } from 'react';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { DateInput } from './DateInput';
import { Modal } from './Modal';
import { resetHistoryLayers } from './historyLayers';
// Слушатель `popstate` — общий с охраной шага: без него слои не слышат «Назад».
import './NavigationGuard';

vi.mock('@/shared/hooks/useLocale', async importOriginal => ({
  ...(await importOriginal<typeof import('@/shared/hooks/useLocale')>()),
  useLocale: () => ['ru-RU', () => {}, 'idle'],
}));

/**
 * Календарь поля даты — слой истории ([UI-54], #1522): на телефоне «Назад» закрывает его, а не окно
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

function Payment({ onClose }: { onClose: () => void }) {
  const [date, setDate] = useState('2026-11-12');
  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Оплата">
      <DateInput label="Дата платежа" value={date} onChange={setDate} />
    </Modal>
  );
}

/**
 * Календарь — селектором, а не по роли: после измерения в jsdom Radix прячет слой (`hideWhenDetached` —
 * у якоря нет размеров), а у скрытого узла поиск по роли имени не вычисляет.
 */
const calendar = () => document.querySelector<HTMLElement>('[role="grid"][aria-label="Календарь"]');

beforeAll(() => {
  // Календарь — поповер Radix: jsdom не знает ни наблюдателя размеров, ни захвата указателя.
  globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.scrollIntoView ??= () => {};
});

beforeEach(() => {
  resetHistoryLayers();
  mockWidth(false);
  window.history.replaceState({ idx: 0, key: 'base' }, '');
  routerSaw.length = 0;
  window.addEventListener('popstate', router);
});

afterEach(() => {
  cleanup();
  window.removeEventListener('popstate', router);
});

async function openCalendar() {
  fireEvent.click(screen.getByRole('button', { name: 'Открыть календарь' }));
  await settle();
  expect(calendar()).not.toBeNull();
}

describe('DateInput: «Назад» на телефоне', () => {
  it('«Назад» закрывает календарь, окно остаётся', async () => {
    const onClose = vi.fn();
    render(<Payment onClose={onClose} />);
    await settle();
    expect(state()?.depth).toBe(1);
    await openCalendar();
    expect(state()?.depth).toBe(2);

    window.history.back();
    await settle();
    expect(calendar()).toBeNull();
    expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByRole('group', { name: 'Дата платежа' })).toBeTruthy();
    expect(state()?.depth).toBe(1);
    expect(routerSaw).toEqual([]);
  });

  it('выбор дня снимает запись календаря — следующая «Назад» закрывает окно', async () => {
    const onClose = vi.fn();
    render(<Payment onClose={onClose} />);
    await settle();
    await openCalendar();
    fireEvent.click(within(calendar()!).getByText('19'));
    await settle();
    expect(calendar()).toBeNull();
    expect(state()?.depth).toBe(1);

    window.history.back();
    await settle();
    expect(calendar()).toBeNull();
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(routerSaw).toEqual([]);
  });

  it('Esc закрывает календарь и снимает его запись', async () => {
    render(<Payment onClose={() => {}} />);
    await settle();
    await openCalendar();
    fireEvent.keyDown(calendar()!, { key: 'Escape' });
    await settle();
    expect(calendar()).toBeNull();
    expect(state()?.depth).toBe(1);
  });
});

describe('DateInput: «Назад» на рабочем столе', () => {
  it('календарь записей истории не кладёт', async () => {
    mockWidth(true);
    render(<DateInput label="Дата платежа" value="2026-11-12" onChange={() => {}} />);
    await openCalendar();
    expect(state()).toEqual({ idx: 0, key: 'base' });
  });
});
