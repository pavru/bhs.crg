// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { SnackbarProvider } from '@/shared/ui/SnackbarProvider';
import { InvoiceFileDialog } from './InvoiceFileDialog';

/**
 * «Приложить / Заменить файл…» (costs-invoice-screen-14, D6): площадка и кнопка выбора; ничего не пишется до
 * «Приложить» / «Заменить», а замена спрашивает «Прежний файл будет удалён без возврата».
 */

const mutateAsync = vi.fn(async () => ({}));
vi.mock('@/shared/api/invoices', async real => ({
  ...(await real<object>()), useAttachInvoiceScan: () => ({ mutateAsync, isPending: false }),
}));
vi.mock('@/shared/api/fileKinds', async real => ({ ...(await real<object>()), useFileKinds: () => ({ data: undefined }) }));

const pdf = new File(['x'], 'Счёт исправленный.pdf', { type: 'application/pdf' });
let onClose: () => void;

const show = (current: string | null) => render(
  <SnackbarProvider>
    <InvoiceFileDialog invoiceId="i1" version="v7" number="СЧ-104" current={current} onClose={onClose} />
  </SnackbarProvider>);
const pick = () => fireEvent.change(screen.getByLabelText('Файл счёта'), { target: { files: [pdf] } });

beforeEach(() => { mutateAsync.mockClear(); onClose = vi.fn(); });
afterEach(cleanup);

describe('окно файла счёта', () => {
  it('замена: без согласия файл не уходит; после «Заменить файл» — один запрос с версией', async () => {
    show('Счёт СЧ-104 Кабель-Торг.pdf');
    expect(screen.getByRole('dialog', { name: 'Заменить файл счёта СЧ-104' })).toBeTruthy();
    const replace = screen.getByRole('button', { name: 'Заменить' }) as HTMLButtonElement;
    expect(replace.disabled).toBe(true);
    pick();
    fireEvent.click(replace);
    await screen.findByText('Прежний файл будет удалён без возврата');
    expect(mutateAsync).not.toHaveBeenCalled();
    // «Отмена» шага — назад к окну, файл не тронут.
    fireEvent.click(screen.getByRole('button', { name: 'Отмена' }));
    await waitFor(() => expect(screen.queryByText('Прежний файл будет удалён без возврата')).toBeNull());
    expect(mutateAsync).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Заменить' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Заменить файл' }));
    await waitFor(() => expect(mutateAsync).toHaveBeenCalledTimes(1));
    expect(mutateAsync).toHaveBeenCalledWith({ id: 'i1', seen: 'v7', file: pdf });
    await waitFor(() => expect(onClose).toHaveBeenCalled());
  });

  it('приложение: без вопроса, запрос только по «Приложить»', async () => {
    show(null);
    expect(screen.getByRole('dialog', { name: 'Приложить файл к счёту СЧ-104' })).toBeTruthy();
    pick();
    expect(mutateAsync).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Приложить' }));
    await waitFor(() => expect(mutateAsync).toHaveBeenCalledTimes(1));
    expect(screen.queryByText('Прежний файл будет удалён без возврата')).toBeNull();
  });

  it('пустой файл — отказ площадки словами предпроверки, файл не взят', () => {
    show(null);
    fireEvent.change(screen.getByLabelText('Файл счёта'), { target: { files: [new File([], 'пустой.pdf', { type: 'application/pdf' })] } });
    expect(screen.getByRole('alert').textContent).toBe('пустой.pdf — файл пуст');
    expect((screen.getByRole('button', { name: 'Приложить' }) as HTMLButtonElement).disabled).toBe(true);
  });
});
