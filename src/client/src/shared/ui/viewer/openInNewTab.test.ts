// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { openInNewTab, TabBlocked } from './openInNewTab';

/**
 * «Открыть во вкладке» (issue #1490, [UI-20]): окно — синхронно, в обработчике нажатия, иначе его режет
 * блокировщик; тип файла — свой, `application/pdf`, что бы ни ответил сервер.
 */

let made: Blob[];
let written: string[];
let tab: {
  opener: unknown; closed: boolean; close: ReturnType<typeof vi.fn>; location: { href: string };
  document: { open(): void; write(html: string): void; close(): void };
};

beforeEach(() => {
  vi.useFakeTimers();
  made = [];
  written = [];
  tab = {
    opener: window, closed: false, close: vi.fn(), location: { href: '' },
    document: { open: () => {}, write: html => { written.push(html); }, close: () => {} },
  };
  vi.spyOn(window, 'open').mockImplementation(() => tab as unknown as Window);
  URL.createObjectURL = vi.fn((b: Blob) => { made.push(b); return `blob:test/${made.length}`; });
  URL.revokeObjectURL = vi.fn();
});
afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe('открыть во вкладке', () => {
  it('с загрузчиком окно открыто до ответа загрузчика и получает файл по готовности', async () => {
    let answer!: (b: Blob) => void;
    const opened = openInNewTab(() => new Promise<Blob>(r => { answer = r; }));
    expect(window.open).toHaveBeenCalledTimes(1);
    expect(tab.opener).toBeNull();
    expect(tab.location.href).toBe('');
    expect(written.at(-1)).toContain('Загрузка файла…');

    answer(new Blob(['%PDF'], { type: 'application/pdf' }));
    await opened;
    expect(tab.location.href).toBe('blob:test/1');
  });

  it('тип `text/html` от сервера не проходит: во вкладку идёт `application/pdf`', async () => {
    await openInNewTab(() => Promise.resolve(new Blob(['<script>alert(1)</script>'], { type: 'text/html' })));
    expect(made).toHaveLength(1);
    expect(made[0].type).toBe('application/pdf');
  });

  it('картинка из белого списка идёт своим типом, векторная — нет', async () => {
    await openInNewTab(new Blob([new Uint8Array([0xff, 0xd8])], { type: 'image/jpeg' }));
    await openInNewTab(new Blob(['<svg onload="alert(1)"/>'], { type: 'image/svg+xml' }));
    expect(made.map(b => b.type)).toEqual(['image/jpeg', 'application/pdf']);
  });

  it('ссылка отзывается с отсрочкой, а не сразу', async () => {
    await openInNewTab(new Uint8Array([37, 80, 68, 70]));
    expect(URL.revokeObjectURL).not.toHaveBeenCalled();
    vi.advanceTimersByTime(60_000);
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:test/1');
  });

  it('отказ загрузчика вкладка называет словами, и он доходит до вызывающего', async () => {
    await expect(openInNewTab(() => Promise.reject(new Error('404')))).rejects.toThrow('404');
    expect(written.at(-1)).toContain('Не удалось открыть файл');
    // Не «обновите страницу»: в этой вкладке это значило бы обновить её саму.
    expect(written.at(-1)).toContain('на странице приложения');
    expect(tab.close).not.toHaveBeenCalled();
    expect(URL.createObjectURL).not.toHaveBeenCalled();
  });

  it('вкладку, в которой не написать об отказе, отказ закрывает — пустой она не остаётся', async () => {
    tab.document.write = () => { throw new Error('чужой источник'); };
    await expect(openInNewTab(() => Promise.reject(new Error('404')))).rejects.toThrow('404');
    expect(tab.close).toHaveBeenCalled();
  });

  it('вкладку не дали открыть — загрузчик не зовётся, вызывающий получает TabBlocked', async () => {
    vi.mocked(window.open).mockReturnValue(null);
    const load = vi.fn(() => Promise.resolve(new Blob(['%PDF'])));
    await expect(openInNewTab(load)).rejects.toBeInstanceOf(TabBlocked);
    expect(load).not.toHaveBeenCalled();
    expect(window.open).toHaveBeenCalledTimes(1);
  });
});
