// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { NavigationGuardProvider } from './NavigationGuardProvider';
import { useNavigationGuard } from './NavigationGuard';
import { LeaveGuard } from './LeaveGuard';

/**
 * Охрана ухода и её вопрос одним компонентом (ревью PR #1817): вопрос стоит, пока идёт запись, без записи —
 * без кнопки записи, а когда терять стало нечего — уход идёт сам.
 */

afterEach(cleanup);

/** Пункт навигации: уходит через охрану, как ссылки обвязки. */
function NavItem({ onGo }: { onGo: () => void }) {
  const guard = useNavigationGuard();
  return <button type="button" onClick={() => { if (!guard?.attempt(onGo)) onGo(); }}>Счета</button>;
}

type Props = Parameters<typeof LeaveGuard>[0];

function mount(props: Props) {
  const go = vi.fn();
  const view = (p: Props) => (
    <NavigationGuardProvider>
      <LeaveGuard {...p} />
      <NavItem onGo={go} />
    </NavigationGuardProvider>
  );
  const { rerender } = render(view(props));
  return {
    go, rerender: (p: Props) => rerender(view(p)),
    leave: () => fireEvent.click(screen.getByRole('button', { name: 'Счета' })),
  };
}

const dialog = () => screen.queryByRole('dialog', { name: 'Несохранённые изменения' });
const saveButton = () => screen.queryByRole('button', { name: 'Сохранить и перейти' });

/** Запись, которую тест завершает сам. */
function pending() {
  let finish!: (ok: boolean) => void;
  const save = vi.fn(() => new Promise<boolean>(resolve => { finish = resolve; }));
  return { save, finish: (ok: boolean) => act(async () => { finish(ok); }) };
}

describe('охрана', () => {
  it('есть несохранённое — уход спрашивает, текст свой', () => {
    const { go, leave } = mount({ active: true, message: 'Не сохранены строки счёта.', save: vi.fn(async () => true) });
    leave();
    expect(dialog()).toBeTruthy();
    expect(screen.getByText('Не сохранены строки счёта.')).toBeTruthy();
    expect(go).not.toHaveBeenCalled();
  });

  it('нечего терять — уход без вопроса', () => {
    const { go, leave } = mount({ active: false });
    leave();
    expect(dialog()).toBeNull();
    expect(go).toHaveBeenCalledOnce();
  });

  it('перезагрузку вкладки держит вопрос браузера', () => {
    mount({ active: true });
    const event = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(true);
  });

  it('«Не сохранять» уходит без записи; «Отмена» остаётся', () => {
    const save = vi.fn(async () => true);
    const { go, leave } = mount({ active: true, save });
    leave();
    fireEvent.click(screen.getByRole('button', { name: 'Отмена' }));
    expect(go).not.toHaveBeenCalled();
    expect(dialog()).toBeNull();
    leave();
    fireEvent.click(screen.getByRole('button', { name: 'Не сохранять' }));
    expect(go).toHaveBeenCalledOnce();
    expect(save).not.toHaveBeenCalled();
  });
});

describe('«Сохранить и перейти»', () => {
  it('вопрос стоит, пока идёт запись, и уходит после неё', async () => {
    const write = pending();
    const { go, leave } = mount({ active: true, save: write.save });
    leave();
    fireEvent.click(saveButton()!);
    expect(write.save).toHaveBeenCalledOnce();
    // Запись идёт: вопрос открыт и занят — правок под ним нет, второй уход его не дублирует.
    expect(dialog()).toBeTruthy();
    expect(saveButton()!.hasAttribute('disabled')).toBe(true);
    expect(go).not.toHaveBeenCalled();
    await write.finish(true);
    expect(dialog()).toBeNull();
    expect(go).toHaveBeenCalledOnce();
  });

  it('отказ записи уход отменяет', async () => {
    const write = pending();
    const { go, leave } = mount({ active: true, save: write.save });
    leave();
    fireEvent.click(saveButton()!);
    await write.finish(false);
    expect(dialog()).toBeNull();
    expect(go).not.toHaveBeenCalled();
  });

  it('сохранять нечем — кнопки записи нет вовсе', () => {
    const { leave } = mount({ active: true });
    leave();
    expect(dialog()).toBeTruthy();
    expect(saveButton()).toBeNull();
    expect(screen.getByRole('button', { name: 'Не сохранять' })).toBeTruthy();
  });
});

