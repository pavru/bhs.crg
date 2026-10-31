// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { Switch } from './Switch';

// Переключатель MD3 ([UI-69]): трек 52×32, ручка 16 → 24 (при нажатии 28), знака на ручке нет, размер один.
afterEach(cleanup);
const sw = (name: string) => screen.getByRole('switch', { name });
const handle = (name: string) => sw(name).querySelector('[aria-hidden] > span') as HTMLElement;
const outline = (name: string) => sw(name).querySelector('[data-outline]');
const cls = (el: Element) => el.className.split(/\s+/);

describe('Switch MD3', () => {
  it('role=switch с aria-checked; нажатие переключает', () => {
    const onChange = vi.fn();
    render(<Switch checked={false} onChange={onChange} label="Абстрактный" />);
    expect(sw('Абстрактный').getAttribute('aria-checked')).toBe('false');
    fireEvent.click(sw('Абстрактный'));
    expect(onChange).toHaveBeenCalledWith(true);
  });

  it('трек 52×32; выключенный — surface-container-highest с обводкой outline, ручка 16 outline', () => {
    render(<Switch checked={false} onChange={() => {}} label="Выкл" />);
    expect(cls(sw('Выкл'))).toEqual(expect.arrayContaining(['h-8', 'w-13', 'rounded-full', 'bg-surface-container-highest']));
    expect(cls(outline('Выкл')!)).toEqual(expect.arrayContaining(['absolute', 'inset-0', 'border-2', 'border-outline']));
    expect(cls(handle('Выкл'))).toEqual(expect.arrayContaining(['size-4', 'bg-outline', 'group-active:size-7']));
  });

  it('отключённый выключенный сохраняет обводку — on-surface 12 %, а не box-shadow, который снимает disabled-container', () => {
    render(<Switch checked={false} onChange={() => {}} label="Нельзя" disabled />);
    expect(cls(sw('Нельзя'))).not.toEqual(expect.arrayContaining(['inset-ring-2']));
    expect(cls(outline('Нельзя')!)).toContain('border-on-surface/12');
  });

  it('у включённого обводки нет', () => {
    render(<Switch checked onChange={() => {}} label="Вкл" />);
    expect(outline('Вкл')).toBeNull();
  });

  it('включённый — трек primary, ручка 24 on-primary справа', () => {
    render(<Switch checked onChange={() => {}} label="Вкл" />);
    expect(sw('Вкл').getAttribute('aria-checked')).toBe('true');
    expect(cls(sw('Вкл'))).toContain('bg-primary');
    expect(cls(handle('Вкл'))).toEqual(expect.arrayContaining(['size-6', 'bg-on-primary']));
    expect(cls(handle('Вкл').parentElement!)).toContain('left-4');
  });

  it('знака на ручке нет', () => {
    render(<Switch checked onChange={() => {}} label="Вкл" />);
    expect(sw('Вкл').querySelector('svg')).toBeNull();
    expect(handle('Вкл').childElementCount).toBe(0);
  });

  it('отключённый не переключается; ручка on-surface 38 %, без слоя', () => {
    const onChange = vi.fn();
    render(<Switch checked={false} onChange={onChange} label="Нельзя" disabled />);
    fireEvent.click(sw('Нельзя'));
    expect(onChange).not.toHaveBeenCalled();
    expect(cls(handle('Нельзя'))).toContain('bg-on-surface/38');
    expect(cls(handle('Нельзя').parentElement!)).not.toContain('state-layer');
  });

  it('размера sm больше нет', () => {
    // @ts-expect-error — size у переключателя удалён: один размер 52×32 ([UI-69])
    render(<Switch checked onChange={() => {}} label="Мелкий" size="sm" />);
    expect(cls(sw('Мелкий'))).toContain('w-13');
  });
});
