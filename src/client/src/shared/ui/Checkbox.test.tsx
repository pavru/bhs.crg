// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { Checkbox } from './Checkbox';

// Вид флажка MD3 ([UI-69]): нативный input — круг слоя 40 px без своего вида, квадрат 18 рисует соседний span.
// jsdom раскладки не считает, поэтому след 18 px сторожится классами: input 40 px с полями −11 px
// (40 − 2·11 = 18), квадрат — во всю обёртку.
afterEach(cleanup);
const input = (name: string) => screen.getByRole('checkbox', { name }) as HTMLInputElement;
const mark = (name: string) => input(name).nextElementSibling as HTMLElement;
const cls = (el: Element) => el.className.split(/\s+/);

describe('Checkbox MD3', () => {
  it('нативный флажок — круг 40 со слоем, след 18; квадрат — span поверх, скрытый от диктора', () => {
    render(<Checkbox checked={false} onChange={() => {}} aria-label="Строка" />);
    expect(input('Строка').type).toBe('checkbox');
    expect(cls(input('Строка'))).toEqual(expect.arrayContaining(['size-10', '-m-[11px]', 'appearance-none', 'bg-transparent', 'rounded-full', 'state-layer']));
    expect(mark('Строка').getAttribute('aria-hidden')).toBe('true');
    expect(cls(mark('Строка'))).toEqual(expect.arrayContaining(['absolute', 'inset-0', 'border-2', 'border-on-surface-variant', 'rounded-[2px]']));
    expect(mark('Строка').querySelector('svg')).toBeNull();
  });

  it('отмеченный — заливка primary со знаком; «отмечены не все» — тоже, у input indeterminate', () => {
    const { rerender } = render(<Checkbox checked onChange={() => {}} aria-label="Все" />);
    expect(cls(mark('Все'))).toEqual(expect.arrayContaining(['bg-primary', 'text-on-primary']));
    expect(cls(input('Все'))).toContain('[--_state-color:var(--color-primary)]');
    expect(mark('Все').querySelector('svg')).toBeTruthy();
    rerender(<Checkbox checked={false} onChange={() => {}} indeterminate aria-label="Все" />);
    expect(input('Все').indeterminate).toBe(true);
    expect(cls(mark('Все'))).toContain('bg-primary');
  });

  it('ошибка — роль error; отключённый — on-surface 38 %', () => {
    render(<Checkbox checked onChange={() => {}} aria-label="Ошибка" aria-invalid />);
    expect(cls(mark('Ошибка'))).toEqual(expect.arrayContaining(['bg-error', 'text-on-error']));
    expect(cls(input('Ошибка'))).toContain('[--_state-color:var(--color-error)]');
    cleanup();
    render(<Checkbox checked={false} onChange={() => {}} aria-label="Нельзя" disabled />);
    expect(cls(mark('Нельзя'))).toContain('border-on-surface/38');
  });

  it('только для чтения — обводка border-outline-variant без заливки, aria-readonly у input; нажатие гасит место', () => {
    // Как PrimitiveInput: клик отменён, onChange место не пропускает.
    const onChange = vi.fn();
    render(<Checkbox checked onChange={() => {}} aria-label="Заперто" aria-readonly
      onClick={e => { e.preventDefault(); onChange(); }} />);
    expect(input('Заперто').getAttribute('aria-readonly')).toBe('true');
    expect(cls(mark('Заперто'))).toContain('border-outline-variant');
    expect(cls(mark('Заперто')).some(c => c.startsWith('bg-'))).toBe(false);
    expect(cls(mark('Заперто'))).not.toContain('opacity-70');
    fireEvent.click(input('Заперто'));
    expect(onChange).toHaveBeenCalledOnce();
  });

  it('размера sm больше нет', () => {
    // @ts-expect-error — size у флажка удалён: один размер 18 px ([UI-69])
    render(<Checkbox checked={false} onChange={() => {}} aria-label="Мелкий" size="sm" />);
    expect(cls(input('Мелкий'))).toContain('size-10');
  });
});
