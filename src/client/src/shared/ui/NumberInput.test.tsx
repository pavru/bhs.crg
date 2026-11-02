// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { useState, type ReactNode } from 'react';
import { NumberInput } from './NumberInput';
import { FieldDensityContext } from './fieldBox';

/**
 * Число в строке таблицы ([UI-69], [UI-78]): в клетке сетки правки — поле клетки без рамки и черты в покое,
 * вне её — плотное поле 40 с рамкой (шапка колонки матрицы разноски, В5).
 */

afterEach(cleanup);

function Amount({ unit, density }: { unit?: string; density?: 'dense' | 'cell' }) {
  const [value, setValue] = useState('12,5');
  return <NumberInput label="Количество" value={value} onChange={setValue} unit={unit} density={density} />;
}

const inCell = (node: ReactNode) => <FieldDensityContext.Provider value="cell">{node}</FieldDensityContext.Provider>;
const input = () => screen.getByLabelText('Количество');
/** Обёртка оболочки: на ней черта клетки, рядом с полем — рамка плотного. */
const box = () => input().closest('label')!.parentElement!;
const classes = (el: Element) => el.className.split(/\s+/);

describe('NumberInput: по месту', () => {
  it('в клетке — 40, body-medium, без рамки и без черты в покое, у фокуса черта primary', () => {
    const { container } = render(inCell(<Amount />));
    expect(classes(input().closest('label')!)).toEqual(expect.arrayContaining(['h-10']));
    expect(classes(input())).toContain('text-body-medium');
    expect(container.querySelector('fieldset')).toBeNull();
    expect(classes(box())).toEqual(expect.arrayContaining(['after:h-0.5', 'after:bg-transparent', 'focus-within:after:bg-primary']));
    expect(box().className).not.toContain('after:bg-outline');
  });

  it('вне сетки — плотное поле с рамкой, у фокуса рамка primary', () => {
    const { container } = render(<Amount />);
    const frame = container.querySelector('fieldset')!;
    expect(classes(frame)).toContain('border-outline');
    expect(box().className).not.toContain('after:');
    fireEvent.focus(input());
    expect(classes(container.querySelector('fieldset')!)).toEqual(expect.arrayContaining(['border-primary', 'border-2']));
  });

  it('свой размер сильнее места: плотное в сетке остаётся с рамкой', () => {
    const { container } = render(inCell(<Amount density="dense" />));
    expect(container.querySelector('fieldset')).not.toBeNull();
  });

  it('в клетке имя — aria-label поля, а текстом в клетке его нет; вне сетки — подпись для диктора', () => {
    const { container } = render(inCell(<Amount />));
    expect(input().getAttribute('aria-label')).toBe('Количество');
    expect(container.textContent).toBe('');
    cleanup();
    render(<Amount />);
    expect(input().getAttribute('aria-label')).toBeNull();
    expect(screen.getAllByText('Количество')).toHaveLength(1);
  });

  it('единица — суффикс вне значения и для диктора скрыта', () => {
    render(<Amount unit="м" />);
    const unit = screen.getByText('м');
    expect(unit.getAttribute('aria-hidden')).toBe('true');
    expect(input().closest('label')!.contains(unit)).toBe(true);
    fireEvent.change(input(), { target: { value: '7' } });
    expect((input() as HTMLInputElement).value).toBe('7');
  });
});
