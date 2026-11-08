// @vitest-environment jsdom
import { useEffect } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { InvoiceSameFile } from '@/shared/api/invoices';
import { sameFileByFingerprints } from '@/shared/api/invoiceSameFile';
import { SnackbarProvider } from '@/shared/ui/SnackbarProvider';
import { useDropMissGuard } from '@/shared/ui/fileDrop';
import { currentBatch, releaseHeld, resetBatchForTests, useScanBatch } from './scanBatch';
import { useInvoiceFileIntake } from './useInvoiceFileIntake';

vi.mock('@/shared/api/invoiceSameFile', () => ({ sameFileByFingerprints: vi.fn() }));
vi.mock('@/shared/api/invoiceRecognition', () => ({ sendScan: vi.fn(), refreshInvoiceLists: vi.fn() }));
vi.mock('@/shared/api/fileKinds', async real => ({ ...(await real<object>()), useFileKinds: () => ({ data: undefined }) }));
// Отпечаток — по имени: «тот же файл» задаётся ответом вопроса, а не содержимым.
vi.mock('@/shared/utils/fileFingerprint', () => ({ fingerprintOf: async (f: File) => f.name.padEnd(64, '0') }));

/**
 * Один файл (costs-invoice-screen-14, D3, D4): бросок, кнопка и палитра — одним путём; двойники — окно
 * повтора, где Enter и Esc ничего не заводят; запоздавшее окно не забирает Enter у печатающего.
 */

const ask = vi.mocked(sameFileByFingerprints);
const pdf = (name = 'счёт.pdf') => new File(['x'], name, { type: 'application/pdf' });
const twin = (id: string, over: Partial<InvoiceSameFile> = {}): InvoiceSameFile => ({
  id, number: id, issuedOn: '2026-10-22', total: 54300, scanFileName: 'a.pdf', createdAt: '2026-10-22T06:14:00Z',
  supplierId: null, supplierName: 'ООО «ЭлектроКомплект»', supplierArchived: false, state: 'Черновик', payment: 'Не оплачен',
  createdByName: 'Потоцкий А.', mine: false, ...over,
});
const answer = (twins: InvoiceSameFile[]) => ask.mockImplementation(async prints => prints.map(fingerprint => ({ fingerprint, twins })));

let onSingle: ReturnType<typeof vi.fn<(file: File, acceptRepeat: boolean, unchecked: boolean) => void>>;
let onOpen: ReturnType<typeof vi.fn<(id: string) => void>>;
let add: (files: File[]) => void = () => {};

function Harness() {
  useDropMissGuard();
  const batch = useScanBatch('я');
  const intake = useInvoiceFileIntake({
    enabled: true, owner: 'я', batch, opened: { number: '104' }, listShown: true, sending: false,
    onSingle, onOpen, onBatchStart: () => {},
  });
  useEffect(() => { add = intake.addScans; });
  return <>{intake.layer}<input aria-label="Номер счёта" /><div data-testid="invoice-header" tabIndex={-1} /></>;
}

function mount() {
  const qc = new QueryClient();
  render(<QueryClientProvider client={qc}><SnackbarProvider><Harness /></SnackbarProvider></QueryClientProvider>);
}

beforeEach(() => {
  resetBatchForTests();
  onSingle = vi.fn();
  onOpen = vi.fn();
  ask.mockReset();
});
afterEach(() => { cleanup(); vi.useRealTimers(); });

