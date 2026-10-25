// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { openInNewTab } from './openInNewTab';

/**
 * «Открыть во вкладке» (issue #1490, [UI-20]): окно — синхронно, в обработчике нажатия, иначе его режет
 * блокировщик; тип файла — свой, `application/pdf`, что бы ни ответил сервер.
 */

let made: Blob[];
let tab: { opener: unknown; closed: boolean; close: ReturnType<typeof vi.fn>; location: { href: string } };

beforeEach(() => {
  vi.useFakeTimers();
  made = [];
  tab = { opener: window, closed: false, close: vi.fn(), location: { href: '' } };
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

  it('отказ загрузчика закрывает вкладку и доходит до вызывающего', async () => {
    await expect(openInNewTab(() => Promise.reject(new Error('404')))).rejects.toThrow('404');
    expect(tab.close).toHaveBeenCalled();
    expect(URL.createObjectURL).not.toHaveBeenCalled();
  });
});
