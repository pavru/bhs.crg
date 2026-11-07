import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from './client';
import { MAX_FINGERPRINTS, sameFileByFingerprints } from './invoiceSameFile';

vi.mock('./client', () => ({ apiClient: { get: vi.fn() } }));

const a = 'a'.repeat(64);
const b = 'b'.repeat(64);

// Телом, а не выражением: функцию, которую вернул хук, vitest зовёт уборкой — а mockReset возвращает сам мок.
beforeEach(() => { vi.mocked(apiClient.get).mockReset(); });

describe('вопрос «тот же файл» по отпечаткам', () => {
  it('повторяет параметр fingerprint, а не шлёт fingerprint[]', async () => {
    vi.mocked(apiClient.get).mockResolvedValueOnce({ data: [] });

    await sameFileByFingerprints([a, b]);

    const [url] = vi.mocked(apiClient.get).mock.calls[0];
    expect(url).toBe(`/costs/invoices/same-file?fingerprint=${a}&fingerprint=${b}`);
    expect(new URLSearchParams(url.split('?')[1]).getAll('fingerprint')).toEqual([a, b]);
  });

  it('отдаёт ответ сервера как есть — запись на отпечаток с двойниками', async () => {
    const twin = {
      id: 'i1', number: 'СЧ-7', issuedOn: '2026-10-01', total: 1000, scanFileName: 'Счёт 7.pdf',
      createdAt: '2026-10-01T10:00:00Z', supplierId: 's1', supplierName: null, supplierArchived: false,
      state: 'Разобран', payment: 'Оплачен', createdByName: 'Петров', mine: false,
    };
    vi.mocked(apiClient.get).mockResolvedValueOnce({
      data: [{ fingerprint: a, twins: [twin] }, { fingerprint: b, twins: [] }],
    });

    const answers = await sameFileByFingerprints([a, b]);

    expect(answers.map(x => x.fingerprint)).toEqual([a, b]);
    expect(answers[0].twins[0]).toEqual(twin);
    // Справочник не ответил — название пусто при идентификаторе: «не знаем», а не «без поставщика».
    expect(answers[0].twins[0].supplierName).toBeNull();
    expect(answers[0].twins[0].supplierId).toBe('s1');
    expect(answers[1].twins).toEqual([]);
  });

  it('схлопывает повторы и регистр, как сервер', async () => {
    vi.mocked(apiClient.get).mockResolvedValueOnce({ data: [] });

    await sameFileByFingerprints([a, a.toUpperCase(), b, a]);

    const [url] = vi.mocked(apiClient.get).mock.calls[0];
    expect(new URLSearchParams(url.split('?')[1]).getAll('fingerprint')).toEqual([a, b]);
  });

  it('длинный перечень делит на части по пределу сервера и отвечает в порядке вопроса', async () => {
    const prints = Array.from({ length: MAX_FINGERPRINTS + 3 }, (_, i) => i.toString(16).padStart(64, '0'));
    vi.mocked(apiClient.get).mockImplementation(async (url: string) => ({
      data: new URLSearchParams(url.split('?')[1]).getAll('fingerprint').map(fingerprint => ({ fingerprint, twins: [] })),
    }));

    const answers = await sameFileByFingerprints(prints);

    const sizes = vi.mocked(apiClient.get).mock.calls
      .map(([url]) => new URLSearchParams(url.split('?')[1]).getAll('fingerprint').length);
    expect(sizes).toEqual([MAX_FINGERPRINTS, 3]);
    expect(answers.map(x => x.fingerprint)).toEqual(prints);
  });

  it('пустой перечень — без запроса: сервер отказал бы', async () => {
    expect(await sameFileByFingerprints([])).toEqual([]);
    expect(apiClient.get).not.toHaveBeenCalled();
  });
});