describe('запись, начатая кнопкой страницы', () => {
  it('вопрос ждёт её; терять стало нечего — уход идёт сам', () => {
    const save = vi.fn(async () => true);
    const { go, leave, rerender } = mount({ active: true, saving: true, save });
    leave();
    expect(dialog()).toBeTruthy();
    expect(saveButton()!.hasAttribute('disabled')).toBe(true);
    rerender({ active: false, saving: true, save });
    expect(go).not.toHaveBeenCalled();
    rerender({ active: false, saving: false, save });
    expect(go).toHaveBeenCalledOnce();
    expect(dialog()).toBeNull();
    expect(save).not.toHaveBeenCalled();
  });

  it('она отказала — вопрос остаётся', () => {
    const { go, leave, rerender } = mount({ active: true, saving: true });
    leave();
    rerender({ active: true, saving: false });
    expect(dialog()).toBeTruthy();
    expect(go).not.toHaveBeenCalled();
  });
});

/** Страница, что пишет сама (форма счёта, costs-invoice-screen-12-autosave, D-12.7): уход сначала дописывает. */
describe('уход дописывает (`flush`)', () => {
  const question = () => screen.queryByRole('alertdialog', { name: 'Запись не дошла — уйти без неё?' })
    ?? screen.queryByRole('dialog', { name: 'Запись не дошла — уйти без неё?' });

  it('запись дошла — уход без вопроса', async () => {
    const flush = pending();
    const { go, leave } = mount({ active: true, flush: flush.save });
    leave();
    expect(flush.save).toHaveBeenCalledOnce();
    expect(go).not.toHaveBeenCalled();
    await flush.finish(true);
    expect(go).toHaveBeenCalledOnce();
    expect(question()).toBeNull();
    expect(dialog()).toBeNull();
  });

  it('запись не дошла — вопрос; «Остаться» оставляет', async () => {
    const flush = pending();
    const { go, leave } = mount({ active: true, flush: flush.save, failedMessage: 'Набранное не записано.' });
    leave();
    await flush.finish(false);
    expect(question()).toBeTruthy();
    expect(screen.getByText('Набранное не записано.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Остаться' }));
    expect(go).not.toHaveBeenCalled();
    expect(question()).toBeNull();
  });

  it('запись не дошла — «Уйти без записи» уходит', async () => {
    const flush = pending();
    const { go, leave } = mount({ active: true, flush: flush.save });
    leave();
    await flush.finish(false);
    fireEvent.click(screen.getByRole('button', { name: 'Уйти без записи' }));
    expect(go).toHaveBeenCalledOnce();
  });

  it('уход во время записи не теряется: после неё идёт последний (ревью PR #1837)', async () => {
    const flush = pending();
    const other = vi.fn();
    function Other() {
      const guard = useNavigationGuard();
      return <button type="button" onClick={() => { if (!guard?.attempt(other)) other(); }}>Отчёты</button>;
    }
    const go = vi.fn();
    render(
      <NavigationGuardProvider>
        <LeaveGuard active flush={flush.save} />
        <NavItem onGo={go} />
        <Other />
      </NavigationGuardProvider>,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Счета' }));
    fireEvent.click(screen.getByRole('button', { name: 'Отчёты' }));
    expect(flush.save).toHaveBeenCalledOnce();
    await flush.finish(true);
    expect(other).toHaveBeenCalledOnce();
    expect(go).not.toHaveBeenCalled();
  });

  it('запись дошла, пока вопрос открыт («Повторить» в сводке), — уход идёт сам', async () => {
    const flush = pending();
    const { go, leave, rerender } = mount({ active: true, flush: flush.save });
    leave();
    await flush.finish(false);
    expect(question()).toBeTruthy();
    rerender({ active: false, flush: flush.save });
    expect(go).toHaveBeenCalledOnce();
  });
});
