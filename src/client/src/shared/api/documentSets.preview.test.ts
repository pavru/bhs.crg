// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from './client';
import { previewGeneratedFile } from './documentSets';
import { TabBlocked } from '@/shared/ui/viewer/openInNewTab';

vi.mock('./client', () => ({ apiClient: { get: vi.fn() } }));

/**
 * Готовый PDF документа во вкладке (issue #1493): окно открыто в обработчике нажатия, до ответа
 * сервера, а ссылка на файл отзывается с отсрочкой — прежде она не отзывалась никогда, и каждый
 * открытый PDF держался в памяти страницы до её закрытия.
 */

let tab: { opener: unknown; closed: boolean; close: () => void; location: { href: string }; document: Document };

beforeEach(() => {
  vi.mocked(apiClient.get).mockReset();
  vi.useFakeTimers();
  tab = {
    opener: window, closed: false, close: vi.fn(), location: { href: '' },
    document: { open: () => {}, write: () => {}, close: () => {} } as unknown as Document,
  };
  vi.spyOn(window, 'open').mockImplementation(() => tab as unknown as Window);
  URL.createObjectURL = vi.fn(() => 'blob:test/1');
  URL.revokeObjectURL = vi.fn();
});
afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe('открыть готовый PDF документа', () => {
  it('вкладка открыта сразу, получает файл, ссылка отозвана после отсрочки', async () => {
    let answer!: (r: { data: Blob }) => void;
    vi.mocked(apiClient.get).mockReturnValue(new Promise(r => { answer = r; }));
    const opened = previewGeneratedFile('doc-1', 'tpl-1');
    expect(window.open).toHaveBeenCalledTimes(1);
    expect(apiClient.get).toHaveBeenCalledWith('/generate/download/doc-1/tpl-1/pdf', { responseType: 'blob' });

    answer({ data: new Blob(['%PDF'], { type: 'application/pdf' }) });
    await opened;
    expect(tab.location.href).toBe('blob:test/1');
    expect(URL.revokeObjectURL).not.toHaveBeenCalled();
    vi.advanceTimersByTime(60_000);
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:test/1');
  });

  it('отказ сервера не уходит необработанным: о нём говорит вкладка', async () => {
    vi.mocked(apiClient.get).mockRejectedValue(new Error('404'));
    await expect(previewGeneratedFile('doc-1')).resolves.toBeUndefined();
    expect(apiClient.get).toHaveBeenCalledWith('/generate/download/doc-1/pdf', { responseType: 'blob' });
    expect(URL.createObjectURL).not.toHaveBeenCalled();
  });

  it('вкладку не дали открыть — файл не скачивается, отказ уходит вызывающему', async () => {
    vi.mocked(window.open).mockReturnValue(null);
    await expect(previewGeneratedFile('doc-1')).rejects.toBeInstanceOf(TabBlocked);
    expect(apiClient.get).not.toHaveBeenCalled();
  });
});
