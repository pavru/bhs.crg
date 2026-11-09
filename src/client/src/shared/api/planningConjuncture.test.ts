import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from './client';
import { ATTACHMENT_PAGE_LIMIT, readAllPages } from './planningConjuncture';

vi.mock('./client', () => ({ apiClient: { get: vi.fn() } }));

/**
 * Приложение больше одной страницы сервера читается целиком (ревью PR #1845): сетка КА и ВОР ищет по всему файлу,
 * а первая страница одна показала бы его обрезанным молча.
 */

interface Page { total: number; offset: number; rows: number[] }

const read = () => readAllPages<Page, number>('/plan/volume-statements/s1/lines', p => p.rows, (first, rows) => ({ ...first, rows }));

function serve(total: number, cut?: number) {
  vi.mocked(apiClient.get).mockImplementation(async (_url: string, config?: { params?: { offset?: number; limit?: number } }) => {
    const offset = config?.params?.offset ?? 0;
    const end = Math.min(total, cut ?? total, offset + (config?.params?.limit ?? 0));
    return { data: { total, offset, rows: Array.from({ length: Math.max(0, end - offset) }, (_, i) => offset + i) } } as never;
  });
}

describe('readAllPages', () => {
  beforeEach(() => vi.mocked(apiClient.get).mockReset());

  it('дочитывает страницы до total — смещением от прочитанного', async () => {
    serve(ATTACHMENT_PAGE_LIMIT * 2 + 5);
    const page = await read();
    expect(page.rows).toHaveLength(ATTACHMENT_PAGE_LIMIT * 2 + 5);
    expect(page.rows.at(-1)).toBe(ATTACHMENT_PAGE_LIMIT * 2 + 4);
    expect(vi.mocked(apiClient.get).mock.calls.map(c => (c[1] as { params: { offset: number } }).params.offset))
      .toEqual([0, ATTACHMENT_PAGE_LIMIT, ATTACHMENT_PAGE_LIMIT * 2]);
  });

  it('влезло в одну страницу — один запрос', async () => {
    serve(3);
    expect((await read()).rows).toEqual([0, 1, 2]);
    expect(apiClient.get).toHaveBeenCalledTimes(1);
  });

  it('пустая страница раньше total — конец, а не бесконечный цикл', async () => {
    serve(ATTACHMENT_PAGE_LIMIT + 10, ATTACHMENT_PAGE_LIMIT);
    expect((await read()).rows).toHaveLength(ATTACHMENT_PAGE_LIMIT);
    expect(apiClient.get).toHaveBeenCalledTimes(2);
  });
});
