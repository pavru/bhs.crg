// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import * as Popover from '@radix-ui/react-popover';
import { IconButton } from './Button';

/**
 * Подсказка и фокус, который слой Radix ставит сам, открываясь (ревью PR #1450).
 *
 * Отдельным файлом нарочно: тесты `Tooltip.test.tsx` оставляют тестовому DOM состояние, при котором
 * подсказка после автофокуса не открывалась и без починки — проверка там проходила, ничего не доказав.
 */

afterEach(cleanup);

/** Фокус с клавиатуры: тестовый DOM, как браузер, после щелчка считает фокус невидимым. */
async function focus(el: HTMLElement) {
  fireEvent.keyDown(document.body, { key: 'Tab' });
  await act(async () => { el.focus(); });
}
const tip = () => screen.queryByRole('tooltip');

describe('подсказка и автофокус слоя', () => {
  // Ревью PR #1450: поповер, открытый с клавиатуры, сам ставит фокус на первую кнопку, и её подсказка
  // забирала первый Esc. Фокус, поставленный слоем, подсказку не открывает; Tab после него — открывает.
  it('фокус, поставленный открывшимся слоем, подсказку не открывает — первый Esc закрывает слой', async () => {
    // Открываем по-настоящему с клавиатуры — Tab и Enter: после щелчка фокус невидим, и подсказка
    // не открылась бы и без починки.
    render(
      <Popover.Root>
        <Popover.Trigger asChild><button type="button">Открыть</button></Popover.Trigger>
        <Popover.Portal>
          <Popover.Content aria-label="Слой">
            <IconButton label="Отметить все прочитанными"><span /></IconButton>
            <IconButton label="Очистить все"><span /></IconButton>
          </Popover.Content>
        </Popover.Portal>
      </Popover.Root>,
    );
    const user = userEvent.setup();
    await user.tab();
    await user.keyboard('{Enter}');
    const first = screen.getByRole('button', { name: 'Отметить все прочитанными' });
    expect(document.activeElement).toBe(first);
    // Без этого проверка ничего не доказывает: невидимый фокус подсказку не открыл бы и без починки.
    expect(first.matches(':focus-visible')).toBe(true);
    expect(tip()).toBeNull();
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('dialog', { name: 'Слой' })).toBeNull();
  });

  it('после автофокуса слоя фокус с клавиатуры подсказку открывает', async () => {
    render(
      <Popover.Root defaultOpen>
        <Popover.Portal>
          <Popover.Content>
            <IconButton label="Отметить все прочитанными"><span /></IconButton>
            <IconButton label="Очистить все"><span /></IconButton>
          </Popover.Content>
        </Popover.Portal>
      </Popover.Root>,
    );
    await act(async () => {});
    await focus(screen.getByRole('button', { name: 'Очистить все' }));
    expect(tip()?.textContent).toBe('Очистить все');
  });
});
