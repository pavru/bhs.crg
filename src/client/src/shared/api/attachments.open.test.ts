// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '@/shared/api/client';
import { openAttachmentInNewTab } from './attachments';

/**
 * Вложение во вкладке — общей функцией просмотрщика ([UI-27], issue #1494): окно до загрузки, ссылка
 * отзывается с отсрочкой, отказ загрузки — словами во вкладке и исключением месту.
 */

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn() } }));

let written: string[];
let tab: {
  opener: unknown; closed: boolean; close: ReturnType<typeof vi.fn>; location: { href: string };
  document: { open(): void; write(html: string): void; close(): void };
};

beforeEach(() => {
  vi.useFakeTimers();
  written = [];
  tab = {
    opener: window, closed: false, close: vi.fn(), location: { href: '' },
    document: { open: () => {}, write: html => { written.push(html); }, close: () => {} },
  };
  vi.spyOn(window, 'open').mockImplementation(() => tab as unknown as Window);
  URL.createObjectURL = vi.fn(() => 'blob:test/1');
  URL.revokeObjectURL = vi.fn();
});
afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe('вложение во вкладке', () => {
  it('окно открыто до ответа сервера, файл приходит в него, ссылка отзывается с отсрочкой', async () => {
    let answer!: (r: unknown) => void;
    vi.mocked(apiClient.get).mockReturnValue(new Promise(r => { answer = r; }));

    const opened = openAttachmentInNewTab('scans/a.pdf');
    expect(window.open).toHaveBeenCalledTimes(1);
    expect(written.at(-1)).toContain('Загрузка файла…');
    expect(apiClient.get).toHaveBeenCalledWith('/attachments', { params: { path: 'scans/a.pdf' }, responseType: 'blob' });

    answer({ data: new Blob(['%PDF']), headers: { 'content-type': 'application/pdf' } });
    await opened;
    expect(tab.location.href).toBe('blob:test/1');
    expect(URL.revokeObjectURL).not.toHaveBeenCalled();
    vi.advanceTimersByTime(60_000);
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:test/1');
  });

  it('тип картинки из белого списка доходит до вкладки своим', async () => {
    vi.mocked(apiClient.get).mockResolvedValue({ data: new Blob([new Uint8Array([0x89])]), headers: { 'content-type': 'image/png' } });
    await openAttachmentInNewTab('bugs/shot.png');
    expect(vi.mocked(URL.createObjectURL).mock.calls[0][0]).toMatchObject({ type: 'image/png' });
  });

  it('байты не пришли — во вкладке текст отказа, а место получает исключение', async () => {
    vi.mocked(apiClient.get).mockRejectedValue(new Error('404'));
    await expect(openAttachmentInNewTab('scans/gone.pdf')).rejects.toThrow('404');
    expect(written.at(-1)).toContain('Не удалось открыть файл');
    expect(tab.close).not.toHaveBeenCalled();
    expect(URL.createObjectURL).not.toHaveBeenCalled();
  });
});
