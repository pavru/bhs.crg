// @vitest-environment jsdom
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { useState } from 'react';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { Select, SelectItem } from './Select';
import { Modal } from './Modal';
import { resetHistoryLayers } from './historyLayers';
// Слушатель `popstate` — общий с охраной шага: без него слои не слышат «Назад».
import './NavigationGuard';

/**
 * Список выбора — слой истории ([UI-96]): его открывают касанием, клавиатуры при нём нет, и на телефоне
 * первая «Назад» без слоя закрыла бы окно под ним вместе с набранным (дефект #1522).
 */

const settle = () => act(() => new Promise(r => setTimeout(r, 30)));
const state = () => window.history.state as { idx?: number; layer?: string; depth?: number } | null;

function mockWidth(wide: boolean) {
  // Ширину спрашивают двумя вопросами — «шире 768» (WIDE) и «уже 768» (NARROW), — и ответ у них обратный.
  window.matchMedia = vi.fn().mockImplementation((q: string) => ({
    matches: q.includes('min-width') ? wide : !wide, addEventListener() {}, removeEventListener() {},
  })) as unknown as typeof window.matchMedia;
}

const ownMatchMedia = window.matchMedia;

beforeAll(() => {
  // Radix Select зовёт API указателя и прокрутки, которых в jsdom нет.
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.releasePointerCapture ??= () => {};
  Element.prototype.scrollIntoView ??= () => {};
});
afterAll(() => { window.matchMedia = ownMatchMedia; });

beforeEach(() => {
  resetHistoryLayers();
  mockWidth(false);
  window.history.replaceState({ idx: 0, key: 'base' }, '');
});
afterEach(cleanup);

function Form({ onClose }: { onClose: () => void }) {
  const [value, setValue] = useState('a');
  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Строка">
      <Select label="Раздел" value={value} onValueChange={setValue}>
        <SelectItem value="a">Первый</SelectItem>
        <SelectItem value="b">Второй</SelectItem>
      </Select>
      <output>{value}</output>
    </Modal>
  );
}

const field = () => screen.getByRole('combobox', { name: 'Раздел' });
const list = () => screen.queryByRole('listbox');
async function openList() {
  fireEvent.keyDown(field(), { key: 'Enter' });
  await settle();
  expect(list()).not.toBeNull();
}

describe('Select: «Назад» на телефоне', () => {
  it('«Назад» закрывает список, значение прежнее, окно остаётся', async () => {
    const onClose = vi.fn();
    render(<Form onClose={onClose} />);
    await settle();
    expect(state()?.depth).toBe(1);
    await openList();
    expect(state()?.depth).toBe(2);

    window.history.back();
    await settle();
    expect(list()).toBeNull();
    expect(onClose).not.toHaveBeenCalled();
    expect(document.querySelector('output')!.textContent).toBe('a');
    expect(state()?.depth).toBe(1);
  });

  it('выбор пункта снимает запись — следующая «Назад» закрывает окно', async () => {
    const onClose = vi.fn();
    render(<Form onClose={onClose} />);
    await settle();
    await openList();
    fireEvent.click(screen.getByRole('option', { name: 'Второй' }));
    await settle();
    expect(list()).toBeNull();
    expect(document.querySelector('output')!.textContent).toBe('b');
    expect(state()?.depth).toBe(1);

    window.history.back();
    await settle();
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});

describe('Select: на рабочем столе', () => {
  it('записи истории не кладёт', async () => {
    mockWidth(true);
    render(<Form onClose={() => {}} />);
    await settle();
    await openList();
    expect(state()).toEqual({ idx: 0, key: 'base' });
  });
});
