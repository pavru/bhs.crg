import { QueryClient } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from './client';
import { refreshInvoiceLists, sendScan } from './invoiceRecognition';
import { QK } from './invoices';

vi.mock('./client', () => ({ apiClient: { post: vi.fn() } }));

/**
 * Двойники пакета (issue #1344, ревью PR #1645): новый черновик делает устаревшим вид каждого своего
 * двойника, но перечитываются они шагом списков, а не на каждый файл — иначе пакет одинаковых файлов
 * перечитывал бы открытую форму двойника столько раз, сколько в нём файлов.
 */

const created = (id: string, twins: string[]) => ({
  data: { invoice: { id, sameFile: twins.map(t => ({ id: t })) }, recognition: { state: 'failed' } },
});

let qc: QueryClient;
beforeEach(() => {
  vi.mocked(apiClient.post).mockReset();
  qc = new QueryClient();
});

describe('двойники заведённых из файла черновиков', () => {
  it('отправка их не перечитывает, шаг списков — по разу на двойника', async () => {
    vi.mocked(apiClient.post)
      .mockResolvedValueOnce(created('b', ['a']))
      .mockResolvedValueOnce(created('c', ['a', 'b']));
    const invalidate = vi.spyOn(qc, 'invalidateQueries');

    await sendScan(qc, new File(['x'], '1.pdf'));
    await sendScan(qc, new File(['x'], '2.pdf'));
    expect(invalidate).not.toHaveBeenCalled();

    refreshInvoiceLists(qc);
    const keys = invalidate.mock.calls.map(([filters]) => filters?.queryKey);
    expect(keys).toEqual([[QK, 'list'], [QK, 'queues'], [QK, 'a'], [QK, 'b']]);

    // Перечитаны — и забыты: следующий шаг списков их не трогает.
    invalidate.mockClear();
    refreshInvoiceLists(qc);
    expect(invalidate).toHaveBeenCalledTimes(2);
  });
});
