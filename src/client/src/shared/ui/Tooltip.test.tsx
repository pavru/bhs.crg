// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { Button, IconButton } from './Button';
import { Modal } from './Modal';
import { Tooltip } from './Tooltip';

/**
 * Подсказка вместо нативного `title` (issue #1394): видна при фокусе с клавиатуры, у неактивной
 * кнопки объясняет, почему нельзя, и не отнимает у кнопки-иконки имя.
 */

afterEach(cleanup);

async function focus(el: HTMLElement) {
  await act(async () => { el.focus(); });
}
const tip = () => screen.queryByRole('tooltip');

describe('Tooltip', () => {
  it('без текста — триггер как есть, без обёртки и подсказки', () => {
    const { container } = render(<Tooltip content=""><button type="button">Да</button></Tooltip>);
    expect(container.firstElementChild?.tagName).toBe('BUTTON');
  });

  it('фокус с клавиатуры показывает подсказку — нативный title этого не умел', async () => {
    render(<Tooltip content="Сохранить черновик"><button type="button">Сохранить</button></Tooltip>);
    await focus(screen.getByRole('button', { name: 'Сохранить' }));
    expect(tip()?.textContent).toBe('Сохранить черновик');
  });

  it('невидимый фокус (вернуло окно, щелчок мышью) подсказку не открывает — Esc остаётся окну', async () => {
    const matches = Element.prototype.matches;
    const spy = vi.spyOn(Element.prototype, 'matches').mockImplementation(function (this: Element, s: string) {
      return s === ':focus-visible' ? false : matches.call(this, s);
    });
    try {
      render(<Tooltip content="Сохранить черновик"><button type="button">Сохранить</button></Tooltip>);
      await focus(screen.getByRole('button', { name: 'Сохранить' }));
      expect(tip()).toBeNull();
    } finally { spy.mockRestore(); }
  });
});

describe('Button и IconButton', () => {
  it('title кнопки — наша подсказка, браузерной в разметке нет', async () => {
    render(<Button title="Создать запись общих данных">Вынести</Button>);
    const button = screen.getByRole('button', { name: 'Вынести' });
    expect(button.hasAttribute('title')).toBe(false);
    await focus(button);
    expect(tip()?.textContent).toBe('Создать запись общих данных');
  });

  it('у кнопки без подписи подсказка — ещё и имя', () => {
    render(<Button icon={<svg />} title="Удалить строку" />);
    expect(screen.getByRole('button', { name: 'Удалить строку' })).toBeTruthy();
  });

  it('неактивная кнопка объясняет причину: подсказка на обёртке, до неё доходит Tab', async () => {
    render(<Button disabled title="Нечего выносить — объект пуст">Вынести</Button>);
    const button = screen.getByRole('button', { name: 'Вынести' }) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    const wrapper = button.parentElement!;
    expect(wrapper.tabIndex).toBe(0);
    await focus(wrapper);
    expect(tip()?.textContent).toBe('Нечего выносить — объект пуст');
  });

  it('IconButton: подпись — и имя, и подсказка; своя title её заменяет', async () => {
    render(<><IconButton label="Закрыть"><svg /></IconButton><IconButton label="Ещё" title="Действия строки"><svg /></IconButton></>);
    const close = screen.getByRole('button', { name: 'Закрыть' });
    expect(close.hasAttribute('title')).toBe(false);
    await focus(close);
    expect(tip()?.textContent).toBe('Закрыть');
    await focus(screen.getByRole('button', { name: 'Ещё' }));
    expect(tip()?.textContent).toBe('Действия строки');
  });

  it('в окне Esc закрывает подсказку, а не окно', async () => {
    const onOpenChange = vi.fn();
    render(
      <Modal open onOpenChange={onOpenChange} title="Окно" nothingToLose>
        <IconButton label="Обновить"><svg /></IconButton>
      </Modal>,
    );
    await focus(screen.getByRole('button', { name: 'Обновить' }));
    expect(tip()).not.toBeNull();
    fireEvent.keyDown(document.activeElement!, { key: 'Escape' });
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    expect(tip()).toBeNull();
    expect(onOpenChange).not.toHaveBeenCalled();
  });
});
