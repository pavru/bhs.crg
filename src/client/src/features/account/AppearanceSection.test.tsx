// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { AppearanceSection } from './AppearanceSection';

/**
 * Язык и формат дат — радиогруппа по APG ([UI-44], #1416): одна остановка Tab, стрелка выбирает
 * соседний вариант. Прежде строки были кнопками с `aria-pressed` — пять независимых переключателей.
 */

let locale = 'ru-RU';
const setLocale = vi.fn((next: string) => { locale = next; });
vi.mock('@/shared/hooks/useLocale', async importOriginal => ({
  ...await importOriginal<typeof import('@/shared/hooks/useLocale')>(),
  useLocale: () => [locale, setLocale, 'idle'],
}));
vi.mock('@/shared/ui/ThemeToggle', () => ({ ThemeToggle: () => null }));
vi.mock('@/shared/ui/themeContext', () => ({ useTheme: () => ({ saveState: 'idle' }) }));

afterEach(() => { cleanup(); vi.clearAllMocks(); locale = 'ru-RU'; });

describe('AppearanceSection — выбор языка', () => {
  it('варианты — радиокнопки, выбранный — единственная остановка Tab', () => {
    render(<AppearanceSection />);
    const group = screen.getByRole('radiogroup', { name: 'Язык и формат дат' });
    const radios = screen.getAllByRole('radio');
    expect(group.contains(radios[0])).toBe(true);
    const checked = radios.filter(r => r.getAttribute('aria-checked') === 'true');
    expect(checked.map(r => r.textContent)).toEqual(['Русский (Россия)']);
    expect(radios.filter(r => r.tabIndex === 0)).toEqual(checked);
    expect(screen.queryByRole('button', { pressed: true })).toBeNull();
  });

  // Значение вне списка не запирает группу: остановка Tab — первый вариант (ревью #1704).
  it('язык вне списка — в группу всё равно ведёт Tab', () => {
    locale = 'fr-FR';
    render(<AppearanceSection />);
    const radios = screen.getAllByRole('radio');
    expect(radios.filter(r => r.tabIndex === 0)).toEqual([radios[0]]);
    fireEvent.keyDown(radios[0], { key: 'ArrowDown' });
    expect(setLocale).toHaveBeenCalledWith('system');
  });

  it('стрелка вниз выбирает соседний язык и переводит на него фокус', () => {
    render(<AppearanceSection />);
    const current = screen.getByRole('radio', { name: 'Русский (Россия)' });
    current.focus();
    fireEvent.keyDown(current, { key: 'ArrowDown' });
    expect(setLocale).toHaveBeenCalledWith('en-US');
    expect(document.activeElement).toBe(screen.getByRole('radio', { name: 'English (US)' }));
  });
});
