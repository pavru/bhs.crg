// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { ViewerToolbar, type ToolbarProps } from './ViewerToolbar';

/**
 * Панель просмотрщика (issue #1490, [UI-11], вариант В): блок страниц — только у многостраничного
 * документа; в узкой колонке `−` и `+` уходят в меню масштаба.
 */

afterEach(cleanup);

const props = (over: Partial<ToolbarProps> = {}): ToolbarProps => ({
  numPages: 4, page: 1, onPage: vi.fn(), mode: 'width', onMode: vi.fn(), onZoomIn: vi.fn(), onZoomOut: vi.fn(),
  canZoomIn: true, canZoomOut: true, onRotate: vi.fn(), narrow: false, ...over,
});

/** Radix открывает меню по нажатию указателя, а не по click. */
function openZoomMenu() {
  const trigger = screen.getByTestId('viewer-zoom');
  fireEvent.pointerDown(trigger, { button: 0, pointerType: 'mouse' });
  return screen.findByRole('menu');
}

describe('панель просмотрщика', () => {
  it('одностраничный документ — без блока страниц', () => {
    render(<ViewerToolbar {...props({ numPages: 1 })} />);
    expect(screen.queryByRole('textbox', { name: 'Номер страницы' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Следующая страница' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Повернуть по часовой' })).toBeTruthy();
  });

  it('широкая — `−` и `+` на панели, в меню их нет', async () => {
    render(<ViewerToolbar {...props()} />);
    expect(screen.getByRole('button', { name: 'Уменьшить' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Увеличить' })).toBeTruthy();
    const menu = await openZoomMenu();
    expect(menu.textContent).not.toContain('Увеличить');
  });

  it('узкая — `−` и `+` в меню масштаба, страницы и поворот на месте', async () => {
    const p = props({ narrow: true });
    render(<ViewerToolbar {...p} />);
    expect(screen.queryByRole('button', { name: 'Уменьшить' })).toBeNull();
    expect(screen.getByRole('textbox', { name: 'Номер страницы' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Повернуть по часовой' })).toBeTruthy();
    await openZoomMenu();
    fireEvent.click(screen.getByRole('menuitem', { name: /Увеличить/ }));
    expect(p.onZoomIn).toHaveBeenCalled();
  });

  it('меню масштаба: отмечен текущий режим, пресет выбирается', async () => {
    const p = props({ mode: 1.5 });
    render(<ViewerToolbar {...p} />);
    expect(screen.getByTestId('viewer-zoom').textContent).toContain('150 %');
    await openZoomMenu();
    fireEvent.click(screen.getByRole('menuitem', { name: /Страница целиком/ }));
    expect(p.onMode).toHaveBeenCalledWith('page');
  });

  it('Esc в поле номера возвращает текущую страницу и не переходит', () => {
    const p = props({ page: 2 });
    render(<ViewerToolbar {...p} />);
    const field = screen.getByRole('textbox', { name: 'Номер страницы' }) as HTMLInputElement;
    fireEvent.focus(field);
    fireEvent.change(field, { target: { value: '4' } });
    fireEvent.keyDown(field, { key: 'Escape' });
    expect(field.value).toBe('2');
    expect(p.onPage).not.toHaveBeenCalled();
  });

  it('набор после Enter без ухода из поля виден и переходит на набранное', () => {
    const p = props();
    const { rerender } = render(<ViewerToolbar {...p} />);
    const field = screen.getByRole('textbox', { name: 'Номер страницы' }) as HTMLInputElement;
    fireEvent.focus(field);
    fireEvent.change(field, { target: { value: '3' } });
    fireEvent.keyDown(field, { key: 'Enter' });
    expect(p.onPage).toHaveBeenLastCalledWith(3);
    rerender(<ViewerToolbar {...p} page={3} />);

    // Фокус остался в поле, onFocus второй раз не приходит.
    fireEvent.change(field, { target: { value: '4' } });
    expect(field.value).toBe('4');
    fireEvent.keyDown(field, { key: 'Enter' });
    expect(p.onPage).toHaveBeenLastCalledWith(4);
  });
});
