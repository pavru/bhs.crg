// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { SelectionBar } from './SelectionBar';

/** Контекстная шапка выбора ([UI-81], sharedui-md3-17 PR 0, макет А развилки Р8). */

afterEach(cleanup);

describe('SelectionBar', () => {
  it('полоса — область с именем «Выбрано: N», подложка surface-container-high, число title-large', () => {
    render(<SelectionBar count={3} onClear={vi.fn()} />);
    const bar = screen.getByRole('region', { name: 'Выбрано: 3' });
    expect(bar.className).toContain('bg-surface-container-high');
    const count = screen.getByText('Выбрано: 3');
    expect(count.className).toContain('text-title-large');
    expect(count.getAttribute('aria-live')).toBe('polite');
  });

  it('✕ у ведущего края снимает выбор; подпись крестика — своя', () => {
    const onClear = vi.fn();
    const { rerender } = render(<SelectionBar count={2} onClear={onClear} />);
    const close = screen.getByRole('button', { name: 'Снять выбор' });
    expect(close.parentElement!.firstElementChild).toBe(close);
    fireEvent.click(close);
    expect(onClear).toHaveBeenCalledTimes(1);
    rerender(<SelectionBar count={2} onClear={onClear} clearLabel="Отменить выбор" />);
    expect(screen.getByRole('button', { name: 'Отменить выбор' })).toBeTruthy();
  });

  it('действия — у замыкающего края; note — строкой warning под полосой, без неё строки нет', () => {
    const { container, rerender } = render(
      <SelectionBar count={3} onClear={vi.fn()} actions={<button type="button">Связать выбранные (3)</button>} />);
    expect(screen.getByRole('button', { name: 'Связать выбранные (3)' })).toBeTruthy();
    expect(container.querySelector('.text-warning')).toBeNull();
    rerender(<SelectionBar count={3} onClear={vi.fn()} note="Шире выбранного уровня — 1" />);
    const note = screen.getByText('Шире выбранного уровня — 1').parentElement!;
    expect(note.className).toContain('text-warning');
    expect(note.querySelector('svg')).toBeTruthy();
    // Строка — внутри области: предупреждение читается вместе с числом.
    expect(screen.getByRole('region').contains(note)).toBe(true);
  });
});
