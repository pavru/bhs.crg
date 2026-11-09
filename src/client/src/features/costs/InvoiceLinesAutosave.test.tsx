// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from '@/shared/ui/SnackbarProvider';
import { apiClient } from '@/shared/api/client';
import type { InvoiceLineView, InvoiceView } from '@/shared/api/invoices';
import { InvoiceLinesTable } from './InvoiceLinesTable';
import { modePreview } from './invoiceVat';
import { toDrafts } from './invoiceLines';

vi.mock('@/shared/api/client', () => ({
  apiClient: { post: vi.fn(), get: vi.fn(async () => ({ data: null })), put: vi.fn(), delete: vi.fn() }, traceIdOf: () => undefined,
}));
vi.mock('@/shared/api/access', () => ({ NO_ACCESS: {}, useAccess: () => ({ data: {} }), hasPermission: () => true }));

/**
 * Поводы записи строк черновика (costs-invoice-screen-12-autosave, D-12.2, ТЗ COST-6.5): уход из строки, Enter в
 * последнем поле, удаление — по закрытию снэкбара «Вернуть»; переход между клетками одной строки записи не даёт.
 * Режим НДС — окном с предпросмотром (D-12.6).
 */

afterEach(() => { cleanup(); vi.useRealTimers(); });
beforeEach(() => vi.mocked(apiClient.put).mockReset());

function line(id: string, ordinal: number, quantity: number, vatRate: number | null = null): InvoiceLineView {
  return {
    id, ordinal, nomenclatureId: null, nomenclatureName: null, nomenclatureLost: false,
    supplierText: `Кабель ${ordinal}`, supplierCode: null, unit: 'м', quantity, price: 10, vatRate, vatAmount: null,
    amount: quantity * 10, note: null,
    allocation: { mode: 'none', parts: [], unallocatedQuantity: null, unallocatedAmount: null, balanced: true },
  };
}

function view(lines: InvoiceLineView[]): InvoiceView {
  return {
    id: 'i1', version: 'v1', documentTypeId: 't', vatMode: 'Included', unconfirmed: [],
    requisites: { Состояние: 'Черновик' }, duplicates: [], sameFile: [], lines,
    readiness: { required: [], gaps: [] }, totals: { count: lines.length },
    payment: { lockedBy: null }, allocation: { document: { parts: [] } },
  } as unknown as InvoiceView;
}

function mount(of: InvoiceView, autosave = true) {
  const commit = vi.fn();
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <SnackbarProvider>
        <InvoiceLinesTable view={of} locked={false} autosave={autosave} onCommit={commit} />
        <button type="button">вне таблицы</button>
      </SnackbarProvider>
    </QueryClientProvider>,
  );
  return commit;
}

const TAB = String.fromCharCode(9);
const NL = String.fromCharCode(10);
const field = (name: string) => screen.getByRole('textbox', { name }) as HTMLInputElement;

describe('уход из строки — повод записи', () => {
  it('переход между клетками одной строки записи не даёт; уход в другую строку — даёт', () => {
    const commit = mount(view([line('a', 1, 1), line('b', 2, 2)]));
    const quantity = field('Количество, строка 1');
    quantity.focus();
    fireEvent.change(quantity, { target: { value: '10' } });
    fireEvent.blur(quantity, { relatedTarget: field('Цена, строка 1') });
    expect(commit).not.toHaveBeenCalled();
    fireEvent.blur(field('Цена, строка 1'), { relatedTarget: field('Количество, строка 2') });
    expect(commit).toHaveBeenCalledOnce();
  });

  it('щелчок мимо полей — тоже уход из строки', () => {
    const commit = mount(view([line('a', 1, 1)]));
    fireEvent.blur(field('Количество, строка 1'), { relatedTarget: screen.getByRole('button', { name: 'вне таблицы' }) });
    expect(commit).toHaveBeenCalledOnce();
  });

  it('слой, открытый из клетки (список, окно), уходом из строки не считается', () => {
    const commit = mount(view([line('a', 1, 1)]));
    const layer = document.createElement('div');
    layer.setAttribute('role', 'listbox');
    const option = document.createElement('button');
    layer.append(option);
    document.body.append(layer);
    fireEvent.blur(field('Количество, строка 1'), { relatedTarget: option });
    expect(commit).not.toHaveBeenCalled();
    layer.remove();
  });

  it('Enter в последнем поле строки — как уход из строки; в среднем — нет', () => {
    const commit = mount(view([line('a', 1, 1)]));
    const fields = Array.from(document.querySelectorAll('tr[data-row="0"] [data-cell] input')) as HTMLInputElement[];
    fireEvent.keyDown(fields[0], { key: 'Enter' });
    expect(commit).not.toHaveBeenCalled();
    fireEvent.keyDown(fields[fields.length - 1], { key: 'Enter' });
    expect(commit).toHaveBeenCalledOnce();
  });

  it('добавление пустой строки записи не даёт', () => {
    const commit = mount(view([line('a', 1, 1)]));
    fireEvent.click(screen.getByRole('button', { name: 'Добавить строку' }));
    expect(commit).not.toHaveBeenCalled();
  });
});

