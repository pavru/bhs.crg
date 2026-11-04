// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { SelectionBar } from './SelectionBar';
import { LinkIcon } from './icons';

/** Контекстная шапка выбора ([UI-81], sharedui-md3-17 PR 0, макет А развилки Р8). */

const realMatchMedia = window.matchMedia;

/** Ширина окна для `useMedia`: в jsdom своего `matchMedia` нет. */
function mockWidth(wide: boolean) {
  window.matchMedia = vi.fn().mockImplementation((q: string) => ({
    matches: q.includes('min-width') ? wide : !wide, addEventListener() {}, removeEventListener() {},
  })) as unknown as typeof window.matchMedia;
}

beforeEach(() => mockWidth(true));
afterEach(() => {
  cleanup();
  window.matchMedia = realMatchMedia;
});

const live = (c: HTMLElement) => c.querySelector('[aria-live="polite"]')!;

describe('SelectionBar', () => {
  it('пустой выбор — шапка области из children; выбор — полоса на её месте', () => {
    const { rerender } = render(<SelectionBar count={0} onClear={vi.fn()}><h2>Материалы комплекта</h2></SelectionBar>);
    expect(screen.getByRole('heading', { name: 'Материалы комплекта' })).toBeTruthy();
    expect(screen.queryByRole('region')).toBeNull();
    rerender(<SelectionBar count={3} onClear={vi.fn()}><h2>Материалы комплекта</h2></SelectionBar>);
    expect(screen.queryByRole('heading', { name: 'Материалы комплекта' })).toBeNull();
    const bar = screen.getByRole('region', { name: 'Выбор строк' });
    expect(bar.className).toContain('bg-surface-container-high');
    expect(screen.getByText('Выбрано: 3', { selector: 'p' }).className).toContain('text-title-large');
  });

  it('живая строка стоит и при пустом выборе — первый флажок диктор слышит; имя полосы не меняется', () => {
    const { container, rerender } = render(<SelectionBar count={0} onClear={vi.fn()} />);
    const status = live(container);
    expect(status.textContent).toBe('');
    rerender(<SelectionBar count={1} onClear={vi.fn()} />);
    expect(live(container)).toBe(status);
    expect(status.textContent).toBe('Выбрано: 1');
    rerender(<SelectionBar count={2} onClear={vi.fn()} />);
    expect(status.textContent).toBe('Выбрано: 2');
    expect(screen.getByRole('region', { name: 'Выбор строк' })).toBeTruthy();
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

  it('действия — у замыкающего края; note — строкой warning под полосой, абзацы внутри — без вложенного <p>', () => {
    const onClick = vi.fn();
    const { rerender } = render(
      <SelectionBar count={3} onClear={vi.fn()} actions={[{ label: 'Связать выбранные', icon: LinkIcon, onClick, variant: 'filled' }]} />);
    fireEvent.click(screen.getByRole('button', { name: 'Связать выбранные' }));
    expect(onClick).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('region').querySelector('.text-warning')).toBeNull();
    rerender(<SelectionBar count={3} onClear={vi.fn()} note={<p>Шире выбранного уровня — 1</p>} />);
    const text = screen.getByText('Шире выбранного уровня — 1');
    const note = text.parentElement!.parentElement!;
    expect(note.className).toContain('text-warning');
    expect(note.querySelector('svg')).toBeTruthy();
    expect(text.parentElement!.closest('p')).toBeNull();
    // Строка — внутри области: предупреждение читается вместе с числом.
    expect(screen.getByRole('region').contains(note)).toBe(true);
  });

  it('на телефоне действия — кнопками-значками с той же подписью', () => {
    mockWidth(false);
    render(<SelectionBar count={2} onClear={vi.fn()} actions={[{ label: 'Разорвать', icon: LinkIcon, onClick: vi.fn() }]} />);
    const button = screen.getByRole('button', { name: 'Разорвать' });
    expect(button.textContent).toBe('');
  });

  it('полоса липкая к верху прокрутки — действия видны и у конца длинного списка', () => {
    render(<SelectionBar count={1} onClear={vi.fn()} />);
    expect(screen.getByRole('region').className).toContain('sticky top-0');
  });
});
