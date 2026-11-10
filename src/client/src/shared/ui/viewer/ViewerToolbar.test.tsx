// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { ViewerToolbar, type ToolbarProps } from './ViewerToolbar';

/**
 * Панель просмотрщика (issue #1490, [UI-11], макет А md3-16): блок страниц — только у многостраничного
 * документа; в узкой колонке `−` и `+` уходят в меню масштаба, а чип показывает процент.
 */

afterEach(cleanup);

const props = (over: Partial<ToolbarProps> = {}): ToolbarProps => ({
  numPages: 4, page: 1, onPage: vi.fn(), mode: 'width', onMode: vi.fn(), onZoomIn: vi.fn(), onZoomOut: vi.fn(),
  canZoomIn: true, canZoomOut: true, onRotate: vi.fn(), narrow: false, scale: 0.86, ...over,
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

  it('вид панели: 64 на surface-container без черты, поле номера с суффиксом «/ M»', () => {
    render(<ViewerToolbar {...props({ numPages: 12, page: 3 })} />);
    const bar = screen.getByTestId('viewer-toolbar');
    expect(bar.className).toContain('h-16');
    expect(bar.className).toContain('bg-surface-container');
    expect(bar.className.split(/\s+/)).not.toContain('border-t');
    const field = screen.getByRole('textbox', { name: 'Номер страницы' }) as HTMLInputElement;
    expect(field.value).toBe('3');
    // Суффикс виден как «/ 12», а диктор слышит «из 12» — описанием поля.
    expect(bar.textContent).toContain('/ 12');
    const described = document.getElementById(field.getAttribute('aria-describedby')!.split(' ')[0]);
    expect(described?.textContent).toBe('из 12');
  });

  // Суффикс — не в слоте хвоста кнопки (44, при касании 52): там «/ 1234» налезал на номер, а на
  // сенсорном экране в поле шириной 80 номер обрезался уже с трёх цифр.
  it('поле номера по числу цифр: ширина и отступ под суффикс растут вместе с ним', () => {
    render(<ViewerToolbar {...props({ numPages: 1234, page: 7 })} />);
    const field = screen.getByRole('textbox', { name: 'Номер страницы' });
    // jsdom переставляет слагаемые calc — сверяем их, а не строку целиком.
    const terms = (v: string) => v.replace(/^calc\(|\)$/g, '').split(' + ').sort();
    expect(terms(field.style.paddingRight)).toEqual(['1rem', '6ch']);
    expect(terms((field.closest('[style*="width"]') as HTMLElement).style.width)).toEqual(['1.75rem', '10ch']);
    expect(screen.getByText('/ 1234')).toBeTruthy();
  });

  it('широкая — `−` и `+` на панели, в меню их нет', async () => {
    render(<ViewerToolbar {...props()} />);
    expect(screen.getByRole('button', { name: 'Уменьшить' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Увеличить' })).toBeTruthy();
    const menu = await openZoomMenu();
    expect(menu.textContent).not.toContain('Увеличить');
  });

  it('широкая — чип показывает режим, а не процент', () => {
    render(<ViewerToolbar {...props()} />);
    expect(screen.getByTestId('viewer-zoom').textContent).toContain('По ширине');
  });

  it('узкая — чип показывает действующий масштаб в процентах, режим — в имени и в меню', async () => {
    render(<ViewerToolbar {...props({ narrow: true, scale: 0.857 })} />);
    const chip = screen.getByTestId('viewer-zoom');
    expect(chip.textContent).toContain('86 %');
    expect(chip.textContent).not.toContain('По ширине');
    expect(chip.getAttribute('aria-label')).toBe('Масштаб: 86 %, по ширине');
    await openZoomMenu();
    expect(screen.getByRole('menuitemradio', { name: /По ширине/ }).getAttribute('aria-checked')).toBe('true');
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
    // Режим — выбор из вариантов: галочка у текущего ([UI-76]).
    expect(screen.getByRole('menuitemradio', { name: '150 %' }).getAttribute('aria-checked')).toBe('true');
    expect(screen.getByRole('menuitemradio', { name: /По ширине/ }).getAttribute('aria-checked')).toBe('false');
    fireEvent.click(screen.getByRole('menuitemradio', { name: /Страница целиком/ }));
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

describe('кнопка «Лупа» [UI-11] [UI-92] [UI-94]', () => {
  const loupe = (over: Partial<NonNullable<ToolbarProps['loupe']>> = {}) => ({ on: false, available: true, toggle: vi.fn(), ...over });
  const button = () => screen.getByRole('button', { name: 'Лупа' });

  it.each([false, true])('в панели перед поворотом — и в узкой колонке (%s)', narrow => {
    render(<ViewerToolbar {...props({ narrow, loupe: loupe() })} />);
    const buttons = screen.getAllByRole('button');
    expect(buttons.indexOf(button())).toBe(buttons.indexOf(screen.getByRole('button', { name: 'Повернуть по часовой' })) - 1);
  });

  it('выключена — как соседи, aria-pressed="false"; включена — тональная, aria-pressed="true"', () => {
    const { rerender } = render(<ViewerToolbar {...props({ loupe: loupe() })} />);
    expect(button().getAttribute('aria-pressed')).toBe('false');
    expect(button().className).not.toContain('bg-secondary-container');
    rerender(<ViewerToolbar {...props({ loupe: loupe({ on: true }) })} />);
    expect(button().getAttribute('aria-pressed')).toBe('true');
    expect(button().className).toContain('bg-secondary-container');
  });

  it('нажатие переключает', () => {
    const onToggle = vi.fn();
    render(<ViewerToolbar {...props({ loupe: loupe({ toggle: onToggle }) })} />);
    fireEvent.click(button());
    expect(onToggle).toHaveBeenCalledOnce();
  });

  // Неактивная кнопка с подсказкой — `aria-disabled` (Tooltip): подсказка объясняет, почему нельзя.
  it('при листе от 300 % — неактивна и не переключает', () => {
    const onToggle = vi.fn();
    render(<ViewerToolbar {...props({ scale: 3, loupe: loupe({ available: false, toggle: onToggle }) })} />);
    expect(button().getAttribute('aria-disabled')).toBe('true');
    fireEvent.click(button());
    expect(onToggle).not.toHaveBeenCalled();
  });

  it('без точного указателя кнопки нет', () => {
    render(<ViewerToolbar {...props({ loupe: null })} />);
    expect(screen.queryByRole('button', { name: 'Лупа' })).toBeNull();
  });
});
