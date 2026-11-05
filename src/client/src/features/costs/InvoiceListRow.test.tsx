// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import type { InvoiceListItem } from '@/shared/api/invoices';
import { InvoiceListRow } from './InvoiceListRow';

/**
 * Пометка «исправлен» в списке «Счета» (E2, issue #1100, PR 6c; разбор Дизайнера, п. 6). Чем ломается:
 * счёт, изменённый после закрытия периода, в списке выглядит таким, каким его закрыли.
 */

afterEach(cleanup);

const ITEM: InvoiceListItem = {
  id: 'i1', number: '17', issuedOn: '2026-09-10', supplierId: null, supplierName: null, supplierArchived: false,
  total: 120, state: 'Разобран', payment: 'Оплачен', dueDate: null, purpose: null, unconfirmedCount: 0,
  hasScan: false, linesCount: 1, linesWithoutNomenclature: 0,
};

const mount = (item: InvoiceListItem) =>
  render(<InvoiceListRow item={item} active={false} queue={null} onClick={() => {}} />);

describe('строка списка счетов', () => {
  it('исправленный после закрытия назван словом', () => {
    mount({ ...ITEM, corrected: true });
    expect(screen.getByText('исправлен').closest('[aria-description]')?.getAttribute('aria-description')).toContain('после закрытия периода');
  });

  it('строка MD3: invoice-row — цель ListItem, открытый — aria-current и secondary-container, без черты', () => {
    // Открытая строка прокручивается к себе — у jsdom прокрутки нет.
    const scroll = vi.fn();
    Element.prototype.scrollIntoView = scroll;
    render(<InvoiceListRow item={ITEM} active queue={null} onClick={() => {}} />);
    const row = screen.getByTestId('invoice-row');
    // ref — на цели строки: прокручивается именно она.
    expect(scroll.mock.contexts[0]).toBe(row);
    expect(row.getAttribute('aria-current')).toBe('true');
    expect(row.parentElement!.className).toContain('bg-secondary-container');
    expect(row.parentElement!.className).not.toContain('border-b');
    // Вторая часть многострочная — строка растёт минимумом, а не режется высотой.
    expect(row.className).toContain('min-h-16');
  });

  it('без книги исправлений — и без пометки, и когда сервер о ней молчит', () => {
    mount({ ...ITEM, corrected: false });
    expect(screen.queryByText('исправлен')).toBeNull();
    cleanup();
    mount(ITEM);
    expect(screen.queryByText('исправлен')).toBeNull();
  });
});
