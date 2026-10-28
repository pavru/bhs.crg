// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import type { FilterCondition, FilterNode } from '@/shared/api/types';
import { ConditionEditor } from './ConditionEditor';
import { FilterChips } from './FilterChips';
import { RowFilterValue } from './RowFilterValue';
import type { FilterChange } from './chipsModel';
import type { FilterColumn } from './rowFilterModel';

/**
 * Поля условия отбора — `Select`, а не нативный `<select>` (#1469, [UI-48]): колонка, оператор и
 * значение «да / нет» или из списка — и в строке диалога, и в поповере чипа над таблицей.
 */

// Radix зовёт API указателя, прокрутки и размеров, которых в jsdom нет.
beforeAll(() => {
  globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.releasePointerCapture ??= () => {};
  Element.prototype.scrollIntoView ??= () => {};
});
afterEach(cleanup);

const SUM: FilterColumn = { name: 'Итого', label: 'Сумма', kind: 'number', operators: ['eq', 'gt'] };
const PAID: FilterColumn = { name: 'Оплачен', label: 'Оплачен', kind: 'boolean', operators: ['eq'] };
const LOCKED: FilterColumn = { name: 'Банк', label: 'Банк', kind: 'text', operators: ['eq'], unavailable: 'нет права' };
const COLUMNS = [SUM, PAID, LOCKED];

const combo = (name: string) => screen.getByRole('combobox', { name });
/** Открыть список и выбрать пункт — так, как это делает человек с клавиатуры. */
function pick(field: string, option: string) {
  fireEvent.keyDown(combo(field), { key: 'Enter' });
  fireEvent.click(screen.getByRole('option', { name: option }));
}

describe('ConditionEditor: колонка и оператор — Select', () => {
  const cond = (extra: Partial<FilterCondition> = {}): FilterCondition =>
    ({ type: 'condition', column: '', op: 'eq', value: '', ...extra });

  it('выбор колонки отдаёт условие на неё (withColumn)', () => {
    const onChange = vi.fn<(c: FilterCondition) => void>();
    render(<ConditionEditor cond={cond()} columns={COLUMNS} onChange={onChange} />);
    expect(combo('Колонка').textContent).toContain('— колонка —');
    pick('Колонка', 'Сумма');
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ column: 'Итого', op: 'eq' }));
  });

  it('колонка, которой в источнике уже нет, показана выбранной, а не «— колонка —»', () => {
    render(<ConditionEditor cond={cond({ column: 'Удалённая' })} columns={COLUMNS} onChange={() => {}} />);
    expect(combo('Колонка').textContent).toContain('Удалённая');
  });

  it('недоступная колонка — в списке, неактивная и с причиной', () => {
    render(<ConditionEditor cond={cond()} columns={COLUMNS} onChange={() => {}} />);
    fireEvent.keyDown(combo('Колонка'), { key: 'Enter' });
    const locked = screen.getByRole('option', { name: 'Банк — нет права' });
    expect(locked.getAttribute('aria-disabled')).toBe('true');
  });

  it('смена оператора — тем же списком', () => {
    const onChange = vi.fn<(c: FilterCondition) => void>();
    render(<ConditionEditor cond={cond({ column: 'Итого' })} columns={COLUMNS} onChange={onChange} />);
    pick('Оператор', '> больше');
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ op: 'gt' }));
  });
});

describe('RowFilterValue: «да / нет» — Select', () => {
  function Condition({ initial }: { initial: FilterCondition }) {
    const [cond, setCond] = useState(initial);
    return (
      <>
        <RowFilterValue cond={cond} column={{ kind: 'boolean' }} onChange={setCond} />
        <output data-testid="cond">{JSON.stringify(cond.values ?? cond.value)}</output>
      </>
    );
  }
  const shown = () => JSON.parse(screen.getByTestId('cond').textContent!);

  it('выбор «нет» доходит до условия', () => {
    render(<Condition initial={{ type: 'condition', column: 'Оплачен', op: 'eq', value: '' }} />);
    pick('Значение', 'нет');
    expect(shown()).toBe('false');
  });

  it('в списке значений выбор фиксируется закрытием списка, как уходом из поля', () => {
    render(<Condition initial={{ type: 'condition', column: 'Оплачен', op: 'in', values: [] }} />);
    pick('Добавить значение', 'да');
    expect(shown()).toEqual(['true']);
    // Закрыли, ничего не выбрав, — добавлять нечего.
    fireEvent.keyDown(combo('Добавить значение'), { key: 'Enter' });
    fireEvent.keyDown(screen.getByRole('listbox'), { key: 'Escape' });
    expect(shown()).toEqual(['true']);
  });
});

describe('FilterChips: Select в поповере чипа', () => {
  function Chips() {
    const [filter, setFilter] = useState<FilterNode | null>(null);
    return <FilterChips columns={COLUMNS} filter={filter} onChange={(change: FilterChange) => setFilter(change)} />;
  }

  it('выбор колонки и оператора не закрывает поповер; Esc закрывает список, а не поповер', () => {
    render(<Chips />);
    fireEvent.click(screen.getByRole('button', { name: 'условие' }));
    const popover = screen.getByRole('dialog');
    pick('Колонка', 'Сумма');
    pick('Оператор', '> больше');
    expect(screen.getByRole('dialog')).toBe(popover);
    expect(combo('Оператор').textContent).toContain('больше');

    fireEvent.keyDown(combo('Колонка'), { key: 'Enter' });
    fireEvent.keyDown(screen.getByRole('listbox'), { key: 'Escape' });
    expect(screen.queryByRole('listbox')).toBeNull();
    expect(screen.getByRole('dialog')).toBe(popover);
  });
});
