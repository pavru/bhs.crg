// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { Banner } from './Banner';

/** Сообщение области ([UI-73]): знак в круге тона, текст — `status`, до двух text-действий. */

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

  it('текст доступен как status, действия — text-кнопки, не больше двух', () => {
    const onClick = vi.fn();
    render(
      <Banner tone="error" actions={[
        { label: 'Обновить', onClick }, { label: 'Понятно', onClick: vi.fn() }, { label: 'Лишнее', onClick: vi.fn() },
      ]}>
        Счёт не перечитался.
      </Banner>,
    );
    expect(screen.getByRole('status').textContent).toBe('Счёт не перечитался.');
    expect(screen.getAllByRole('button').map(b => b.textContent)).toEqual(['Обновить', 'Понятно']);
    fireEvent.click(screen.getByRole('button', { name: 'Обновить' }));
    expect(onClick).toHaveBeenCalledOnce();
  });
});
