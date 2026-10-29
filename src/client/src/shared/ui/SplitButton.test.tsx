// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { SplitButton, type SplitButtonProps } from './SplitButton';
import { variantClass } from './buttonClasses';

/** Split-кнопка ([UI-83]): основное действие одним нажатием, варианты — из стрелки. */

afterEach(() => { cleanup(); vi.restoreAllMocks(); });

const MENU = 'Ещё варианты сохранения';

function setup(props: Partial<SplitButtonProps> = {}) {
  const onClick = vi.fn();
  const onSelect = vi.fn();
  render(
    <SplitButton onClick={onClick} menuLabel={MENU}
      items={[{ key: 'new', label: 'Сохранить как новую версию…', description: 'Создаст v3', onSelect }]}
      {...props}>
      Сохранить
    </SplitButton>,
  );
  return {
    onClick, onSelect,
    main: () => screen.getByRole('button', { name: /Сохранить/ }),
    arrow: () => screen.getByRole('button', { name: MENU }),
  };
}

const classes = (el: HTMLElement) => el.className.split(/\s+/).filter(Boolean);
const look = (v: Parameters<typeof variantClass>[0]) => variantClass(v).split(' ');
const openWithKeyboard = async (el: HTMLElement) => {
  await act(async () => { el.focus(); fireEvent.keyDown(el, { key: 'Enter' }); });
};

describe('SplitButton', () => {
  it('нажатие основной части — действие, меню закрыто', () => {
    const { onClick, main } = setup();
    fireEvent.click(main());
    expect(onClick).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('menu')).toBeNull();
  });

  it('без variant — вид filled, а не прозрачный text у Button', () => {
    const { main, arrow } = setup();
    for (const half of [main(), arrow()]) expect(classes(half)).toEqual(expect.arrayContaining(look('filled')));
  });

  it.each(['filled', 'tonal', 'outlined'] as const)('у обеих частей вид варианта %s — из общих констант', variant => {
    const { main, arrow } = setup({ variant });
    for (const half of [main(), arrow()]) expect(classes(half)).toEqual(expect.arrayContaining(look(variant)));
  });

  it('у половин нет rounded-full рядом с односторонним углом и нет тени', () => {
    const { main, arrow } = setup();
    for (const half of [main(), arrow()]) {
      const cls = classes(half);
      expect(cls).not.toContain('rounded-full');
      expect(cls.some(c => /^rounded-[lr]-/.test(c))).toBe(true);
      expect(cls.some(c => c.includes('shadow'))).toBe(false);
    }
  });

  it('стрелка — кнопка меню с именем; у открытого — data-state="open"', async () => {
    // Открытое меню прячет остальное от диктора (aria-hidden) — стрелку берём до открытия.
    const arrow = setup().arrow();
    expect(arrow.getAttribute('aria-haspopup')).toBe('menu');
    expect(arrow.getAttribute('aria-expanded')).toBe('false');
    await openWithKeyboard(arrow);
    expect(arrow.getAttribute('aria-expanded')).toBe('true');
    expect(arrow.getAttribute('data-state')).toBe('open');
  });

  it('Enter на стрелке — меню, фокус на пункте, выбор зовёт onSelect', async () => {
    const { arrow, onSelect, onClick } = setup();
    await openWithKeyboard(arrow());
    const item = screen.getByRole('menuitem', { name: /Сохранить как новую версию…/ });
    expect(item.textContent).toContain('Создаст v3');
    expect(document.activeElement).toBe(item);
    await act(async () => { fireEvent.keyDown(item, { key: 'Enter' }); });
    expect(onSelect).toHaveBeenCalledTimes(1);
    expect(onClick).not.toHaveBeenCalled();
  });

  it('Esc закрывает меню и возвращает фокус на стрелку', async () => {
    const { arrow } = setup();
    await openWithKeyboard(arrow());
    await act(async () => { fireEvent.keyDown(screen.getByRole('menu'), { key: 'Escape' }); });
    expect(screen.queryByRole('menu')).toBeNull();
    expect(document.activeElement).toBe(arrow());
  });

  it('при loading обе части недоступны, меню не открыть; основная часть объясняет', async () => {
    const { main, arrow, onClick } = setup({ loading: true, title: 'Идёт сохранение' });
    expect(main().getAttribute('aria-disabled')).toBe('true');
    expect(main().getAttribute('aria-description')).toBe('Идёт сохранение');
    expect((arrow() as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(main());
    expect(onClick).not.toHaveBeenCalled();
    fireEvent.pointerDown(arrow(), { button: 0, ctrlKey: false });
    fireEvent.click(arrow());
    await openWithKeyboard(arrow());
    expect(screen.queryByRole('menu')).toBeNull();
  });

  it('без вариантов стрелки нет', () => {
    const error = vi.spyOn(console, 'error').mockImplementation(() => {});
    setup({ items: [] });
    expect(screen.queryByRole('button', { name: MENU })).toBeNull();
    expect(error).toHaveBeenCalled();
  });
});
