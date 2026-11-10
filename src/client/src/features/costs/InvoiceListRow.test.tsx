// @vitest-environment jsdom
import { afterEach, describe, expect, it, onTestFinished, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { InvoiceListItem } from '@/shared/api/invoices';
import { InvoiceLeftRow, InvoiceListRow } from './InvoiceListRow';

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
    // Открытая строка прокручивается к себе — у jsdom прокрутки нет; заглушка снимается после теста.
    const scroll = vi.fn();
    const own = Element.prototype.scrollIntoView;
    Element.prototype.scrollIntoView = scroll;
    onTestFinished(() => { Element.prototype.scrollIntoView = own; });
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

  it('ушёл из отбора: почему строка осталась — описанием цели, а не подсказкой внутри неё', () => {
    render(<InvoiceLeftRow number="17" exit="parsed" onClick={() => {}} />);
    expect(screen.getByTestId('row-exit').getAttribute('aria-description')).toContain('из очереди ушёл');
  });

  it('черновик из скана: обрезанное имя файла — полностью в title подписи', () => {
    render(<InvoiceListRow item={{ ...ITEM, number: null, scanFileName: 'Счёт поставщика от 01.11.2026 номер 4011379.pdf' }}
      active={false} queue={null} onClick={() => {}} />);
    const label = screen.getByText(/Счёт поставщика от/);
    Object.defineProperty(label, 'scrollWidth', { configurable: true, value: 400 });
    Object.defineProperty(label, 'clientWidth', { configurable: true, value: 200 });
    fireEvent.pointerEnter(label);
    expect(label.getAttribute('title')).toBe('Счёт поставщика от 01.11.2026 номер 4011379.pdf');
  });

  // [COST-6.3], макет А: состояние — ведущим значком, слово — в имени строки и подсказкой значка.
  it.each([
    ['Черновик', 'Не оплачен', 'черновик', 'Черновик — ещё не разобран', 'text-on-surface-variant'],
    ['Разобран', 'Не оплачен', 'разобран', 'Разобран, не оплачен', 'text-primary'],
    ['Разобран', 'Оплачен', 'оплачен', 'Оплачен', 'text-success'],
    ['Черновик', 'Оплачен', 'оплачен, черновик', 'Оплачен, но не разобран — черновик', 'text-success'],
    ['Отклонён', 'Не оплачен', 'отклонён', 'Отклонён — по счёту не платим', 'text-on-surface-variant'],
  ])('%s, %s: значок и слово «%s»', (state, payment, word, hint, tone) => {
    mount({ ...ITEM, state, payment });
    const row = screen.getByTestId('invoice-row');
    expect(row.textContent).toContain(`, ${word}, `);
    const mark = screen.getByTestId('row-state');
    expect(mark.getAttribute('aria-description')).toBe(hint);
    expect(mark.querySelector('svg')!.getAttribute('class')).toContain(tone);
  });

  it('отклонённый назван один раз: значком, без пометки словом во второй строке', () => {
    mount({ ...ITEM, state: 'Отклонён', payment: 'Не оплачен' });
    expect(screen.getByTestId('invoice-row').textContent!.match(/отклонён/g)).toHaveLength(1);
  });

  it('выбранная строка: громкий значок держит цвет, тихий берёт тон заливки, слово на месте', () => {
    Element.prototype.scrollIntoView ??= () => {};
    render(<InvoiceListRow item={{ ...ITEM, payment: 'Оплачен' }} active queue={null} onClick={() => {}} />);
    expect(screen.getByTestId('row-state').querySelector('svg')!.getAttribute('class')).toContain('text-success');
    expect(screen.getByTestId('invoice-row').textContent).toContain(', оплачен, ');
    cleanup();
    render(<InvoiceListRow item={{ ...ITEM, state: 'Черновик', payment: 'Не оплачен' }} active queue={null} onClick={() => {}} />);
    expect(screen.getByTestId('row-state').querySelector('svg')!.getAttribute('class')).toContain('text-on-secondary-container');
  });

  it('фокус строки с клавиатуры открывает подсказку значка, уход фокуса — закрывает', () => {
    mount({ ...ITEM, payment: 'Оплачен' });
    act(() => screen.getByTestId('invoice-row').focus());
    expect(screen.getByRole('tooltip').textContent).toBe('Оплачен');
    act(() => screen.getByTestId('invoice-row').blur());
    expect(screen.queryByRole('tooltip')).toBeNull();
  });

  it('без книги исправлений — и без пометки, и когда сервер о ней молчит', () => {
    mount({ ...ITEM, corrected: false });
    expect(screen.queryByText('исправлен')).toBeNull();
    cleanup();
    mount(ITEM);
    expect(screen.queryByText('исправлен')).toBeNull();
  });
});
