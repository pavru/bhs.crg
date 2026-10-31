// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import type { FilterCondition } from '@/shared/api/types';
import { RowFilterValue } from './RowFilterValue';
import type { FilterColumn } from './rowFilterModel';

/**
 * Поле значения у колонки с открытым перечнем (UI-56, change `costs-filter-place-hints`): подсказка из
 * перечня при свободном тексте, «из списка» с выбором пункта, пометка значения списка и значения вне
 * закрытого перечня колонки-выбора — тоном предупреждения.
 */

afterEach(cleanup);

const OBJECTS: Omit<FilterColumn, 'name'> = {
  kind: 'list',
  known: { complete: true, items: [{ value: 'Комарова 36' }, { value: 'Комарова 36А' }, { value: 'Берёзовая 4' }] },
};

function Condition({ initial, column = OBJECTS }: { initial: FilterCondition; column?: Omit<FilterColumn, 'name'> }) {
  const [cond, setCond] = useState(initial);
  return (
    <>
      <RowFilterValue cond={cond} column={column} onChange={setCond} />
      <output data-testid="cond">{JSON.stringify(cond.values ?? cond.value)}</output>
    </>
  );
}
const shown = () => JSON.parse(screen.getByTestId('cond').textContent!);
const field = (name: string) => screen.getByRole('combobox', { name }) as HTMLInputElement;
const type = (input: HTMLInputElement, text: string) => fireEvent.change(input, { target: { value: text } });

describe('RowFilterValue: подсказка из перечня колонки', () => {
  it('«содержит Комарова» — свободный текст, хотя такого пункта нет', () => {
    render(<Condition initial={{ type: 'condition', column: 'Объект', op: 'contains', value: '' }} />);
    type(field('Значение'), 'Комарова');
    expect(field('Значение').getAttribute('aria-expanded')).toBe('true');
    fireEvent.keyDown(field('Значение'), { key: 'Tab' });
    expect(shown()).toBe('Комарова');
  });

  it('«из списка»: «ком» → ↓ → Enter добавляет «Комарова 36», а не «ком»', () => {
    render(<Condition initial={{ type: 'condition', column: 'Объект', op: 'in', values: [] }} />);
    type(field('Добавить значение'), 'ком');
    fireEvent.keyDown(field('Добавить значение'), { key: 'ArrowDown' });
    fireEvent.keyDown(field('Добавить значение'), { key: 'Enter' });
    expect(shown()).toEqual(['Комарова 36']);
    expect(field('Добавить значение').value).toBe('');
  });

  it('«из списка»: щелчок по пункту добавляет его сразу', () => {
    render(<Condition initial={{ type: 'condition', column: 'Объект', op: 'in', values: [] }} />);
    type(field('Добавить значение'), 'бер');
    fireEvent.click(screen.getAllByRole('option', { hidden: true })[0]);
    expect(shown()).toEqual(['Берёзовая 4']);
  });

  it('«из списка»: Enter при закрытом списке добавляет набранное', () => {
    render(<Condition initial={{ type: 'condition', column: 'Объект', op: 'in', values: [] }} />);
    type(field('Добавить значение'), 'Комарова');
    fireEvent.keyDown(field('Добавить значение'), { key: 'Escape' });
    expect(field('Добавить значение').getAttribute('aria-expanded')).toBe('false');
    fireEvent.keyDown(field('Добавить значение'), { key: 'Enter' });
    expect(shown()).toEqual(['Комарова']);
  });

  it('«из списка»: Enter при открытом списке без выбранного пункта тоже добавляет набранное и закрывает список', () => {
    render(<Condition initial={{ type: 'condition', column: 'Объект', op: 'in', values: [] }} />);
    type(field('Добавить значение'), 'Комарова');
    fireEvent.keyDown(field('Добавить значение'), { key: 'Enter' });
    expect(shown()).toEqual(['Комарова']);
    expect(field('Добавить значение').getAttribute('aria-expanded')).toBe('false');
  });

  it('колонка без перечня — прежнее текстовое поле без подсказки', () => {
    render(<Condition column={{ kind: 'list' }} initial={{ type: 'condition', column: 'Объект', op: 'eq', value: '' }} />);
    expect(screen.queryByRole('combobox')).toBeNull();
    expect((screen.getByLabelText('Значение') as HTMLInputElement).tagName).toBe('INPUT');
  });

  it('значение списка вне перечня — жетоном пометки', () => {
    render(<Condition initial={{ type: 'condition', column: 'Объект', op: 'in', values: ['Комарова 36', 'Ленина 5'] }} />);
    expect(screen.getAllByLabelText('нет в справочнике')).toHaveLength(1);
    expect(screen.getByLabelText('нет в справочнике').parentElement!.textContent).toContain('Ленина 5');
  });
});

describe('RowFilterValue: значение вне закрытого перечня колонки-выбора', () => {
  const STATE: Omit<FilterColumn, 'name'> = { kind: 'choice', options: ['Оплачен', 'Не оплачен'] };

  it('показано тоном предупреждения с текстом «нет в перечне колонки» и снимается крестиком', () => {
    const { container } = render(<Condition column={STATE}
      initial={{ type: 'condition', column: 'Состояние', op: 'in', values: ['Оплачен', 'Частично'] }} />);
    const stray = container.querySelector('[data-stray]')!;
    expect(stray.textContent).toContain('Частично');
    // Тона нет ([UI-68]): контейнер — как у прочих значений, о беде говорит знак цвета warning.
    expect(stray.parentElement!.className).toContain('bg-secondary-container');
    expect(stray.parentElement!.className).not.toContain('warning');
    expect(stray.querySelector('svg')!.getAttribute('class')).toContain('text-warning');
    // Жетон — подпись, а не кнопка: нажать в нём нечего, кроме крестика.
    expect(stray.tagName).toBe('SPAN');
    expect(stray.parentElement!.className).not.toContain('text-error');
    expect(screen.getByText(/нет в перечне колонки/).className).toContain('text-warning');
    fireEvent.click(screen.getByRole('button', { name: 'Убрать «Частично»' }));
    expect(shown()).toEqual(['Оплачен']);
  });
});