describe('удаление строки без разноски', () => {
  it('пишется, когда снэкбар «Вернуть» закрылся, а не в миг удаления', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const commit = mount(view([line('a', 1, 1), line('b', 2, 2)]));
    fireEvent.click(screen.getAllByRole('button', { name: /Удалить строку/ })[0]);
    expect(screen.getByText(/Строка 1 удалена/)).toBeTruthy();
    expect(commit).not.toHaveBeenCalled();
    await act(async () => { await vi.advanceTimersByTimeAsync(9000); });
    expect(commit).toHaveBeenCalledOnce();
  });

  it('повод отдаётся после слова о грязи; «Вернуть» без других правок оставляет строки чистыми (Э5)', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const said: string[] = [];
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={qc}>
        <SnackbarProvider>
          <InvoiceLinesTable view={view([line('a', 1, 1), line('b', 2, 2)])} locked={false} autosave
            onDirty={dirty => { said.push(dirty ? 'грязно' : 'чисто'); }} onCommit={() => { said.push('повод'); }} />
        </SnackbarProvider>
      </QueryClientProvider>,
    );
    fireEvent.click(screen.getAllByRole('button', { name: /Удалить строку/ })[0]);
    fireEvent.click(screen.getByRole('button', { name: 'Вернуть' }));
    await act(async () => { await vi.advanceTimersByTimeAsync(9000); });
    // Очередь читает грязь в миг повода: поданный раньше слова, он сбросил бы пометку как «писать нечего».
    expect(said.at(-1)).toBe('повод');
    expect(said.at(-2)).toBe('чисто');
  });
});

describe('режим НДС — явный шаг', () => {
  it('флажок открывает предпросмотр; «Отмена» оставляет режим и не пишет', () => {
    const commit = mount(view([line('a', 1, 10, 20), line('b', 2, 5, 20)]));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Цены без НДС, НДС сверху' }));
    const preview = screen.getByTestId('vat-mode-preview');
    expect(preview.textContent).toContain('Пересчитать 2 строки: сумма строк станет 180,00');
    fireEvent.click(screen.getByRole('button', { name: 'Отмена' }));
    expect((screen.getByRole('checkbox', { name: 'Цены без НДС, НДС сверху' }) as HTMLInputElement).checked).toBe(false);
    expect(commit).not.toHaveBeenCalled();
  });

  it('«Пересчитать» меняет режим и пишет строки', () => {
    const commit = mount(view([line('a', 1, 10, 20)]));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Цены без НДС, НДС сверху' }));
    fireEvent.click(screen.getByRole('button', { name: 'Пересчитать 1 строку' }));
    expect((screen.getByRole('checkbox', { name: 'Цены без НДС, НДС сверху' }) as HTMLInputElement).checked).toBe(true);
    expect(commit).toHaveBeenCalledOnce();
  });

  it('без автосохранения (разобранный, исправление) флажок меняет режим сразу, без окна', () => {
    mount(view([line('a', 1, 10, 20)]), false);
    fireEvent.click(screen.getByRole('checkbox', { name: 'Цены без НДС, НДС сверху' }));
    expect(screen.queryByTestId('vat-mode-preview')).toBeNull();
  });

  it('вставка «цены без НДС» у счёта со строками — тот же предпросмотр; «Отмена» не кладёт ни строк, ни режима', () => {
    const commit = mount(view([line('a', 1, 10, 20)]));
    const paste = () => {
      fireEvent.click(screen.getByRole('button', { name: 'Вставить из буфера' }));
      fireEvent.change(screen.getByRole('textbox', { name: 'Таблица из буфера' }),
        { target: { value: 'Наименование' + TAB + 'Кол-во' + TAB + 'Цена без НДС' + NL + 'Провод' + TAB + '2' + TAB + '50' } });
      fireEvent.click(screen.getByRole('button', { name: /Добавить строк: 1/ }));
    };
    paste();
    expect(screen.getByTestId('vat-mode-preview').textContent).toContain('Пересчитать 1 строку');
    fireEvent.click(screen.getByRole('button', { name: 'Отмена' }));
    expect(commit).not.toHaveBeenCalled();
    expect(screen.queryByRole('textbox', { name: 'Наименование, строка 2' })).toBeNull();
    expect((screen.getByRole('checkbox', { name: 'Цены без НДС, НДС сверху' }) as HTMLInputElement).checked).toBe(false);

    paste();
    fireEvent.click(screen.getByRole('button', { name: 'Пересчитать 1 строку' }));
    expect(commit).toHaveBeenCalledOnce();
    expect((screen.getByRole('checkbox', { name: 'Цены без НДС, НДС сверху' }) as HTMLInputElement).checked).toBe(true);
  });

  it('предпросмотр считает строки со ставкой, строки без суммы и суммы до и после', () => {
    const drafts = toDrafts([line('a', 1, 10, 20), line('b', 2, 5, null)], 'Included');
    expect(modePreview(drafts, 'Included', 'OnTop')).toEqual({ changed: 1, unknown: 1, before: 150, after: 120 });
  });
});
