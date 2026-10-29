// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import * as Popover from '@radix-ui/react-popover';
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

  it('условная подсказка не пересоздаёт триггер — фокус остаётся на нём', () => {
    const { rerender } = render(<Tooltip content={undefined}><button type="button">Сохранить</button></Tooltip>);
    const before = screen.getByRole('button', { name: 'Сохранить' });
    before.focus();
    rerender(<Tooltip content="Идёт сохранение"><button type="button">Сохранить</button></Tooltip>);
    expect(screen.getByRole('button', { name: 'Сохранить' })).toBe(before);
    expect(document.activeElement).toBe(before);
  });

  it('у кнопки из одной иконки подсказка — её имя; у подписанной — постоянное описание', () => {
    render(<>
      <Tooltip content="Удалить строку" name><button type="button"><svg /></button></Tooltip>
      <Tooltip content="Без сохранения"><button type="button">Закрыть</button></Tooltip>
      <Tooltip content="Обновить"><button type="button" aria-label="Обновить"><svg /></button></Tooltip>
    </>);
    expect(screen.getByRole('button', { name: 'Удалить строку' }).hasAttribute('aria-description')).toBe(false);
    expect(screen.getByRole('button', { name: 'Закрыть' }).getAttribute('aria-description')).toBe('Без сохранения');
    // Подсказка, повторяющая имя, описанием не ложится: скринридер прочёл бы его дважды.
    expect(screen.getByRole('button', { name: 'Обновить' }).hasAttribute('aria-description')).toBe(false);
  });

  it('неактивная своя кнопка с подсказкой: доступна мыши и Tab, вид — её же неактивный, нажатие ничего не делает', async () => {
    const onClick = vi.fn();
    render(
      <Tooltip content="Нечего сохранять">
        <button type="button" disabled onClick={onClick}
          className="px-2 state-layer disabled-content hover:underline disabled:font-normal disabled:pointer-events-none">Сохранить</button>
      </Tooltip>,
    );
    const button = screen.getByRole('button', { name: 'Сохранить' }) as HTMLButtonElement;
    expect(button.disabled).toBe(false);
    expect(button.getAttribute('aria-disabled')).toBe('true');
    // Утилиты MD3 ловят aria-disabled сами и остаются как есть; disabled: становится безусловным.
    expect(button.className.split(' ').sort()).toEqual(['disabled-content', 'font-normal', 'px-2', 'state-layer']);
    fireEvent.click(button);
    expect(onClick).not.toHaveBeenCalled();
    await focus(button);
    expect(tip()?.textContent).toBe('Нечего сохранять');
  });

  it('вокруг триггера окна и меню: их состояние на кнопке не перебито, запрет меню не снят', async () => {
    render(<>
      <Popover.Root defaultOpen>
        <Tooltip content="Уведомления">
          <Popover.Trigger asChild><button type="button">Колокол</button></Popover.Trigger>
        </Tooltip>
        <Popover.Portal><Popover.Content>Список</Popover.Content></Popover.Portal>
      </Popover.Root>
      <DropdownMenu.Root>
        <Tooltip content="Основа: акт">
          <DropdownMenu.Trigger asChild disabled><button type="button">Основа</button></DropdownMenu.Trigger>
        </Tooltip>
      </DropdownMenu.Root>
    </>);
    expect(screen.getByRole('button', { name: 'Колокол' }).getAttribute('data-state')).toBe('open');
    expect((screen.getByRole('button', { name: 'Основа' }) as HTMLButtonElement).disabled).toBe(true);
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
    // Человек приходит фокусом позже, своим заходом: автофокус открывшегося окна подсказку не зовёт.
    await act(async () => {});
    await focus(screen.getByRole('button', { name: 'Обновить' }));
    expect(tip()).not.toBeNull();
    fireEvent.keyDown(document.activeElement!, { key: 'Escape' });
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    expect(tip()).toBeNull();
    expect(onOpenChange).not.toHaveBeenCalled();
  });
});
