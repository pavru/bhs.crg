// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { InvoiceSameFile } from '@/shared/api/invoices';
import { InvoiceScanBatchBar } from './InvoiceScanBatchBar';
import type { ScanBatch } from './scanBatch';

/**
 * Итог пакета с повторами (costs-invoice-screen-14, D5; лист «Ф», окно 7): «Заведено N · придержано M · K
 * выбран дважды», у придержанных — «Открыть» и «Завести всё равно», после перезагрузки — «выберите файл заново».
 */

const twin: InvoiceSameFile = {
  id: 't-2611', number: '2611', issuedOn: '2026-10-22', total: 1, scanFileName: null, createdAt: '2026-10-22T06:14:00Z',
  supplierId: null, supplierName: 'ООО «ЭлектроКомплект»', supplierArchived: false, state: 'Черновик', payment: 'Не оплачен',
  createdByName: null, mine: false,
};

const batch = (over: Partial<ScanBatch> = {}): ScanBatch => ({
  owner: 'я', phase: 'done', total: 8, settled: 8, created: ['a', 'b', 'c', 'd', 'e', 'f', 'g'], repeats: [], rejected: [],
  pending: [], halted: null, refused: null, doubled: ['Счёт_4417.pdf'], unchecked: false, recovered: [],
  held: [
    { name: 'Счёт 2611 ЭлектроКомплект.pdf', file: new File(['x'], 'Счёт 2611 ЭлектроКомплект.pdf'), print: 'p1', twins: [twin] },
    { name: 'скан-0412.jpg', file: null, print: 'p2', twins: [twin] },
  ],
  ...over,
});

const show = (b: ScanBatch, onAccept = vi.fn(), onOpen = vi.fn()) => {
  render(<InvoiceScanBatchBar batch={b} only={false} found={null} alive={null}
    onOnly={() => {}} onShowAll={() => {}} onRetry={() => {}} onOpen={onOpen} onAccept={onAccept} />);
  return { onAccept, onOpen };
};

afterEach(cleanup);

describe('полоса пакета: придержанные', () => {
  it('итог одной строкой и строка каждого придержанного', () => {
    show(batch());
    expect(screen.getByTestId('upload-tally').textContent).toBe('Заведено 7 · придержано 2 (тот же файл) · 1 выбран дважды');
    expect(screen.getByText('Счёт_4417.pdf выбран дважды — заведён один черновик')).toBeTruthy();
    expect(screen.getByTestId('upload-held').textContent).toContain('уже у счёта 2611 от 22.10.2026 · ООО «ЭлектроКомплект» · черновик');
  });

  it('«Завести всё равно» — по отпечатку; «Открыть» — прежний счёт', () => {
    const { onAccept, onOpen } = show(batch());
    fireEvent.click(screen.getByRole('button', { name: 'Завести черновик из Счёт 2611 ЭлектроКомплект.pdf всё равно' }));
    expect(onAccept).toHaveBeenCalledWith('p1');
    fireEvent.click(screen.getAllByRole('button', { name: 'Открыть счёт 2611 от 22.10.2026' })[0]);
    expect(onOpen).toHaveBeenCalledWith('t-2611');
  });

  it('после перезагрузки файла нет — «выберите файл заново» вместо «Завести всё равно»', () => {
    show(batch());
    expect(screen.queryByRole('button', { name: 'Завести черновик из скан-0412.jpg всё равно' })).toBeNull();
    expect(screen.getByTestId('upload-held').textContent).toContain('— выберите файл заново');
  });

  it('повтор не проверен — строкой итога', () => {
    show(batch({ held: [], doubled: [], unchecked: true }));
    expect(screen.getByTestId('upload-unchecked').textContent).toBe('Повтор не проверен — если файл уже был, счёт скажет об этом.');
    expect(screen.queryByTestId('upload-tally')).toBeNull();
  });

  it('пока пакет идёт, «Завести всё равно» неактивна, а итог говорит «Заводим N из M»', () => {
    show(batch({ phase: 'running', settled: 4, total: 7 }));
    expect(screen.getByTestId('upload-tally').textContent).toBe('Заводим 4 из 7 · придержано 2 (тот же файл) · 1 выбран дважды');
    expect((screen.getByRole('button', { name: 'Завести черновик из Счёт 2611 ЭлектроКомплект.pdf всё равно' }) as HTMLButtonElement).disabled).toBe(true);
  });
});
