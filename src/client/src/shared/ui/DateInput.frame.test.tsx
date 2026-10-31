// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { DateInput } from './DateInput';

vi.mock('@/shared/hooks/useLocale', async importOriginal => ({
  ...(await importOriginal<typeof import('@/shared/hooks/useLocale')>()),
  useLocale: () => ['ru-RU', () => {}, 'idle'],
}));

/** Поле без оболочки (issue #1464): рамку рисует поле по `density`, как Select, — экраны своих не рисуют.
 *  Размер один — плотное поле 40 ([UI-69]). */

afterEach(() => cleanup());
const group = () => screen.getByRole('group', { name: 'Дата' });
const classes = () => group().className.split(/\s+/);

describe('DateInput: плотное поле', () => {
  it('40 с рамкой формы 4, текст body-medium и крестик у заполненного', () => {
    render(<DateInput label="Дата" value="2026-11-03" onChange={() => {}} density="dense" />);
    expect(classes()).toEqual(expect.arrayContaining(['h-10', 'text-body-medium', 'border', 'border-outline', 'rounded-xs']));
    expect(screen.queryByRole('button', { name: 'Очистить дату' })).toBeTruthy();
  });

  it('invalid — красная рамка и aria-invalid у сегментов; readOnly — обводка border-outline-variant без подложки', () => {
    render(<DateInput label="Дата" value="2026-11-03" onChange={() => {}} density="dense" invalid />);
    expect(classes()).toContain('border-error');
    expect(screen.getByPlaceholderText('ДД').getAttribute('aria-invalid')).toBe('true');
    cleanup();
    render(<DateInput label="Дата" value="2026-11-03" onChange={() => {}} density="dense" readOnly />);
    expect(classes()).toContain('border-outline-variant');
    expect(classes().some(c => c.startsWith('bg-'))).toBe(false);
    expect(screen.queryByRole('button', { name: 'Открыть календарь' })).toBeNull();
  });

  it('ошибка остаётся красной и при открытом календаре; кнопка календаря — 32 px', () => {
    globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
    render(<DateInput label="Дата" value="2026-11-03" onChange={() => {}} density="dense" invalid />);
    const trigger = screen.getByRole('button', { name: 'Открыть календарь' });
    expect(trigger.className.split(/\s+/)).toEqual(expect.arrayContaining(['h-8', 'w-8']));
    fireEvent.click(trigger);
    expect(classes()).toEqual(expect.arrayContaining(['inset-ring', 'inset-ring-error']));
    expect(classes()).not.toContain('inset-ring-primary');
  });

  it('без density рамки нет — её рисует оболочка или ячейка', () => {
    render(<DateInput label="Дата" value="2026-11-03" onChange={() => {}} className="w-full" />);
    expect(classes()).not.toContain('border');
  });
});
