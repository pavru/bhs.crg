// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { useState } from 'react';
import { DateRange } from './DateRange';

// Маска — языка пользователя (issue #1461); jsdom говорит по-английски, поэтому язык — явно.
vi.mock('@/shared/hooks/useLocale', async importOriginal => ({
  ...(await importOriginal<typeof import('@/shared/hooks/useLocale')>()),
  useLocale: () => ['ru-RU', () => {}, 'idle'],
}));

/**
 * Период (issue #1464, вариант А): «по» не раньше «с», побеждает граница, которую правили, и поправка —
 * при уходе из поля или выборе в календаре, а не на каждой цифре.
 */

beforeAll(() => {
  // Календарь — поповер Radix: jsdom не знает ни наблюдателя размеров, ни захвата указателя.
  globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.scrollIntoView ??= () => {};
});
afterEach(() => { cleanup(); vi.useRealTimers(); });

function Period({ from = '2026-10-12', to = '2026-10-24', ordered }: { from?: string; to?: string; ordered?: boolean }) {
  const [range, setRange] = useState({ from, to });
  return (
    <>
      <DateRange label="Дата счёта" from={range.from} to={range.to} ordered={ordered}
        onChange={(f, t) => setRange({ from: f, to: t })} />
      <output data-testid="range">{`${range.from}|${range.to}`}</output>
    </>
  );
}
const shown = () => screen.getByTestId('range').textContent;
const field = (side: 'с' | 'по') => screen.getByRole('group', { name: `Дата счёта, ${side}` });
const seg = (side: 'с' | 'по', ph: 'ДД' | 'ММ' | 'ГГГГ') => within(field(side)).getByPlaceholderText(ph) as HTMLInputElement;
function leave(el: HTMLInputElement) {
  fireEvent.blur(el);
  act(() => { vi.advanceTimersByTime(200); });
}

describe('DateRange: поправка другой границы', () => {
  it('поздняя «с» тянет «по» — при уходе из поля, а не на цифре', () => {
    vi.useFakeTimers();
    render(<Period />);
    fireEvent.focus(seg('с', 'ДД'));
    fireEvent.change(seg('с', 'ДД'), { target: { value: '28' } });
    expect(shown()).toBe('2026-10-28|2026-10-24'); // посреди правки другую границу не трогаем
    leave(seg('с', 'ММ'));
    expect(shown()).toBe('2026-10-28|2026-10-28');
  });

  it('ранняя «по» тянет «с»', () => {
    vi.useFakeTimers();
    render(<Period />);
    fireEvent.focus(seg('по', 'ДД'));
    fireEvent.change(seg('по', 'ДД'), { target: { value: '05' } });
    leave(seg('по', 'ММ'));
    expect(shown()).toBe('2026-10-05|2026-10-05');
  });

  it('стрелка вниз по дню «по» другую границу не утягивает, пока поле не оставили', () => {
    vi.useFakeTimers();
    render(<Period to="2026-10-12" />);
    fireEvent.focus(seg('по', 'ДД'));
    fireEvent.keyDown(seg('по', 'ДД'), { key: 'ArrowDown' });
    fireEvent.keyDown(seg('по', 'ДД'), { key: 'ArrowUp' });
    leave(seg('по', 'ДД'));
    expect(shown()).toBe('2026-10-12|2026-10-12');
  });

  it('шаг Tab\'ом на значок календаря — ещё поле: поправка — когда ушли и со значка', () => {
    vi.useFakeTimers();
    render(<Period />);
    const trigger = within(field('с')).getByRole('button', { name: 'Открыть календарь' });
    fireEvent.focus(seg('с', 'ДД'));
    fireEvent.change(seg('с', 'ДД'), { target: { value: '28' } });
    fireEvent.blur(seg('с', 'ГГГГ'), { relatedTarget: trigger });
    fireEvent.focus(trigger);
    act(() => { vi.advanceTimersByTime(200); });
    expect(shown()).toBe('2026-10-28|2026-10-24');
    fireEvent.blur(trigger);
    act(() => { vi.advanceTimersByTime(200); });
    expect(shown()).toBe('2026-10-28|2026-10-28');
  });

  it('нетронутая сохранённая пара «наоборот» при проходе Tab\'ом не правится', () => {
    vi.useFakeTimers();
    render(<Period from="2026-10-28" to="2026-10-24" />);
    fireEvent.focus(seg('с', 'ДД'));
    leave(seg('с', 'ДД'));
    fireEvent.focus(seg('по', 'ДД'));
    leave(seg('по', 'ДД'));
    expect(shown()).toBe('2026-10-28|2026-10-24');
  });

  it('стёртая граница другую не сдвигает', () => {
    vi.useFakeTimers();
    render(<Period />);
    fireEvent.click(within(field('с')).getByRole('button', { name: 'Очистить дату' }));
    leave(seg('с', 'ДД'));
    expect(shown()).toBe('|2026-10-24');
  });
});

