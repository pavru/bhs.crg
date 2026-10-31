// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import { NumberInput } from './NumberInput';

/** Число в строке таблицы видом плотного поля клетки ([UI-69], sharedui-md3-11). */

afterEach(cleanup);

function Amount({ unit }: { unit?: string }) {
  const [value, setValue] = useState('12,5');
  return <NumberInput label="Количество" value={value} onChange={setValue} unit={unit} />;
}

const wrap = () => screen.getByLabelText('Количество').closest('label')!;

describe('NumberInput: вид клетки', () => {
  it('40 без рамки: подчёркивание псевдоэлементом, текст body-medium', () => {
    render(<Amount />);
    const input = screen.getByLabelText('Количество');
    expect(wrap().className.split(/\s+/)).toEqual(expect.arrayContaining(['h-10', 'after:h-px', 'after:bg-outline']));
    expect(wrap().className).not.toMatch(/(^|\s)border(\s|$)/);
    expect(input.className.split(/\s+/)).toContain('text-body-medium');
  });

  it('единица — суффикс вне значения и для диктора скрыта', () => {
    render(<Amount unit="м" />);
    const unit = screen.getByText('м');
    expect(unit.getAttribute('aria-hidden')).toBe('true');
    expect(wrap().contains(unit)).toBe(true);
    fireEvent.change(screen.getByLabelText('Количество'), { target: { value: '7' } });
    expect((screen.getByLabelText('Количество') as HTMLInputElement).value).toBe('7');
  });

  it('наведение не перекрашивает подчёркивание фокуса и отключённого', () => {
    // В собранном CSS `hover:` стоит позже `focus-within:` при равном весе — исключение обязано жить в
    // самом селекторе наведения, иначе поле в фокусе под указателем теряет `primary`.
    render(<Amount />);
    const hover = wrap().className.split(/\s+/).filter(c => c.includes('hover'));
    expect(hover).toEqual(['[&:hover:not(:focus-within):not(:has(:disabled))]:after:bg-on-surface']);
    expect(wrap().className.split(/\s+/)).toContain('focus-within:after:bg-primary');
  });
});
