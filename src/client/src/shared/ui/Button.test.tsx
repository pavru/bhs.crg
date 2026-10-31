// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { Button, IconButton } from './Button';
import type { ButtonVariant } from './buttonClasses';
import { Close, Star } from './icons';

afterEach(cleanup);

const classes = (el: HTMLElement) => el.className.split(/\s+/).filter(Boolean);
const heights = (el: HTMLElement) => classes(el).filter(c => /^(?:min-)?h-/.test(c));
const VARIANTS: ButtonVariant[] = ['filled', 'tonal', 'outlined', 'text'];

describe('Button: common button MD3 ([UI-68])', () => {
  it.each(VARIANTS)('вариант %s — одна высота 40 и подпись label-large', variant => {
    render(<Button variant={variant}>Сохранить</Button>);
    const button = screen.getByRole('button');
    expect(heights(button)).toEqual(['h-10']);
    expect(classes(button)).toContain('text-label-large');
  });

  it('многострочная — та же высота нижней границей', () => {
    render(<Button variant="outlined" multiline>Длинная подпись</Button>);
    expect(heights(screen.getByRole('button'))).toEqual(['min-h-10']);
  });

  it('у filled нет тени без наведения; уровень 1 — только при наведении', () => {
    render(<Button variant="filled">Сохранить</Button>);
    const shadows = classes(screen.getByRole('button')).filter(c => c.includes('shadow'));
    expect(shadows).toEqual(['hover:shadow-elevation-1']);
  });

  it('опасная filled — на роли error', () => {
    render(<Button variant="filled" danger>Удалить</Button>);
    expect(classes(screen.getByRole('button'))).toEqual(expect.arrayContaining(['bg-error', 'text-on-error']));
  });

  it('поля по MD3: 24, со стороны знака 16; у text — 12', () => {
    render(<>
      <Button variant="tonal">Без знака</Button>
      <Button variant="tonal" icon={<svg />}>Со знаком</Button>
      <Button variant="text">Текст</Button>
    </>);
    expect(classes(screen.getByRole('button', { name: 'Без знака' }))).toEqual(expect.arrayContaining(['pl-6', 'pr-6']));
    expect(classes(screen.getByRole('button', { name: 'Со знаком' }))).toEqual(expect.arrayContaining(['pl-4', 'pr-6']));
    expect(classes(screen.getByRole('button', { name: 'Текст' }))).toContain('px-3');
  });
});

describe('IconButton: icon button MD3 ([UI-68])', () => {
  it('label — имя кнопки; размер — переменная места, по умолчанию 40', () => {
    render(<IconButton label="Закрыть" icon={Close} />);
    const button = screen.getByRole('button', { name: 'Закрыть' });
    expect(classes(button)).toContain('size-[var(--md-comp-icon-button-size,40px)]');
    expect(button.hasAttribute('aria-pressed')).toBe(false);
  });

  it('selected — переключатель: aria-pressed и залитый знак', () => {
    const { rerender } = render(<IconButton label="Избранное" icon={Star} selected={false} />);
    const button = () => screen.getByRole('button', { name: 'Избранное' });
    const path = () => button().querySelector('path')!.getAttribute('d');
    expect(button().getAttribute('aria-pressed')).toBe('false');
    expect(path()).toBe(Star.d);
    rerender(<IconButton label="Избранное" icon={Star} selected />);
    expect(button().getAttribute('aria-pressed')).toBe('true');
    expect(path()).toBe(Star.fill);
    expect(classes(button())).toContain('text-primary');
  });

  it('виды: filled — primary, outlined — контур', () => {
    render(<><IconButton label="А" icon={Close} variant="filled" /><IconButton label="Б" icon={Close} variant="outlined" /></>);
    expect(classes(screen.getByRole('button', { name: 'А' }))).toEqual(expect.arrayContaining(['bg-primary', 'text-on-primary']));
    expect(classes(screen.getByRole('button', { name: 'Б' }))).toEqual(expect.arrayContaining(['border', 'border-outline']));
  });

  it('loading — индикатор вместо знака, кнопка недоступна', () => {
    render(<IconButton label="Обновить" icon={Close} loading />);
    const button = screen.getByRole('button', { name: 'Обновить' }) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    expect(button.querySelector('circle')).not.toBeNull();
  });
});
