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

/** Фокус с клавиатуры: тестовый DOM, как браузер, после щелчка считает фокус невидимым. */
async function focus(el: HTMLElement) {
  fireEvent.keyDown(document.body, { key: 'Tab' });
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
    render(<Tooltip content="Сохранить черновик"><button type="button">Сохранить</button></Tooltip>);
    // Последним был щелчок, а не клавиша: фокус из кода после него не виден, как в браузере.
    fireEvent.click(document.body);
    const button = screen.getByRole('button', { name: 'Сохранить' });
    await act(async () => { button.focus(); });
    expect(button.matches(':focus-visible')).toBe(false);
    expect(tip()).toBeNull();
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

  it('неактивная кнопка объясняет причину: доступна мыши и Tab, называется недоступной, нажатие ничего не делает', async () => {
    const onClick = vi.fn();
    const onSubmit = vi.fn((e: { preventDefault: () => void }) => e.preventDefault());
    render(
      <form onSubmit={onSubmit}>
        <Button type="submit" disabled title="Нечего выносить — объект пуст" onClick={onClick}>Вынести</Button>
      </form>,
    );
    const button = screen.getByRole('button', { name: 'Вынести' }) as HTMLButtonElement;
    expect(button.disabled).toBe(false);
    expect(button.getAttribute('aria-disabled')).toBe('true');
    expect(button.tabIndex).toBe(0);
    fireEvent.click(button);
    expect(onClick).not.toHaveBeenCalled();
    expect(onSubmit).not.toHaveBeenCalled();
    await focus(button);
    expect(tip()?.textContent).toBe('Нечего выносить — объект пуст');
  });

  it('переключение disabled не пересоздаёт кнопку и не оборачивает её — место в раскладке то же', () => {
    const { container, rerender } = render(<div><Button disabled title="Почему" className="ml-auto">Сохранить</Button></div>);
    const before = screen.getByRole('button', { name: 'Сохранить' });
    expect(before.parentElement).toBe(container.firstElementChild);
    rerender(<div><Button title="Почему" className="ml-auto">Сохранить</Button></div>);
    const after = screen.getByRole('button', { name: 'Сохранить' });
    expect(after).toBe(before);
    expect(after.hasAttribute('aria-disabled')).toBe(false);
  });

  it('без причины неактивная кнопка — обычная disabled', () => {
    render(<Button disabled>Сохранить</Button>);
    expect((screen.getByRole('button', { name: 'Сохранить' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('title кнопки с подписью — её постоянное описание для скринридера, не только пока открыта подсказка', () => {
    render(<Button title="Письмо не отправляется">Проверить</Button>);
    expect(screen.getByRole('button', { name: 'Проверить' }).getAttribute('aria-description')).toBe('Письмо не отправляется');
  });

  it('условная подпись, давшая false, — не подпись: имя кнопке даёт title', () => {
    const compact = true;
    render(<Button icon={<svg />} title="Удалить строку">{!compact && 'Удалить'}</Button>);
    expect(screen.getByRole('button', { name: 'Удалить строку' })).toBeTruthy();
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
