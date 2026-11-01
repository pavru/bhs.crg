// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { Banner } from './Banner';

/** Сообщение области ([UI-73]): знак в круге тона, текст — `status` (срочное — `alert`), до двух text-действий. */

afterEach(cleanup);

describe('Banner', () => {
  it('без действий — ни кнопок, ни пустой полосы под них', () => {
    const { container } = render(<Banner tone="info">Данные обновляются раз в час.</Banner>);
    expect(screen.queryByRole('button')).toBeNull();
    expect(container.querySelector('.justify-end')).toBeNull();
  });

  it('тон задаёт круг знака и цвет знака — парной ролью контейнера', () => {
    const { container } = render(<Banner tone="warning">Счёт изменили в другой вкладке.</Banner>);
    const circle = container.querySelector('svg')!.parentElement!;
    expect(circle.className).toContain('bg-warning-container');
    expect(circle.className).toContain('text-on-warning-container');
    expect(circle.className).not.toMatch(/(?<![\w-])text-warning\b/);
  });

  it('роль текста: status, у error и срочного — alert', () => {
    const { rerender } = render(<Banner tone="info">Данные обновляются раз в час.</Banner>);
    expect(screen.getByRole('status')).toBeTruthy();
    rerender(<Banner tone="warning" urgent>Не перечитался.</Banner>);
    expect(screen.getByRole('alert').textContent).toBe('Не перечитался.');
    rerender(<Banner tone="error" urgent={false}>Ошибка, но не срочно.</Banner>);
    expect(screen.getByRole('status')).toBeTruthy();
  });

  it('узкая область — действия под текстом, широкая — в строку: ширину меряет сама область', () => {
    // jsdom контейнерных запросов не считает: сторожатся классы; перенос на 343 px видел стенд.
    const { container } = render(<Banner tone="info" actions={[{ label: 'Понятно', onClick: vi.fn() }]}>Текст.</Banner>);
    const root = container.firstElementChild!;
    expect(root.className).toMatch(/(^|\s)@container(\s|$)/);
    const row = root.firstElementChild!;
    expect(row.className).toMatch(/(^|\s)flex-col(\s|$)/);
    expect(row.className).toContain('@lg:flex-row');
  });

  it('у error текст — alert, действия — text-кнопки, не больше двух', () => {
    const onClick = vi.fn();
    render(
      <Banner tone="error" actions={[
        { label: 'Обновить', onClick }, { label: 'Понятно', onClick: vi.fn() }, { label: 'Лишнее', onClick: vi.fn() },
      ]}>
        Счёт не перечитался.
      </Banner>,
    );
    expect(screen.getByRole('alert').textContent).toBe('Счёт не перечитался.');
    expect(screen.getAllByRole('button').map(b => b.textContent)).toEqual(['Обновить', 'Понятно']);
    fireEvent.click(screen.getByRole('button', { name: 'Обновить' }));
    expect(onClick).toHaveBeenCalledOnce();
  });
});
