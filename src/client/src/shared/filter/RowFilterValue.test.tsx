// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { useState } from 'react';
import type { FilterCondition } from '@/shared/api/types';
import { RowFilterValue } from './RowFilterValue';

vi.mock('@/shared/hooks/useLocale', async importOriginal => ({
  ...(await importOriginal<typeof import('@/shared/hooks/useLocale')>()),
  useLocale: () => ['ru-RU', () => {}, 'idle'],
}));

/**
 * Дата в отборе (issue #1465): поле даты маской языка пользователя вместо нативного, запись — та же
 * ГГГГ-ММ-ДД; «между» — период с поправкой границ; список — добавление по Enter и выбором в календаре.
 */

beforeAll(() => {
  globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.scrollIntoView ??= () => {};
});
afterEach(() => { cleanup(); vi.useRealTimers(); });

const DATE = { kind: 'date' } as const;

function Condition({ initial }: { initial: FilterCondition }) {
  const [cond, setCond] = useState(initial);
  return (
    <>
      <RowFilterValue cond={cond} column={DATE} onChange={setCond} />
      <output data-testid="cond">{JSON.stringify(cond.values ?? cond.value)}</output>
    </>
  );
}
const shown = () => JSON.parse(screen.getByTestId('cond').textContent!);
const seg = (group: string, ph: 'ДД' | 'ММ' | 'ГГГГ') =>
  within(screen.getByRole('group', { name: group })).getByPlaceholderText(ph) as HTMLInputElement;
function type(group: string, [d, m, y]: [string, string, string]) {
  fireEvent.focus(seg(group, 'ДД'));
  fireEvent.change(seg(group, 'ДД'), { target: { value: d } });
  fireEvent.change(seg(group, 'ММ'), { target: { value: m } });
  fireEvent.change(seg(group, 'ГГГГ'), { target: { value: y } });
}
function leave(el: HTMLElement) {
  fireEvent.blur(el);
  act(() => { vi.advanceTimersByTime(200); });
}

describe('RowFilterValue: дата', () => {
  it('одно значение — поле даты, не нативное; отдаёт ГГГГ-ММ-ДД', () => {
    const { container } = render(<Condition initial={{ type: 'condition', column: 'Дата', op: 'eq', value: '' }} />);
    expect(container.querySelector('input[type="date"]')).toBeNull();
    type('Значение', ['12', '10', '2026']);
    expect(shown()).toBe('2026-10-12');
  });

  it('сохранённое значение, которое не дата, показано текстом, а не спрятано', () => {
    render(<Condition initial={{ type: 'condition', column: 'Дата', op: 'eq', value: '12.10.2026' }} />);
    expect((screen.getByLabelText('Значение') as HTMLInputElement).value).toBe('12.10.2026');
  });

  it('«между» — период: «до» раньше «от» поправляется при уходе из поля', () => {
    vi.useFakeTimers();
    render(<Condition initial={{ type: 'condition', column: 'Дата', op: 'between', values: ['2026-10-12', '2026-10-24'] }} />);
    fireEvent.focus(seg('Границы, по', 'ДД'));
    fireEvent.change(seg('Границы, по', 'ДД'), { target: { value: '05' } });
    expect(shown()).toEqual(['2026-10-12', '2026-10-05']);
    leave(seg('Границы, по', 'ДД'));
    expect(shown()).toEqual(['2026-10-05', '2026-10-05']);
  });

  it('список: Enter в сегменте добавляет дату, а не отправляет форму', () => {
    const submit = vi.fn((e: React.FormEvent) => e.preventDefault());
    render(<form onSubmit={submit}><Condition initial={{ type: 'condition', column: 'Дата', op: 'in', values: ['2026-10-01'] }} /></form>);
    type('Добавить значение', ['12', '10', '2026']);
    const notCanceled = fireEvent.keyDown(seg('Добавить значение', 'ГГГГ'), { key: 'Enter' });
    expect(notCanceled).toBe(false);
    expect(shown()).toEqual(['2026-10-01', '2026-10-12']);
    expect(seg('Добавить значение', 'ДД').value).toBe('');
    expect(document.activeElement).toBe(seg('Добавить значение', 'ДД'));
  });

  it('список: выбор дня в календаре добавляет дату сразу', () => {
    render(<Condition initial={{ type: 'condition', column: 'Дата', op: 'in', values: [] }} />);
    fireEvent.click(within(screen.getByRole('group', { name: 'Добавить значение' })).getByRole('button', { name: 'Открыть календарь' }));
    const grid = screen.getByRole('grid', { name: 'Календарь' });
    const day = within(grid).getAllByRole('button').find(b => b.textContent === '15')!;
    fireEvent.click(day);
    expect(shown()).toHaveLength(1);
    expect(shown()[0]).toMatch(/^\d{4}-\d{2}-15$/);
  });
});
