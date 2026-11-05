// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import type { CommonDataEntry, DocumentType } from '@/shared/api/types';
import { RefPickerModal } from './RefPickerModal';

/** Окно выбора ссылки — группы вариантов по уровню ([UI-87], sharedui-md3-21, Р7 и Р8). */

afterEach(cleanup);

const entry = (id: string, scope: CommonDataEntry['scope'], displayName: string) =>
  ({ id, scope, displayName, compositeTypeId: 't1', archived: false }) as unknown as CommonDataEntry;
const ENTRIES = [entry('a', 'Set', 'ООО «Альфа»'), entry('b', 'Set', 'ООО «Бета»'), entry('c', 'Construction', 'ООО «Гамма»')];

vi.mock('@/shared/api/commonData', () => ({
  useCommonDataForScope: ({ purpose }: { purpose: string }) => ({ data: purpose === 'choice' ? ENTRIES : [] }),
}));
vi.mock('./archivedRefIds', () => ({ useIsArchivedRef: () => false }));
vi.mock('./RefPickerArchive', () => ({ RefPickerArchive: () => null, RefPickerCurrentArchived: () => null }));

const TYPE = { id: 't1', name: 'Организация', schema: '[]' } as unknown as DocumentType;

function show() {
  const onSelect = vi.fn();
  render(<RefPickerModal open onOpenChange={() => {}} compositeType={TYPE} allDocTypes={[TYPE]} setId="s1" onSelect={onSelect} />);
  return { onSelect, search: screen.getByRole('combobox') };
}

describe('RefPickerModal: группы вариантов', () => {
  it('у группы свой listbox, подписанный заголовком; заголовок — не вариант', () => {
    show();
    const header = screen.getByRole('button', { name: /Комплект/ });
    expect(header.getAttribute('aria-expanded')).toBe('true');
    const listbox = screen.getByRole('listbox', { name: /Комплект/ });
    expect(within(listbox).getAllByRole('option').map(o => o.textContent)).toEqual(['ООО «Альфа»', 'ООО «Бета»']);
    expect(listbox.contains(header)).toBe(false);
  });

  it('стрелки идут по вариантам раскрытых групп, поле показывает на listbox активного', () => {
    const { search } = show();
    const listbox = screen.getByRole('listbox', { name: /Комплект/ });
    expect(search.getAttribute('aria-controls')).toBe(listbox.id);
    fireEvent.keyDown(search, { key: 'ArrowDown' });
    fireEvent.keyDown(search, { key: 'ArrowDown' });
    // Свёрнутая «Стройка» стрелкам не отдаётся: активный остаётся последним вариантом раскрытой группы.
    const active = document.getElementById(search.getAttribute('aria-activedescendant')!)!;
    expect(active.textContent).toBe('ООО «Бета»');
    expect(document.activeElement).toBe(search);
  });

  it('варианты — не остановки Tab, заголовки групп — остановки; Enter выбирает активный', () => {
    const { search, onSelect } = show();
    for (const o of screen.getAllByRole('option')) expect(o.hasAttribute('tabindex')).toBe(false);
    expect(screen.getAllByRole('option').some(o => o.tagName === 'BUTTON')).toBe(false);
    expect(screen.getByRole('button', { name: /Стройка/ }).tabIndex).toBe(0);
    fireEvent.keyDown(search, { key: 'Enter' });
    expect(onSelect.mock.calls[0][0]).toMatchObject({ displayName: 'ООО «Альфа»' });
  });
});
