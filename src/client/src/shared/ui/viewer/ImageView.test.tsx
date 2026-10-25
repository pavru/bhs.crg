// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { openPdf } from './pdfEngine';
import { printPdf } from './printPdf';
import { loadViewer } from './viewerGate';
import { PdfViewer, PdfViewerOverlay, type PdfViewerProps } from './PdfViewer';
import { fakeDocument, opening, supportedBrowser } from './testEngine';

vi.mock('./pdfEngine', () => ({ openPdf: vi.fn(), ViewerFailure: class extends Error {} }));
vi.mock('./printPdf', () => ({ printPdf: vi.fn(), canPrintInline: () => true }));
vi.mock('./viewerGate', async orig => {
  const real = await orig<typeof import('./viewerGate')>();
  return { ...real, loadViewer: vi.fn(real.loadViewer) };
});

/**
 * Режим картинки просмотрщика (issue #1491, [UI-24]): фото счёта той же панелью, что PDF, — масштаб и
 * поворот есть, нумерации нет, pdf.js не грузится. Тип картинки — из белого списка клиента.
 */

const PHOTO = new Blob([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], { type: 'image/png' });
let restore: () => void;

beforeEach(() => {
  restore = supportedBrowser();
  let n = 0;
  URL.createObjectURL = vi.fn(() => `blob:test/${++n}`);
  URL.revokeObjectURL = vi.fn();
  vi.mocked(openPdf).mockReset().mockImplementation(() => opening(fakeDocument(2)));
  vi.mocked(printPdf).mockReset();
  vi.mocked(loadViewer).mockClear();
  localStorage.clear();
});
afterEach(() => {
  cleanup();
  restore();
});

const props = (over: Partial<PdfViewerProps> = {}): PdfViewerProps =>
  ({ source: PHOTO, documentKey: 'a', title: 'Счёт.png', ...over });

/** Картинка «загрузилась» с такими размерами: jsdom картинок не декодирует. */
async function loaded(width = 400, height = 200) {
  const img = await screen.findByRole('img', { name: 'Счёт.png' }) as HTMLImageElement;
  Object.defineProperty(img, 'naturalWidth', { value: width, configurable: true });
  Object.defineProperty(img, 'naturalHeight', { value: height, configurable: true });
  fireEvent.load(img);
  await screen.findByTestId('viewer-toolbar');
  return img;
}

const sheet = () => {
  const s = screen.getByTestId('viewer-image-sheet').style;
  return { width: parseFloat(s.width), height: parseFloat(s.height) };
};

describe('просмотрщик: картинка', () => {
  it('панель без блока страниц, pdf.js не загружен', async () => {
    render(<PdfViewer {...props()} />);
    await loaded();
    expect(screen.queryByRole('textbox', { name: 'Номер страницы' })).toBeNull();
    expect(screen.queryByText(/^из /)).toBeNull();
    expect(screen.getByRole('button', { name: 'Повернуть по часовой' })).toBeTruthy();
    expect(screen.getByTestId('viewer-zoom')).toBeTruthy();
    await act(() => Promise.resolve());
    expect(loadViewer).not.toHaveBeenCalled();
    expect(openPdf).not.toHaveBeenCalled();
  });

  it('поворот на 90° меняет пропорции листа, картинка повёрнута', async () => {
    render(<PdfViewer {...props()} />);
    const img = await loaded(400, 200);
    expect(sheet()).toEqual({ width: 400, height: 200 });
    fireEvent.click(screen.getByRole('button', { name: 'Повернуть по часовой' }));
    expect(sheet()).toEqual({ width: 200, height: 400 });
    expect(img.style.transform).toBe('rotate(90deg)');
  });

  it('масштаб 200 % увеличивает картинку', async () => {
    localStorage.setItem('bhs.viewer.zoom.test', '2');
    render(<PdfViewer {...props({ rememberZoomAs: 'test' })} />);
    await loaded(400, 200);
    expect(screen.getByTestId('viewer-zoom').textContent).toContain('200 %');
    expect(sheet()).toEqual({ width: 800, height: 400 });
  });

  it('новый ключ документа сбрасывает поворот', async () => {
    const { rerender } = render(<PdfViewer {...props()} />);
    await loaded(400, 200);
    fireEvent.click(screen.getByRole('button', { name: 'Повернуть по часовой' }));
    const next = new Blob([new Uint8Array([1])], { type: 'image/jpeg' });
    rerender(<PdfViewer {...props({ source: next, documentKey: 'b' })} />);
    const img = await loaded(400, 200);
    expect(sheet()).toEqual({ width: 400, height: 200 });
    expect(img.style.transform).toBe('');
  });

  it('Ctrl+P печатает саму картинку', async () => {
    render(<PdfViewer {...props()} />);
    await loaded();
    expect(fireEvent.keyDown(screen.getByTestId('viewer-region'), { key: 'p', code: 'KeyP', ctrlKey: true })).toBe(false);
    expect(printPdf).toHaveBeenCalledWith(PHOTO);
  });

  it('картинка не декодируется — отказ словами, а не пустой лист', async () => {
    render(<PdfViewer {...props({ actions: <button type="button">Оригинал</button> })} />);
    fireEvent.error(await screen.findByRole('img', { name: 'Счёт.png' }));
    expect((await screen.findByRole('alert')).getAttribute('data-state')).toBe('broken');
    expect(screen.getByRole('button', { name: 'Оригинал' })).toBeTruthy();
  });

  it.each(['text/html', 'image/svg+xml'])('тип %s в режим картинки не попадает — идёт в pdf.js', async type => {
    render(<PdfViewer {...props({ source: new Blob(['<svg/>'], { type }) })} />);
    await screen.findByTestId('viewer-toolbar');
    expect(screen.queryByRole('img')).toBeNull();
    expect(openPdf).toHaveBeenCalled();
  });
});

describe('просмотрщик поверх экрана без показа на месте', () => {
  it('строка места над листом, «Открыть во вкладке» и «Закрыть»', async () => {
    const onOpenChange = vi.fn();
    render(<PdfViewerOverlay {...props()} open onOpenChange={onOpenChange} note={<span>Вид для чтения — оговорка.</span>} />);
    const dialog = await screen.findByRole('dialog', { name: 'Счёт.png' });
    expect(dialog.textContent).toContain('Вид для чтения — оговорка.');
    expect(screen.getByRole('button', { name: 'Открыть во вкладке' })).toBeTruthy();
    // Показа на месте нет: просмотрщик один, в слое.
    expect(screen.getAllByTestId('viewer-region')).toHaveLength(1);
    fireEvent.click(screen.getByRole('button', { name: 'Закрыть' }));
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });

  it('без строки места лишней полосы нет', async () => {
    render(<PdfViewerOverlay {...props()} open onOpenChange={() => {}} />);
    await screen.findByRole('dialog', { name: 'Счёт.png' });
    expect(screen.queryByText(/оговорка/)).toBeNull();
  });
});
