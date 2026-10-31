// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { Modal } from './Modal';
import { RichTooltip } from './RichTooltip';
import { Tooltip } from './Tooltip';

/**
 * Богатая подсказка ([UI-72]): с действием — немодальное окно, которое не закрывается, пока указатель или
 * фокус в нём, и доступно с клавиатуры; без действия — как простая.
 */

afterEach(() => { cleanup(); vi.useRealTimers(); });

/** Фокус с клавиатуры: тестовый DOM, как браузер, после щелчка считает фокус невидимым. */
async function focus(el: HTMLElement) {
  fireEvent.keyDown(document.body, { key: 'Tab' });
  await act(async () => { el.focus(); });
}

const save = vi.fn();
function Post() {
  return (
    <RichTooltip title="Сначала сохраните разбиение" text="Вырезать можно только сохранённое."
      actions={[{ label: 'Сохранить', onClick: save }]}>
      <button type="button" disabled>Вырезать</button>
    </RichTooltip>
  );
}

describe('RichTooltip с действием', () => {
  it('немодальное окно с именем по заголовку; триггер описан и доступен', async () => {
    render(<Post />);
    const trigger = screen.getByRole('button', { name: 'Вырезать' });
    expect(trigger.getAttribute('aria-disabled')).toBe('true');
    expect(trigger.getAttribute('aria-description')).toBe('Сначала сохраните разбиение. Вырезать можно только сохранённое.');
    await focus(trigger);
    const dialog = screen.getByRole('dialog', { name: 'Сначала сохраните разбиение' });
    expect(dialog.getAttribute('aria-modal')).toBeNull();
    expect(dialog.className).toContain('bg-surface-container');
    expect(dialog.className).not.toContain('pointer-events-none');
  });

  it('указатель, перешедший на подсказку, её не закрывает; ушедший совсем — закрывает через 300 мс', () => {
    vi.useFakeTimers();
    render(<Post />);
    const trigger = screen.getByRole('button', { name: 'Вырезать' });
    fireEvent.pointerEnter(trigger);
    act(() => { vi.advanceTimersByTime(400); });
    const dialog = screen.getByRole('dialog');
    fireEvent.pointerLeave(trigger);
    fireEvent.pointerEnter(dialog);
    act(() => { vi.advanceTimersByTime(1000); });
    expect(screen.queryByRole('dialog')).not.toBeNull();
    fireEvent.pointerLeave(dialog);
    act(() => { vi.advanceTimersByTime(299); });
    expect(screen.queryByRole('dialog')).not.toBeNull();
    act(() => { vi.advanceTimersByTime(1); });
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('Tab с триггера — на действие, уход фокуса в подсказку её не закрывает; Esc закрывает и возвращает фокус', async () => {
    render(<Post />);
    const trigger = screen.getByRole('button', { name: 'Вырезать' });
    await focus(trigger);
    fireEvent.keyDown(trigger, { key: 'Tab' });
    const action = screen.getByRole('button', { name: 'Сохранить' });
    expect(document.activeElement).toBe(action);
    expect(screen.queryByRole('dialog')).not.toBeNull();
    fireEvent.keyDown(action, { key: 'Escape' });
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(document.activeElement).toBe(trigger);
  });

  it('Shift+Tab с действия — обратно на триггер, подсказка открыта', async () => {
    render(<Post />);
    const trigger = screen.getByRole('button', { name: 'Вырезать' });
    await focus(trigger);
    fireEvent.keyDown(trigger, { key: 'Tab' });
    fireEvent.keyDown(screen.getByRole('button', { name: 'Сохранить' }), { key: 'Tab', shiftKey: true });
    expect(document.activeElement).toBe(trigger);
    expect(screen.queryByRole('dialog')).not.toBeNull();
  });

  it('действие выполняется, подсказка закрывается, фокус — на триггере', async () => {
    save.mockClear();
    render(<Post />);
    const trigger = screen.getByRole('button', { name: 'Вырезать' });
    await focus(trigger);
    fireEvent.keyDown(trigger, { key: 'Tab' });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
    expect(save).toHaveBeenCalledOnce();
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(document.activeElement).toBe(trigger);
  });

  it('в окне: Tab доходит до действия, Esc закрывает подсказку, а не окно', async () => {
    const onOpenChange = vi.fn();
    render(<Modal open onOpenChange={onOpenChange} title="Разбиение" nothingToLose><Post /></Modal>);
    await act(async () => {});
    const trigger = screen.getByRole('button', { name: 'Вырезать' });
    await focus(trigger);
    fireEvent.keyDown(trigger, { key: 'Tab' });
    const action = screen.getByRole('button', { name: 'Сохранить' });
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    expect(document.activeElement).toBe(action);
    fireEvent.keyDown(action, { key: 'Escape' });
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    expect(screen.queryByRole('dialog', { name: 'Сначала сохраните разбиение' })).toBeNull();
    expect(onOpenChange).not.toHaveBeenCalled();
    expect(document.activeElement).toBe(trigger);
  });
});

describe('RichTooltip без действия и Tooltip', () => {
  it('без действия — как простая: role tooltip, сквозь неё щёлкают, уход указателя закрывает сразу', () => {
    vi.useFakeTimers();
    render(
      <RichTooltip title="Проведение" text="Проведённую накладную меняет исправление.">
        <button type="button">Провести</button>
      </RichTooltip>,
    );
    const trigger = screen.getByRole('button', { name: 'Провести' });
    fireEvent.pointerEnter(trigger);
    act(() => { vi.advanceTimersByTime(400); });
    const tip = screen.getByRole('tooltip');
    expect(tip.className).toContain('pointer-events-none');
    expect(screen.queryByRole('dialog')).toBeNull();
    fireEvent.pointerLeave(trigger);
    expect(screen.queryByRole('tooltip')).toBeNull();
  });

  it('простая подсказка — plain tooltip MD3: inverse-surface, body-small, форма 4, без рамки и тени', async () => {
    render(<Tooltip content="Удалить"><button type="button">x</button></Tooltip>);
    await focus(screen.getByRole('button'));
    const cls = screen.getByRole('tooltip').className;
    expect(cls).toContain('bg-inverse-surface');
    expect(cls).toContain('text-inverse-on-surface');
    expect(cls).toContain('text-body-small');
    expect(cls).toContain('rounded-xs');
    expect(cls).not.toMatch(/\bborder\b|shadow-|label-small/);
  });
});
