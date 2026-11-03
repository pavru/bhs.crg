// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { DateInput } from './DateInput';

vi.mock('@/shared/hooks/useLocale', async importOriginal => ({
  ...(await importOriginal<typeof import('@/shared/hooks/useLocale')>()),
  useLocale: () => ['ru-RU', () => {}, 'idle'],
}));

/** Слой календаря у поля ([UI-80], макет А md3-16): поверхность docked, размер по месту, Esc в списке. */

beforeAll(() => {
  // Календарь — поповер Radix: jsdom не знает ни наблюдателя размеров, ни захвата указателя.
  globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.scrollIntoView ??= () => {};
});
afterEach(() => cleanup());

function open() {
  render(<DateInput label="Дата оплаты" value="2026-11-12" onChange={() => {}} />);
  fireEvent.click(screen.getByRole('button', { name: 'Открыть календарь' }));
  return screen.getByRole('dialog');
}

describe('DateInput: слой календаря', () => {
  it('поверхность surface-container-high уровня 3, радиус 16; не больше места у поля', () => {
    const layer = open().className.split(/\s+/);
    expect(layer).toEqual(expect.arrayContaining([
      'bg-surface-container-high', 'shadow-elevation-3', 'rounded-lg', 'overflow-y-auto',
      'max-h-(--radix-popover-content-available-height)', 'max-w-(--radix-popover-content-available-width)',
    ]));
    expect(layer).not.toContain('bg-surface-container');
  });

  it('Esc в списке месяцев — к дням, календарь открыт; второй Esc его закрывает', () => {
    open();
    fireEvent.click(screen.getByRole('button', { name: /выбрать месяц$/ }));
    fireEvent.keyDown(screen.getByRole('listbox', { name: 'Выбор месяца' }), { key: 'Escape' });
    expect(screen.getByRole('grid', { name: 'Календарь' })).toBeTruthy();
    expect(screen.queryByRole('dialog')).toBeTruthy();
    fireEvent.keyDown(screen.getByRole('grid', { name: 'Календарь' }), { key: 'Escape' });
    expect(screen.queryByRole('dialog')).toBeNull();
  });
});
