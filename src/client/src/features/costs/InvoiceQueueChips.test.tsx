// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import type { ComponentProps } from 'react';
import type { InvoiceQueues } from '@/shared/api/invoiceQueues';
import { InvoiceQueueChips } from './InvoiceQueueChips';

/** Очереди над лентой счетов — один чип с меню (costs-invoice-filter-row, issue #1910, [COST-6.2]). */

// Radix Select зовёт API указателя и прокрутки, которых в jsdom нет.
beforeAll(() => {
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.releasePointerCapture ??= () => {};
  Element.prototype.scrollIntoView ??= () => {};
});
afterEach(cleanup);

const numbers = (patch: Partial<InvoiceQueues> = {}): InvoiceQueues => ({
  lost: 0, archived: 0, locked: 0, doubt: null, unrecognized: 0, ...patch,
});

function shown(props: Partial<ComponentProps<typeof InvoiceQueueChips>> = {}) {
  const onChange = vi.fn();
  render(<InvoiceQueueChips queue={null} onChange={onChange} queues={numbers()} parsing={3812} failed={false}
    onRetry={() => {}} sites={<button type="button">Стройки</button>} {...props} />);
  return onChange;
}
const chip = () => screen.getByRole('combobox', { name: 'Очередь счетов' });
const open = () => fireEvent.keyDown(chip(), { key: 'Enter' });
const LOST = 'svg[aria-label="Есть счета с удалёнными записями"]';

describe('чип очередей счетов', () => {
  it('в покое — «Все счета», невыбранный; рядом отбор строек, отдельных чипов очередей нет', () => {
    shown();
    expect(chip().textContent).toBe('Все счета');
    expect(chip().hasAttribute('data-selected')).toBe(false);
    const group = screen.getByRole('group', { name: 'Отбор списка счетов' });
    expect(within(group).queryByRole('button', { name: /Разобрать/ })).toBeNull();
    expect(within(group).getByRole('button', { name: 'Стройки' })).toBeTruthy();
  });

  it('выбор пункта зовёт onChange с очередью, «Все счета» — с null', () => {
    const onChange = shown();
    open();
    const parsing = screen.getByRole('option', { name: 'Разобрать' });
    // Имя пункта — подпись, число — описанием: так и прогоны ищут пункт по имени.
    expect(parsing.getAttribute('aria-describedby')).toBeTruthy();
    expect(document.getElementById(parsing.getAttribute('aria-describedby')!)!.textContent).toBe('3812');
    fireEvent.click(parsing);
    expect(onChange).toHaveBeenLastCalledWith('parsing');
    cleanup();

    const again = shown({ queue: 'parsing' });
    open();
    fireEvent.click(screen.getByRole('option', { name: 'Все счета' }));
    expect(again).toHaveBeenLastCalledWith(null);
  });

  it('выбранная очередь — на чипе подпись и её число', () => {
    shown({ queue: 'parsing' });
    expect(chip().textContent).toBe('Разобрать· 3812');
    expect(chip().hasAttribute('data-selected')).toBe(true);
  });

  it('пункты — по числам: пустые скрыты, у пунктов число справа', () => {
    shown({ queues: numbers({ unrecognized: 645, archived: 2 }) });
    open();
    expect(screen.getAllByRole('option').map(o => o.textContent))
      .toEqual(['Все счета', 'Разобрать 3812', 'Не распознано 645', 'В архиве 2']);
  });

  it('есть потерянное — у закрытого чипа знак с именем, в покое и под другой очередью', () => {
    shown({ queues: numbers({ lost: 2 }) });
    expect(chip().querySelector(LOST)).not.toBeNull();
    cleanup();
    shown({ queues: numbers({ lost: 2, archived: 1 }), queue: 'archived' });
    expect(chip().querySelector(LOST)).not.toBeNull();
    expect(chip().textContent).toBe('В архиве· 1');
  });

  it('потерянного нет — ведущего знака нет', () => {
    shown();
    expect(chip().querySelectorAll('svg')).toHaveLength(1); // только стрелка
  });

  it('числа не пришли — пункты без чисел, «Не распознано» остаётся, рядом «Отборы не посчитаны»', () => {
    const onRetry = vi.fn();
    shown({ queues: undefined, parsing: undefined, failed: true, onRetry });
    fireEvent.click(screen.getByRole('button', { name: /Отборы не посчитаны/ }));
    expect(onRetry).toHaveBeenCalled();
    open();
    expect(screen.getAllByRole('option').map(o => o.textContent)).toEqual(['Все счета', 'Разобрать', 'Не распознано']);
  });
});
