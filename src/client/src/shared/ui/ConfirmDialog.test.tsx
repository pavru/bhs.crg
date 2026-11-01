// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { ConfirmDialog } from './ConfirmDialog';

/** Подтверждение — тот же basic dialog, что окно `Modal` ([UI-75]): вид не расходится между ними. */

afterEach(cleanup);

const props = { open: true, onOpenChange: vi.fn(), title: 'Удалить запись?', confirmLabel: 'Удалить запись', onConfirm: vi.fn() };

describe('ConfirmDialog — basic dialog', () => {
  it('поверхность, форма и заголовок ролями MD3, без рамки и размытого затемнения', () => {
    render(<ConfirmDialog {...props} description="Запись уйдёт из списка." />);
    const dialog = screen.getByRole('dialog');
    const own = dialog.className.split(/\s+/);
    expect(own).toEqual(expect.arrayContaining(['bg-surface-container-high', 'rounded-xl', 'shadow-elevation-3', 'max-w-sm']));
    expect(own.some(c => c === 'border' || c.startsWith('border-'))).toBe(false);
    expect(screen.getByText('Удалить запись?').className).toContain('text-headline-small');
    const scrim = document.querySelector<HTMLElement>('.bg-scrim\\/32')!;
    expect(scrim.style.backdropFilter).toBe('');
    // На одном слое с окнами: подтверждение над окном затемняет его, а не ложится под ним.
    expect(scrim.classList.contains('z-50')).toBe(true);
  });

  it('крестика нет: выход — «Отмена», действие справа от неё', () => {
    render(<ConfirmDialog {...props} />);
    expect(screen.queryByRole('button', { name: 'Закрыть' })).toBeNull();
    const buttons = screen.getAllByRole('button').map(b => b.textContent);
    expect(buttons).toEqual(['Отмена', 'Удалить запись']);
  });

  it('«Понятно» отказа — text, как у окна-ответа `Modal`', () => {
    render(<ConfirmDialog {...props} blocked="Запись используется в трёх документах." />);
    const ok = screen.getByRole('button', { name: 'Понятно' });
    expect(ok.className).not.toMatch(/\bbg-(primary|secondary-container)\b/);
  });

  it('длинный каскад прокручивается в теле, а заголовок и кнопки остаются на месте', () => {
    render(<ConfirmDialog {...props} description={<ul>{Array.from({ length: 40 }, (_, i) => <li key={i}>строка {i}</li>)}</ul>} />);
    const scroll = screen.getByText('строка 0').closest('.overflow-y-auto');
    expect(scroll).not.toBeNull();
    expect(scroll!.contains(screen.getByText('Удалить запись?'))).toBe(false);
    expect(scroll!.contains(screen.getByRole('button', { name: 'Удалить запись' }))).toBe(false);
  });
});
