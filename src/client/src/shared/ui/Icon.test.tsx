// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { Icon } from './Icon';
import { Close, Database, Delete } from './icons';

// Значок ([UI-60]): встроенный SVG без имени в тексте, скрыт для читалки, заливка — только у значка с ней.
afterEach(cleanup);

describe('Icon', () => {
  it('имя значка не попадает ни в текст кнопки, ни в её имя', () => {
    render(<button type="button"><Icon icon={Delete} size={18} />Удалить</button>);
    const button = screen.getByRole('button', { name: 'Удалить' });
    expect(button.textContent).toBe('Удалить');
    expect(button.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('путь — контур, с filled — вариант заливки', () => {
    const { container, rerender } = render(<Icon icon={Database} />);
    expect(container.querySelector('path')?.getAttribute('d')).toBe(Database.d);
    rerender(<Icon icon={Database} filled />);
    expect(container.querySelector('path')?.getAttribute('d')).toBe(Database.fill);
    expect(Database.fill).not.toBe(Database.d);
  });

  it('filled у значка без заливки — ошибка типа', () => {
    // @ts-expect-error у Close нет варианта -fill в манифесте
    render(<Icon icon={Close} filled />);
  });

  it('цвет — цвет текста, размер — ступень шкалы, по умолчанию 24', () => {
    const { container } = render(<><Icon icon={Close} /><Icon icon={Close} size={18} /></>);
    const [byDefault, small] = container.querySelectorAll('svg');
    expect(byDefault.getAttribute('fill')).toBe('currentColor');
    expect(byDefault.getAttribute('width')).toBe('24');
    expect(small.getAttribute('height')).toBe('18');
    // @ts-expect-error 13 — не ступень шкалы
    render(<Icon icon={Close} size={13} />);
  });

  it('значок со смыслом без подписи — изображение с именем', () => {
    render(<Icon icon={Close} aria-label="Закрыто" />);
    const img = screen.getByRole('img', { name: 'Закрыто' });
    expect(img.hasAttribute('aria-hidden')).toBe(false);
  });

  it('пустое имя — не имя: значок скрыт, а не изображение без имени', () => {
    const { container } = render(<Icon icon={Close} aria-label="" />);
    expect(screen.queryByRole('img')).toBeNull();
    expect(container.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true');
  });
});
