// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { VariantPicker } from './VariantPicker';

afterEach(cleanup);

const OPTIONS = ['Организация', 'Физическое лицо', 'Индивидуальный предприниматель', 'Иностранная компания']
  .map((label, i) => ({ key: `k${i}`, label, filled: false }));

// Список — радиогруппа с именем и клавиатурой, как сегмент (ревью PR #1709).
describe('VariantPicker списком', () => {
  it('имя группы, одна остановка Tab, стрелка выбирает соседний', () => {
    const onSelect = vi.fn();
    render(<VariantPicker label="Вариант поля «Заказчик»" options={OPTIONS} active="k1" onSelect={onSelect} />);
    const group = screen.getByRole('radiogroup', { name: 'Вариант поля «Заказчик»' });
    const radios = screen.getAllByRole('radio');
    expect(radios.map(r => r.tabIndex)).toEqual([-1, 0, -1, -1]);
    fireEvent.keyDown(radios[1], { key: 'ArrowDown' });
    expect(onSelect).toHaveBeenCalledWith('k2');
    fireEvent.keyDown(group, { key: 'End' });
    expect(onSelect).toHaveBeenLastCalledWith('k3');
  });

  it('confirm: стрелка только двигает фокус, выбор — нажатием', () => {
    const onSelect = vi.fn();
    render(<VariantPicker layout="list" confirm label="Вариант" options={OPTIONS} active="" onSelect={onSelect} />);
    const radios = screen.getAllByRole('radio');
    expect(radios[0].tabIndex).toBe(0);
    radios[0].focus();
    fireEvent.keyDown(radios[0], { key: 'ArrowDown' });
    expect(onSelect).not.toHaveBeenCalled();
    expect(document.activeElement).toBe(radios[1]);
    fireEvent.click(radios[1]);
    expect(onSelect).toHaveBeenCalledWith('k1');
  });
});