describe('один файл', () => {
  it('двойников нет — заводится без окна, о новизне ничего не сказано', async () => {
    answer([]);
    mount();
    act(() => add([pdf()]));
    await waitFor(() => expect(onSingle).toHaveBeenCalledWith(expect.any(File), false, false));
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(document.body.textContent).not.toMatch(/файл новый/i);
  });

  it('двойник есть — окно до заведения; Enter на «Не заводить» ничего не заводит', async () => {
    answer([twin('2611')]);
    mount();
    act(() => add([pdf()]));
    const dialog = await screen.findByRole('dialog', { name: 'Этот файл уже приложен к счёту' });
    // Basic dialog, не alertdialog (Э9); фокус — на «Не заводить».
    expect(dialog.getAttribute('role')).toBe('dialog');
    const no = screen.getByRole('button', { name: 'Не заводить' });
    expect(document.activeElement).toBe(no);
    expect(screen.getByRole('button', { name: 'Открыть счёт 2611 от 22.10.2026' })).toBeTruthy();
    expect(screen.getByText(/^заведён .+ · Потоцкий А.$/)).toBeTruthy();
    fireEvent.click(no); // Enter на сфокусированной кнопке — её нажатие
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(onSingle).not.toHaveBeenCalled();
  });

  it('Esc закрывает окно и ничего не заводит', async () => {
    answer([twin('2611')]);
    mount();
    act(() => add([pdf()]));
    const dialog = await screen.findByRole('dialog');
    fireEvent.keyDown(dialog, { key: 'Escape' });
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(onSingle).not.toHaveBeenCalled();
  });

  it('«Завести ещё один черновик» — ровно один, как сознательный повтор', async () => {
    answer([twin('2611')]);
    mount();
    act(() => add([pdf()]));
    fireEvent.click(await screen.findByRole('button', { name: 'Завести ещё один черновик' }));
    await waitFor(() => expect(onSingle).toHaveBeenCalledTimes(1));
    expect(onSingle).toHaveBeenCalledWith(expect.any(File), true, false);
  });

  it('разные номера у двойников — окно говорит об этом, а «Завести ещё один» заметнее', async () => {
    answer([twin('2611'), twin('2612', { state: 'Разобран', createdByName: 'Иванова Е.' })]);
    mount();
    act(() => add([pdf()]));
    await screen.findByRole('dialog', { name: 'Этот файл уже приложен к 2 счетам' });
    expect(screen.getByText(/уже разобраны счета 2611 и 2612/)).toBeTruthy();
    expect(screen.getAllByTestId('repeat-twin')).toHaveLength(2);
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Не заводить' }));
  });

  it('«Открыть» уводит на двойника путём смены счёта, черновика нет', async () => {
    answer([twin('2611')]);
    mount();
    act(() => add([pdf()]));
    fireEvent.click(await screen.findByRole('button', { name: 'Открыть счёт 2611 от 22.10.2026' }));
    expect(onOpen).toHaveBeenCalledWith('2611');
    expect(onSingle).not.toHaveBeenCalled();
  });

  it('печатает в поле, ответ позже секунды — окна нет, снэкбар «Решить» открывает то же окно', async () => {
    let reply = () => {};
    ask.mockImplementation(prints => new Promise(resolve => {
      reply = () => resolve(prints.map(fingerprint => ({ fingerprint, twins: [twin('2611')] })));
    }));
    const now = vi.spyOn(performance, 'now').mockReturnValue(0);
    mount();
    act(() => add([pdf()]));
    screen.getByLabelText('Номер счёта').focus();
    await waitFor(() => expect(ask).toHaveBeenCalled());
    now.mockReturnValue(1500);
    await act(async () => { reply(); });
    await screen.findByText('Файл уже у счёта 2611');
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(document.activeElement).toBe(screen.getByLabelText('Номер счёта'));
    // Запасное место решения — придержанная строка пакета (Э16).
    expect(currentBatch()?.held.map(h => h.name)).toEqual(['счёт.pdf']);

    fireEvent.click(screen.getByRole('button', { name: 'Решить' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Не заводить' }));
    await waitFor(() => expect(currentBatch()).toBeNull());
    expect(onSingle).not.toHaveBeenCalled();
    now.mockRestore();
  });

  /** Файл придержан: печатали в поле, ответ пришёл позже секунды — снэкбар «Решить». */
  async function holdWhileTyping() {
    let reply = () => {};
    ask.mockImplementation(prints => new Promise(resolve => {
      reply = () => resolve(prints.map(fingerprint => ({ fingerprint, twins: [twin('2611')] })));
    }));
    const now = vi.spyOn(performance, 'now').mockReturnValue(0);
    mount();
    act(() => add([pdf()]));
    screen.getByLabelText('Номер счёта').focus();
    await waitFor(() => expect(ask).toHaveBeenCalled());
    now.mockReturnValue(1500);
    await act(async () => { reply(); });
    await screen.findByRole('button', { name: 'Решить' });
    now.mockRestore();
  }

  it('придержанный: «Открыть» из окна снэкбара — тоже решение, строка уходит', async () => {
    await holdWhileTyping();
    fireEvent.click(screen.getByRole('button', { name: 'Решить' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Открыть счёт 2611 от 22.10.2026' }));
    expect(onOpen).toHaveBeenCalledWith('2611');
    expect(currentBatch()).toBeNull();
  });

  it('придержанный, решённый строкой полосы, — «Решить» окна не открывает: второго черновика нет', async () => {
    await holdWhileTyping();
    act(() => releaseHeld(currentBatch()!.held[0].print));
    fireEvent.click(screen.getByRole('button', { name: 'Решить' }));
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(onSingle).not.toHaveBeenCalled();
  });

  it('вопрос не ответил — черновик заводится с «повтор не проверен»: сказать это — заводящему', async () => {
    ask.mockRejectedValue({ response: { status: 503 } });
    mount();
    act(() => add([pdf()]));
    await waitFor(() => expect(onSingle).toHaveBeenCalledWith(expect.any(File), false, true));
    // До заведения слов нет: охрана ухода могла его отменить (ревью PR #1823).
    expect(screen.queryByText('Повтор не проверен — если файл уже был, счёт скажет об этом.')).toBeNull();
  });

  it('предпроверка та же, что у пакета: пустой файл не уходит и назван', async () => {
    mount();
    act(() => add([new File([], 'пустой.pdf', { type: 'application/pdf' })]));
    await screen.findByText('Счёт из файла не заведён: «пустой.pdf» — файл пуст');
    expect(onSingle).not.toHaveBeenCalled();
    expect(ask).not.toHaveBeenCalled();
  });

  it('бросок на окно идёт тем же путём, что кнопка', async () => {
    answer([]);
    mount();
    const e = new Event('drop', { bubbles: true, cancelable: true });
    const file = pdf();
    Object.defineProperty(e, 'dataTransfer', {
      value: { types: ['Files'], dropEffect: 'move', files: [file], items: [{ kind: 'file', getAsFile: () => file, webkitGetAsEntry: () => null }] },
    });
    act(() => { window.dispatchEvent(e); });
    await waitFor(() => expect(onSingle).toHaveBeenCalledWith(file, false, false));
  });
});
