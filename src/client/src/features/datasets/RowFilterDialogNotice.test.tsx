// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import type { FilterGroup, RowFilterDef } from '@/shared/api/types';
import type { FilterColumn } from '@/shared/filter/rowFilterModel';
import { RowFilterDialog } from './RowFilterDialog';

/**
 * Пометка «нет в справочнике» в строке расширенного диалога отбора (UI-56): под условием, с заменой на
 * похожее, «Сохранить» не заперт.
 */

beforeAll(() => {
  globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.scrollIntoView ??= () => {};
});
afterEach(cleanup);

const OBJECTS: FilterColumn = {
  name: 'ОбъектыРазноски', label: 'Объект', kind: 'list', operators: ['eq', 'neq', 'in', 'not_in', 'contains'],
  known: { complete: true, items: [{ value: 'Комарова 36' }, { value: 'Берёзовая 4' }] },
};
const filter = (value: string): FilterGroup => ({
  type: 'group', logic: 'and', children: [{ type: 'condition', column: 'ОбъектыРазноски', op: 'eq', value }],
});

describe('RowFilterDialog: пометка в строке условия', () => {
  it('под условием — пометка с заменой; замена возвращает фокус в поле', () => {
    render(<RowFilterDialog columns={[OBJECTS]} initial={filter('Березовая 4')} onSave={() => {}} onClose={() => {}} />);
    expect(screen.getByText(/«Березовая 4» — нет в справочнике/)).toBeTruthy();
    expect(screen.queryByRole('alert')).toBeNull();
    const box = screen.getByRole('combobox', { name: 'Значение' }) as HTMLInputElement;
    expect(document.getElementById(box.getAttribute('aria-describedby')!)!.textContent).toContain('нет в справочнике');
    fireEvent.click(screen.getByRole('button', { name: 'Заменить на «Берёзовая 4»' }));
    expect(box.value).toBe('Берёзовая 4');
    expect(document.activeElement).toBe(box);
    expect(screen.queryByText(/нет в справочнике/)).toBeNull();
  });

  it('помеченный отбор сохраняется как набран', async () => {
    const onSave = vi.fn<(f: RowFilterDef | null) => void>();
    render(<RowFilterDialog columns={[OBJECTS]} initial={filter('Ленина 5')} onSave={onSave} onClose={() => {}} />);
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(onSave).toHaveBeenCalledWith(filter('Ленина 5')));
  });
});
