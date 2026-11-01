// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { Delete, Edit } from './icons';
import { Menu, MenuCheckboxItem, MenuItem, MenuLabel, MenuRadioGroup, MenuRadioItem, MenuSeparator } from './Menu';

/** Общее меню ([UI-76], sharedui-md3-13): вид слоя и пункта, клавиатура Radix. */

afterEach(cleanup);

// Закрытие меню и возврат фокуса Radix делает тиком.
const tick = () => new Promise(r => setTimeout(r, 0));

async function open(name = 'Действия') {
  const trigger = screen.getByRole('button', { name });
  await act(async () => { trigger.focus(); fireEvent.keyDown(trigger, { key: 'Enter' }); });
  return trigger;
}

function actions({ dense = false, onEdit = vi.fn() } = {}) {
  render(
    <Menu dense={dense} trigger={<button type="button">Действия</button>}>
      <MenuItem icon={Edit} onSelect={onEdit}>Переименовать</MenuItem>
      <MenuItem disabled description="Нет прав">Скопировать</MenuItem>
      <MenuSeparator />
      <MenuItem icon={Delete} danger>Удалить</MenuItem>
    </Menu>);
  return { onEdit };
}

describe('Menu: слой', () => {
  it('surface-container, радиус 4, уровень 2, без рамки', async () => {
    actions();
    await open();
    const cls = screen.getByRole('menu').className;
    expect(cls).toContain('bg-surface-container');
    expect(cls).toContain('rounded-xs');
    expect(cls).toContain('shadow-elevation-2');
    expect(cls).not.toMatch(/(^|\s)border(\s|-)/);
  });

  it('черта между группами — bg-outline-variant с отступом 8', async () => {
    actions();
    await open();
    expect(screen.getByRole('separator').className).toContain('my-2 h-px bg-outline-variant');
  });
});

describe('Menu: пункт', () => {
  it('48 по умолчанию, 40 в плотном меню; подпись label-large, знак 24', async () => {
    actions();
    await open();
    const item = screen.getByRole('menuitem', { name: 'Переименовать' });
    expect(item.className).toContain('min-h-12');
    expect(item.className).toContain('text-label-large');
    expect(item.querySelector('svg')?.getAttribute('width')).toBe('24');
    cleanup();
    actions({ dense: true });
    await open();
    expect(screen.getByRole('menuitem', { name: 'Переименовать' }).className).toContain('min-h-10');
  });

  it('опасный — тона ошибки; погашенный объясняет строкой под подписью', async () => {
    actions();
    await open();
    expect(screen.getByRole('menuitem', { name: 'Удалить' }).className).toContain('text-error');
    const off = screen.getByRole('menuitem', { name: /Скопировать/ });
    expect(off.getAttribute('data-disabled')).not.toBeNull();
    expect(off.textContent).toContain('Нет прав');
  });

  it('выбранный вариант и флажок — галочкой', async () => {
    render(
      <Menu trigger={<button type="button">Вид</button>}>
        <MenuLabel>Масштаб</MenuLabel>
        <MenuRadioGroup value="page">
          <MenuRadioItem value="width">По ширине</MenuRadioItem>
          <MenuRadioItem value="page">Страница целиком</MenuRadioItem>
        </MenuRadioGroup>
        <MenuCheckboxItem checked>Сетка</MenuCheckboxItem>
      </Menu>);
    await open('Вид');
    const page = screen.getByRole('menuitemradio', { name: 'Страница целиком' });
    expect(page.getAttribute('aria-checked')).toBe('true');
    expect(page.querySelector('svg')).not.toBeNull();
    expect(screen.getByRole('menuitemradio', { name: 'По ширине' }).querySelector('svg')).toBeNull();
    expect(screen.getByRole('menuitemcheckbox', { name: 'Сетка' }).querySelector('svg')).not.toBeNull();
  });
});

describe('Menu: клавиатура', () => {
  it('стрелка вниз переходит к следующему, погашенный перешагивает; Enter выбирает', async () => {
    const { onEdit } = actions();
    await open();
    const first = screen.getByRole('menuitem', { name: 'Переименовать' });
    expect(document.activeElement).toBe(first);
    // Стрелку Radix отрабатывает тиком (setTimeout в RovingFocus).
    await act(async () => { fireEvent.keyDown(first, { key: 'ArrowDown' }); }); await act(tick);
    expect(document.activeElement).toBe(screen.getByRole('menuitem', { name: 'Удалить' }));
    await act(async () => { fireEvent.keyDown(document.activeElement!, { key: 'ArrowUp' }); }); await act(tick);
    await act(async () => { fireEvent.keyDown(document.activeElement!, { key: 'Enter' }); });
    expect(onEdit).toHaveBeenCalledTimes(1);
  });

  it('Esc закрывает меню и возвращает фокус на кнопку', async () => {
    actions();
    const trigger = await open();
    await act(async () => { fireEvent.keyDown(screen.getAllByRole('menuitem')[0], { key: 'Escape' }); }); await act(tick);
    expect(screen.queryByRole('menu')).toBeNull();
    expect(document.activeElement).toBe(trigger);
  });
});
