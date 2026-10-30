// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { Button, IconButton } from './Button';
import type { ButtonVariant } from './buttonClasses';

afterEach(cleanup);

const classes = (el: HTMLElement) => new Set(el.className.split(/\s+/).filter(Boolean));
const set = (s: string) => new Set(s.split(/\s+/));

// Вид кнопок до выноса общих констант ([UI-83], design D1): тот же набор классов, порядок не важен.
const BASE = 'inline-flex items-center justify-center gap-2 rounded-full font-medium select-none state-layer focus-ring disabled:pointer-events-none whitespace-nowrap';
const EXPECTED: Record<ButtonVariant, string> = {
  filled: `${BASE} h-9 px-4 text-body-medium bg-primary text-on-primary shadow-elevation-1 disabled-container`,
  tonal: `${BASE} h-9 px-4 text-body-medium bg-secondary-container text-on-secondary-container disabled-container`,
  outlined: `${BASE} h-9 px-4 text-body-medium border border-outline text-primary disabled-outlined`,
  text: `${BASE} h-9 px-3 text-body-medium text-primary disabled-content`,
};

describe('Button: вид не зависит от того, где лежат общие классы', () => {
  it.each(Object.keys(EXPECTED) as ButtonVariant[])('вариант %s — прежний набор классов', variant => {
    render(<Button variant={variant}>Сохранить</Button>);
    expect(classes(screen.getByRole('button'))).toEqual(set(EXPECTED[variant]));
  });

  it('опасная filled — заливка ошибки и прежняя тень', () => {
    render(<Button variant="filled" danger>Удалить</Button>);
    expect(classes(screen.getByRole('button')))
      .toEqual(set(`${BASE} h-9 px-4 text-body-medium bg-error text-on-error shadow-elevation-1 disabled-container`));
  });

  it('кнопка-иконка — прежний набор классов', () => {
    render(<IconButton label="Закрыть">×</IconButton>);
    expect(classes(screen.getByRole('button'))).toEqual(set(
      'inline-flex items-center justify-center rounded-full shrink-0 h-9 w-9 state-layer focus-ring disabled-content disabled:pointer-events-none text-on-surface-variant'));
  });
});
