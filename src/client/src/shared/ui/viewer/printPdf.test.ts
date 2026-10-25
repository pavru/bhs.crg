// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { printPdf } from './printPdf';

/**
 * Ctrl+P просмотрщика (issue #1490, #1491): печатает скрытый фрейм, а где встроенного показа PDF нет
 * (телефоны) — файл уходит во вкладку. Картинка — так же: `print()` фрейма там печатает страницу
 * приложения или пустой лист и не бросает, запасной путь сам не сработал бы.
 */

const PHOTO = new Blob([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], { type: 'image/png' });
const PDF = new Blob(['%PDF'], { type: 'application/pdf' });

const frames = () => document.querySelectorAll('iframe').length;

function inlinePdf(enabled: boolean) {
  Object.defineProperty(navigator, 'pdfViewerEnabled', { value: enabled, configurable: true });
}

beforeEach(() => {
  let n = 0;
  URL.createObjectURL = vi.fn(() => `blob:test/${++n}`);
  URL.revokeObjectURL = vi.fn();
  vi.spyOn(window, 'open').mockImplementation(() => null);
});
afterEach(() => {
  vi.restoreAllMocks();
  document.querySelectorAll('iframe').forEach(f => f.remove());
});

describe('печать просмотрщика', () => {
  it.each([['PDF', PDF], ['картинка', PHOTO]])('%s со встроенным показом — фреймом, без вкладки', (_, source) => {
    inlinePdf(true);
    printPdf(source);
    expect(frames()).toBe(1);
    expect(window.open).not.toHaveBeenCalled();
  });

  it.each([['PDF', PDF], ['картинка', PHOTO]])('%s на телефоне — во вкладку, без фрейма', (_, source) => {
    inlinePdf(false);
    printPdf(source);
    expect(frames()).toBe(0);
    expect(window.open).toHaveBeenCalled();
  });
});
