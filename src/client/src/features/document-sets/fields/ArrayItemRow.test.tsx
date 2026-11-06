// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { FieldRef } from '@/shared/api/types';
import { ArrayItemRow, type ArrayItemView } from './ArrayItemRow';

/** Строка поля-массива на `ListItem` ([UI-84], sharedui-md3-21 Р3, Р9). */

afterEach(() => { cleanup(); vi.clearAllMocks(); });

const ref: FieldRef = { $ref: 'catalog', entryId: 'e1', displayName: 'ООО «Монтаж»' };
const drop = { onDragOver: vi.fn(), onDrop: vi.fn() };

function row(view: ArrayItemView, { checked = false, onEdit = vi.fn(), onRemove = vi.fn() } = {}) {
  render(<ArrayItemRow view={view} index={1} checked={checked} drop={drop}
    chrome={<input type="checkbox" aria-label="Выбрать строку 2" />} onEdit={onEdit} onRemove={onRemove} />);
  return { onEdit, onRemove };
}

/** Контейнер `ListItem` — тот, что несёт заливку. */
const fillOf = (text: string) => screen.getByText(text).closest('[class*="items-stretch"]')!;

describe('ArrayItemRow', () => {
  it('сводка — цель строки открывает её, ✎ и 🗑 — соседи; отмеченная — заливка без aria-current', () => {
    const { onEdit, onRemove } = row({ kind: 'summary', summary: 'Кабель ВВГ 3×2,5', extraVariants: [] }, { checked: true });
    const target = screen.getByRole('button', { name: 'Кабель ВВГ 3×2,5' });
    fireEvent.click(target);
    expect(onEdit).toHaveBeenCalledTimes(1);
    expect(target.hasAttribute('aria-current')).toBe(false);
    expect(fillOf('Кабель ВВГ 3×2,5').className).toContain('bg-secondary-container');
    fireEvent.click(screen.getByRole('button', { name: 'Удалить строку 2' }));
    expect(onRemove).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('button', { name: 'Редактировать' })).toBeTruthy();
    expect(screen.getByRole('checkbox', { name: 'Выбрать строку 2' })).toBeTruthy();
  });

  it('ссылка — без своей цели и без text-warning', () => {
    row({ kind: 'ref', ref, broken: false });
    const label = screen.getByText('ООО «Монтаж»');
    expect(label.closest('button')).toBeNull();
    expect(fillOf('ООО «Монтаж»').className).not.toContain('state-layer');
    expect(document.querySelector('.text-warning')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Редактировать' })).toBeNull();
  });

  it('битая отмеченная — заливкой, подпись и знак error, без кольца; под строкой — пояснение', () => {
    row({ kind: 'ref', ref, broken: true }, { checked: true });
    const label = screen.getByText('ООО «Монтаж»');
    expect(label.className).toContain('text-error');
    const fill = fillOf('ООО «Монтаж»');
    expect(fill.className).toContain('bg-secondary-container');
    expect(document.querySelector('.ring-primary')).toBeNull();
    // Пояснение — под строкой, но вне цели перетаскивания: рамка цели обводит только строку.
    const dropZone = fill.parentElement!;
    expect(dropZone.children).toHaveLength(1);
    expect(dropZone.nextElementSibling).not.toBeNull();
    fireEvent.dragOver(dropZone.nextElementSibling!);
    expect(drop.onDragOver).not.toHaveBeenCalled();
  });

  it('ссылка-вариант union — вариант справа и ✎ для смены варианта', () => {
    const { onEdit } = row({ kind: 'union-ref', ref, label: 'Подрядчик', broken: false });
    expect(screen.getByText('Подрядчик')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Редактировать' }));
    expect(onEdit).toHaveBeenCalledTimes(1);
  });

  it('перетаскивание — на обёртке строки', () => {
    row({ kind: 'summary', summary: 'Строка', extraVariants: [] });
    fireEvent.dragOver(fillOf('Строка').parentElement!);
    expect(drop.onDragOver).toHaveBeenCalled();
  });
});
