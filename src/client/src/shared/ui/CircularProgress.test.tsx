// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { CircularProgress } from './CircularProgress';

// Индикатор хода ([UI-61]): один — с ролью и именем, рядом с подписью хода — скрыт для читалки.
afterEach(cleanup);

describe('CircularProgress', () => {
  it('один в панели — progressbar с именем', () => {
    render(<CircularProgress label="Загрузка…" />);
    expect(screen.getByRole('progressbar', { name: 'Загрузка…' })).toBeTruthy();
  });

  it('в кнопке — скрыт, имя кнопки остаётся её подписью', () => {
    render(<button type="button" disabled><CircularProgress size={18} />Сохранить</button>);
    expect(screen.queryByRole('progressbar')).toBeNull();
    const button = screen.getByRole('button', { name: 'Сохранить' });
    expect(button.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('толщина дуги — по размеру: дуга целиком внутри квадрата', () => {
    // Радиус = (размер − толщина) / 2: при 18 толщина 2, при 48 — 4.
    const { container } = render(<><CircularProgress size={18} /><CircularProgress size={48} /></>);
    const [small, large] = container.querySelectorAll('circle');
    expect(small.getAttribute('r')).toBe('8');
    expect(large.getAttribute('r')).toBe('22');
  });
});
