// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { FilterCondition, FilterNode } from '@/shared/api/types';
import { SearchOff, Warning, type IconDef } from '@/shared/ui/icons';
import { FilterChips } from './FilterChips';
import type { FilterChange } from './chipsModel';
import type { FilterColumn } from './rowFilterModel';

/**
 * Пометка «нет в справочнике» на чипе над таблицей и в его окошке (UI-56): чип — тоном предупреждения со
 * своим значком и без действия, замена — в окошке, «Применить» не заперт.
 */

beforeAll(() => {
  globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.scrollIntoView ??= () => {};
});
afterEach(cleanup);

const OBJECTS: FilterColumn = {
  name: 'ОбъектыРазноски', label: 'Объект', kind: 'list',
  operators: ['eq', 'neq', 'in', 'not_in', 'contains', 'is_empty', 'is_not_empty'],
  known: { complete: true, items: [{ value: 'Комарова 36' }, { value: 'Берёзовая 4' }] },
};
const SUM: FilterColumn = { name: 'Итого', label: 'Сумма', kind: 'number', operators: ['eq', 'gt'] };
const cond = (op: FilterCondition['op'], extra: Partial<FilterCondition>): FilterCondition =>
  ({ type: 'condition', column: 'ОбъектыРазноски', op, ...extra });

function chips(filter: FilterNode, onChange: (c: FilterChange) => void = () => {}) {
  return render(<FilterChips columns={[OBJECTS, SUM]} filter={filter} onChange={onChange} />);
}

describe('FilterChips: пометка значения вне справочника', () => {
  it('отбор по ссылке со старым названием — чип со значком справочника, без тревоги', () => {
    const { container } = chips(cond('in', { values: ['Комарова 36А'] }));
    const chip = container.querySelector('[data-notice]')!;
    // Тона нет ([UI-68]): контейнер — как у прочих значений, о беде говорит знак цвета warning.
    expect(chip.parentElement!.className).toContain('bg-secondary-container');
    expect(chip.parentElement!.className).not.toContain('warning');
    expect(chip.querySelector('svg')!.getAttribute('class')).toContain('text-warning');
    expect(screen.getByLabelText('нет в справочнике')).toBeTruthy();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('помеченный чип отличим от чипа, который не выполнится, значком, а не только цветом', () => {
    const { container } = chips({
      type: 'group', logic: 'and', children: [
        cond('eq', { value: 'Ленина 5' }),
        { type: 'condition', column: 'Итого', op: 'gt', value: 'много' },
      ],
    });
    expect(container.querySelectorAll('[data-notice]')).toHaveLength(1);
    expect(screen.getAllByLabelText('нет в справочнике')).toHaveLength(1);
    // Значок узнаётся по пути: класса с именем, как у lucide, у значка набора нет.
    const drawn = (icon: IconDef) => [...container.querySelectorAll('path')].filter(p => p.getAttribute('d') === icon.d);
    expect(drawn(Warning)).toHaveLength(1);
    expect(drawn(SearchOff)).toHaveLength(1);
  });

  it('в окошке чипа — замена на похожее; помеченный отбор применяется', () => {
    const onChange = vi.fn<(c: FilterChange) => void>();
    chips(cond('eq', { value: 'Березовая 4' }), onChange);
    fireEvent.click(screen.getAllByRole('button', { name: /Березовая 4/ })[0]);
    const replace = screen.getByRole('button', { name: 'Заменить на «Берёзовая 4»' });
    expect(replace.closest('[id]')!.className).toContain('text-warning');
    fireEvent.click(replace);
    expect(screen.queryByRole('button', { name: /Заменить на/ })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Применить' }));
    const change = onChange.mock.calls[0][0];
    expect(change(cond('eq', { value: 'Березовая 4' }))).toMatchObject({ children: [cond('eq', { value: 'Берёзовая 4' })] });
  });

  it('пометка не запирает «Применить»', () => {
    const onChange = vi.fn<(c: FilterChange) => void>();
    chips(cond('eq', { value: 'Ленина 5' }), onChange);
    fireEvent.click(screen.getAllByRole('button', { name: /Ленина 5/ })[0]);
    expect(screen.getByText(/«Ленина 5» — нет в справочнике/)).toBeTruthy();
    const apply = screen.getByRole('button', { name: 'Применить' }) as HTMLButtonElement;
    expect(apply.disabled).toBe(false);
    fireEvent.click(apply);
    expect(onChange).toHaveBeenCalledTimes(1);
  });
});
