// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { SelectionActionBar } from './PdfGroupingEditor';

/** «Перенести в группу» в полосе выделенных страниц редактора разбиения — меню Radix с порталом ([UI-51]). */

afterEach(cleanup);

// Закрытие меню и возврат фокуса Radix делает тиком.
const tick = () => new Promise(r => setTimeout(r, 0));

function renderBar() {
  const onMoveSelected = vi.fn();
  render(
    <div data-testid="lane" className="overflow-auto">
      <SelectionActionBar count={2} onMoveSelected={onMoveSelected} onSplitSelected={vi.fn()}
        candidateGroups={[{ id: 'g1', label: 'Обложка' }, { id: 'g2', label: 'Акт 1' }]} />
    </div>);
  return { onMoveSelected };
}

async function openMenu() {
  const trigger = screen.getByRole('button', { name: /Перенести в группу/ });
  await act(async () => { trigger.focus(); fireEvent.keyDown(trigger, { key: 'Enter' }); });
  return trigger;
}

describe('SelectionActionBar: «Перенести в группу»', () => {
  it('меню — в портале, вне прокручиваемой ленты', async () => {
    renderBar();
    await openMenu();
    expect(screen.getByTestId('lane').contains(screen.getByRole('menu'))).toBe(false);
  });

  it('выбор группы зовёт onMoveSelected с её id, «+ Новый документ» — с new', async () => {
    const { onMoveSelected } = renderBar();
    await openMenu();
    await act(async () => { fireEvent.keyDown(screen.getByRole('menuitem', { name: 'Акт 1' }), { key: 'Enter' }); });
    expect(onMoveSelected).toHaveBeenCalledWith('g2');
    await openMenu();
    await act(async () => { fireEvent.keyDown(screen.getByRole('menuitem', { name: '+ Новый документ' }), { key: 'Enter' }); });
    expect(onMoveSelected).toHaveBeenLastCalledWith('new');
  });

  it('щелчок мимо закрывает меню без переноса', async () => {
    const { onMoveSelected } = renderBar();
    await openMenu();
    await act(async () => { fireEvent.pointerDown(document.body); }); await act(tick);
    expect(screen.queryByRole('menu')).toBeNull();
    expect(onMoveSelected).not.toHaveBeenCalled();
  });

  it('Esc закрывает меню, фокус — на «Перенести в группу»', async () => {
    const { onMoveSelected } = renderBar();
    const trigger = await openMenu();
    await act(async () => { fireEvent.keyDown(screen.getAllByRole('menuitem')[0], { key: 'Escape' }); }); await act(tick);
    expect(screen.queryByRole('menu')).toBeNull();
    expect(onMoveSelected).not.toHaveBeenCalled();
    expect(document.activeElement).toBe(trigger);
  });
});
