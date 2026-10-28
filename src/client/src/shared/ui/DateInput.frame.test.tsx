// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { DateInput } from './DateInput';

vi.mock('@/shared/hooks/useLocale', async importOriginal => ({
  ...(await importOriginal<typeof import('@/shared/hooks/useLocale')>()),
  useLocale: () => ['ru-RU', () => {}, 'idle'],
}));

/** Слой обвязки (issue #1464): рамку рисует поле по `size`, как Select, — экраны своих не рисуют. */

afterEach(() => cleanup());
const group = () => screen.getByRole('group', { name: 'Дата' });
const classes = () => group().className.split(/\s+/);

describe('DateInput: обвязка size', () => {
  it('md — h-9 с рамкой и крестиком у заполненного; sm — h-7, без крестика', () => {
    render(<DateInput label="Дата" value="2026-11-03" onChange={() => {}} size="md" />);
    expect(classes()).toEqual(expect.arrayContaining(['h-9', 'border', 'border-outline', 'rounded-md']));
    expect(screen.queryByRole('button', { name: 'Очистить дату' })).toBeTruthy();
    cleanup();
    render(<DateInput label="Дата" value="2026-11-03" onChange={() => {}} size="sm" />);
    expect(classes()).toEqual(expect.arrayContaining(['h-7', 'border']));
    expect(screen.queryByRole('button', { name: 'Очистить дату' })).toBeNull();
  });

  it('invalid — красная рамка и aria-invalid у сегментов; readOnly — фон muted без календаря', () => {
    render(<DateInput label="Дата" value="2026-11-03" onChange={() => {}} size="md" invalid />);
    expect(classes()).toContain('border-error');
    expect(screen.getByPlaceholderText('ДД').getAttribute('aria-invalid')).toBe('true');
    cleanup();
    render(<DateInput label="Дата" value="2026-11-03" onChange={() => {}} size="md" readOnly />);
    expect(classes()).toContain('bg-surface-container');
    expect(screen.queryByRole('button', { name: 'Открыть календарь' })).toBeNull();
  });

  it('ошибка остаётся красной и при открытом календаре; у sm значок 24 px с зоной нажатия 32 px', () => {
    globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
    render(<DateInput label="Дата" value="2026-11-03" onChange={() => {}} size="sm" invalid />);
    const trigger = screen.getByRole('button', { name: 'Открыть календарь' });
    expect(trigger.className.split(/\s+/)).toEqual(expect.arrayContaining(['h-6', 'w-6', 'before:-inset-1']));
    fireEvent.click(trigger);
    expect(classes()).toContain('ring-error');
    expect(classes()).not.toContain('ring-primary');
  });

  it('без size рамки нет — её рисует оболочка или ячейка', () => {
    render(<DateInput label="Дата" value="2026-11-03" onChange={() => {}} className="w-full" />);
    expect(classes()).not.toContain('border');
  });
});