// Приёмник, который пару «наоборот» отвергает (адрес отчёта «Затраты по стройке», issue #1466).
describe('DateRange: ordered', () => {
  it('«по» раньше «с» наружу не отдаётся, но видна в поле; уход отдаёт поправленную', () => {
    vi.useFakeTimers();
    render(<Period ordered />);
    fireEvent.focus(seg('по', 'ДД'));
    fireEvent.change(seg('по', 'ДД'), { target: { value: '05' } });
    expect(shown()).toBe('2026-10-12|2026-10-24');
    expect(seg('по', 'ДД').value).toBe('05');
    leave(seg('по', 'ДД'));
    expect(shown()).toBe('2026-10-05|2026-10-05');
    expect(seg('с', 'ДД').value).toBe('05');
  });

  it('вернули по порядку до ухода — отдаётся как есть, другая граница на месте', () => {
    vi.useFakeTimers();
    render(<Period ordered />);
    fireEvent.focus(seg('с', 'ДД'));
    fireEvent.change(seg('с', 'ДД'), { target: { value: '28' } });
    expect(shown()).toBe('2026-10-12|2026-10-24');
    fireEvent.change(seg('с', 'ДД'), { target: { value: '20' } });
    expect(shown()).toBe('2026-10-20|2026-10-24');
    leave(seg('с', 'ДД'));
    expect(shown()).toBe('2026-10-20|2026-10-24');
  });
});

describe('DateRange: календари', () => {
  const openCalendar = (side: 'с' | 'по') => fireEvent.click(within(field(side)).getByRole('button', { name: 'Открыть календарь' }));
  const day = (n: string) => within(screen.getByRole('grid', { name: 'Календарь' })).getAllByRole('button').find(b => b.textContent === n)! as HTMLButtonElement;

  it('в календаре «по» дни раньше «с» неактивны; календарь «с» свободен', () => {
    render(<Period />);
    openCalendar('по');
    expect(day('11').disabled).toBe(true);
    expect(day('12').disabled).toBe(false);
    fireEvent.keyDown(screen.getByRole('grid', { name: 'Календарь' }), { key: 'Escape' });
    cleanup();
    render(<Period />);
    openCalendar('с');
    expect(day('28').disabled).toBe(false);
  });

  it('выбор в календаре «с» позже «по» тянет «по» сразу — выбор и есть фиксация', () => {
    render(<Period />);
    openCalendar('с');
    fireEvent.click(day('28'));
    expect(shown()).toBe('2026-10-28|2026-10-28');
  });
});

describe('DateRange: подписи', () => {
  it('legend — подпись группы, поля названы «с» и «по» для чтения с экрана', () => {
    render(<Period />);
    expect(screen.getByRole('group', { name: 'Дата счёта' })).toBeTruthy();
    expect(field('с')).toBeTruthy();
    expect(field('по')).toBeTruthy();
  });
});
