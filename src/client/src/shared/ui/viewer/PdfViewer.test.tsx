// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { StrictMode, type ReactElement } from 'react';
import { openPdf, ViewerFailure } from './pdfEngine';
import { printPdf } from './printPdf';
import { PdfViewer, type PdfViewerProps } from './PdfViewer';
import { fakeDocument, opening, supportedBrowser, type FakeDocument } from './testEngine';

// Движок подменён целиком: настоящий pdf.js в jsdom не нужен (его проверяет pdfEngine.test.ts в Node).
vi.mock('./pdfEngine', () => ({
  openPdf: vi.fn(),
  ViewerFailure: class extends Error {
    readonly kind: string;
    constructor(kind: string) { super(kind); this.kind = kind; }
  },
}));
vi.mock('./printPdf', () => ({ printPdf: vi.fn(), canPrintInline: () => true }));

/**
 * Просмотрщик PDF с подменённым движком (issue #1490, задачи 2.3–2.6 и 2.8): панель, отказы словами,
 * ключ документа, уничтожение документа, клавиши и Ctrl+P.
 */

const BYTES = new Uint8Array([37, 80, 68, 70]);
const PLACE = <button type="button">Оригинал</button>;
let restore: () => void;
let docs: FakeDocument[];

beforeEach(() => {
  restore = supportedBrowser();
  docs = [];
  vi.mocked(openPdf).mockReset().mockImplementation(() => {
    const d = fakeDocument(12);
    docs.push(d);
    return opening(d);
  });
  vi.mocked(printPdf).mockReset();
  localStorage.clear();
});
afterEach(() => {
  cleanup();
  restore();
  vi.restoreAllMocks();
});

const props = (over: Partial<PdfViewerProps> = {}): PdfViewerProps =>
  ({ source: BYTES, documentKey: 'a', title: 'Счёт.xlsx — вид для чтения', actions: PLACE, ...over });

function shown(ui: ReactElement) {
  return render(<StrictMode>{ui}</StrictMode>);
}

const pageField = () => screen.findByRole('textbox', { name: 'Номер страницы' }) as Promise<HTMLInputElement>;

async function goToPage(n: number) {
  const field = await pageField();
  fireEvent.focus(field);
  fireEvent.change(field, { target: { value: String(n) } });
  fireEvent.keyDown(field, { key: 'Enter' });
}

describe('просмотрщик: страница и границы', () => {
  it('«3 из 12»: переход по номеру, кнопки на краях ленты гаснут', async () => {
    shown(<PdfViewer {...props()} />);
    expect((await pageField()).value).toBe('1');
    expect(screen.getByText('из 12')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Предыдущая страница' }).hasAttribute('disabled')).toBe(true);

    await goToPage(3);
    expect((await pageField()).value).toBe('3');
    expect(screen.getByTestId('viewer-live').textContent).toBe('Страница 3 из 12');

    fireEvent.keyDown(screen.getByTestId('viewer-region'), { key: 'End' });
    expect((await pageField()).value).toBe('12');
    expect(screen.getByRole('button', { name: 'Следующая страница' }).hasAttribute('disabled')).toBe(true);
  });

  it('недопустимый номер молча возвращается к текущему', async () => {
    shown(<PdfViewer {...props()} />);
    await goToPage(99);
    expect((await pageField()).value).toBe('1');
  });

  it('новый ключ документа — показ с первой страницы; тот же ключ с новыми байтами — позиция остаётся', async () => {
    const { rerender } = shown(<PdfViewer {...props()} />);
    await goToPage(3);
    rerender(<StrictMode><PdfViewer {...props({ source: new Uint8Array(BYTES) })} /></StrictMode>);
    await waitFor(() => expect(docs.length).toBeGreaterThan(2));
    expect((await pageField()).value).toBe('3');

    rerender(<StrictMode><PdfViewer {...props({ documentKey: 'b', source: new Uint8Array(BYTES) })} /></StrictMode>);
    await waitFor(async () => expect((await pageField()).value).toBe('1'));
  });
});

describe('просмотрщик: документ уничтожается ровно раз', () => {
  it('при уходе со страницы', async () => {
    const { unmount } = shown(<PdfViewer {...props()} />);
    await pageField();
    const shownDoc = docs.at(-1)!;
    unmount();
    expect(shownDoc.destroyed).toBe(1);
    // Отменённые StrictMode открытия тоже не утекли: каждый документ уничтожен, и не дважды.
    await act(() => Promise.resolve());
    expect(docs.every(d => d.destroyed === 1)).toBe(true);
  });

  it('при замене другим документом — прежний сразу, новый — при уходе', async () => {
    const { rerender, unmount } = shown(<PdfViewer {...props()} />);
    await pageField();
    const first = docs.at(-1)!;
    rerender(<StrictMode><PdfViewer {...props({ documentKey: 'b', source: new Uint8Array(BYTES) })} /></StrictMode>);
    await waitFor(() => expect(first.destroyed).toBe(1));
    await pageField();
    unmount();
    await act(() => Promise.resolve());
    expect(docs.every(d => d.destroyed === 1)).toBe(true);
  });
});

describe('просмотрщик: отказы словами и с выходом', () => {
  it.each([
    ['not-pdf', 'Файл не читается как PDF', { tab: false, place: true }],
    ['password', 'PDF защищён паролем', { tab: true, place: false }],
    ['no-pages', 'В файле нет страниц', { tab: false, place: true }],
    ['worker', 'Просмотрщик не запустился', { tab: true, place: false }],
    ['broken', 'Файл не показан', { tab: true, place: true }],
  ] as const)('%s → «%s»', async (kind, head, exits) => {
    vi.mocked(openPdf).mockImplementation(() => opening(new ViewerFailure(kind)));
    shown(<PdfViewer {...props()} />);
    const alert = await screen.findByRole('alert');
    expect(alert.getAttribute('data-state')).toBe(kind);
    expect(alert.textContent).toContain(head);
    expect(!!screen.queryByRole('button', { name: 'Открыть во вкладке' })).toBe(exits.tab);
    expect(!!screen.queryByRole('button', { name: 'Оригинал' })).toBe(exits.place);
    expect(screen.queryByTestId('viewer-toolbar')).toBeNull();
  });

  it('незнакомое исключение движка — «файл не показан», а не пустой лист', async () => {
    vi.mocked(openPdf).mockImplementation(() => opening(new Error('что-то своё')));
    shown(<PdfViewer {...props()} />);
    expect((await screen.findByRole('alert')).getAttribute('data-state')).toBe('broken');
  });

  it('без байтов — «загружается», движок не зовётся', async () => {
    shown(<PdfViewer {...props({ source: null })} />);
    expect(await screen.findByText('Файл загружается…')).toBeTruthy();
    expect(openPdf).not.toHaveBeenCalled();
  });

  it('старый браузер — отказ с «Открыть во вкладке», движок не загружается', async () => {
    delete (globalThis as Record<string, unknown>).Worker;
    shown(<PdfViewer {...props()} />);
    const alert = screen.getByRole('alert');
    expect(alert.getAttribute('data-state')).toBe('old-browser');
    expect(alert.textContent).toContain('Safari 18');
    expect(screen.getByRole('button', { name: 'Открыть во вкладке' })).toBeTruthy();
    await act(() => Promise.resolve());
    expect(openPdf).not.toHaveBeenCalled();
  });
});

describe('просмотрщик: масштаб и хранилище', () => {
  it('режим масштаба запоминается за местом и возвращается при новом показе', async () => {
    const { unmount } = shown(<PdfViewer {...props({ rememberZoomAs: 'test' })} />);
    await pageField();
    fireEvent.keyDown(screen.getByTestId('viewer-region'), { key: '+', code: 'Equal', ctrlKey: true });
    expect(localStorage.getItem('bhs.viewer.zoom.test')).toBe('1.1');
    unmount();
    shown(<PdfViewer {...props({ rememberZoomAs: 'test' })} />);
    expect((await screen.findByTestId('viewer-zoom')).textContent).toContain('110 %');
  });

  it('хранилище бросает — показ работает, масштаб меняется', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('приватный режим'); });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('приватный режим'); });
    shown(<PdfViewer {...props({ rememberZoomAs: 'test' })} />);
    expect((await screen.findByTestId('viewer-zoom')).textContent).toContain('По ширине');
    fireEvent.keyDown(screen.getByTestId('viewer-region'), { key: '+', code: 'Equal', ctrlKey: true });
    expect(screen.getByTestId('viewer-zoom').textContent).toContain('110 %');
  });
});

describe('просмотрщик: клавиши', () => {
  it('голые + и −, Ctrl+F — не перехвачены', async () => {
    shown(<PdfViewer {...props()} />);
    await pageField();
    const region = screen.getByTestId('viewer-region');
    expect(fireEvent.keyDown(region, { key: '+', code: 'Equal' })).toBe(true);
    expect(fireEvent.keyDown(region, { key: '-', code: 'Minus' })).toBe(true);
    expect(fireEvent.keyDown(region, { key: 'f', code: 'KeyF', ctrlKey: true })).toBe(true);
    expect(screen.getByTestId('viewer-zoom').textContent).toContain('По ширине');
  });

  it('Ctrl± и Ctrl 0 с фокусом в листе — масштаб листа, а не страницы браузера', async () => {
    shown(<PdfViewer {...props()} />);
    await pageField();
    const region = screen.getByTestId('viewer-region');
    expect(fireEvent.keyDown(region, { key: '-', code: 'Minus', ctrlKey: true })).toBe(false);
    expect(screen.getByTestId('viewer-zoom').textContent).toContain('90 %');
    expect(fireEvent.keyDown(region, { key: '0', code: 'Digit0', ctrlKey: true })).toBe(false);
    expect(screen.getByTestId('viewer-zoom').textContent).toContain('По ширине');
  });

  it('Ctrl± при указателе над листом и фокусе снаружи — лист; без указателя — браузер', async () => {
    shown(<PdfViewer {...props()} />);
    await pageField();
    const outside = { key: '+', code: 'Equal', ctrlKey: true };
    expect(fireEvent.keyDown(document.body, outside)).toBe(true);
    fireEvent.pointerEnter(screen.getByTestId('viewer-region').parentElement!);
    expect(fireEvent.keyDown(document.body, outside)).toBe(false);
    expect(screen.getByTestId('viewer-zoom').textContent).toContain('110 %');
  });

  it('Ctrl+P с фокусом внутри печатает файл; снаружи — нет', async () => {
    shown(<PdfViewer {...props()} />);
    await pageField();
    // В русской раскладке это «з»: клавишу узнаём по коду.
    expect(fireEvent.keyDown(screen.getByTestId('viewer-region'), { key: 'з', code: 'KeyP', ctrlKey: true })).toBe(false);
    expect(printPdf).toHaveBeenCalledWith(BYTES);
    vi.mocked(printPdf).mockClear();
    expect(fireEvent.keyDown(document.body, { key: 'p', code: 'KeyP', ctrlKey: true })).toBe(true);
    expect(printPdf).not.toHaveBeenCalled();
  });
});

describe('просмотрщик: поверх экрана', () => {
  it('Esc закрывает окно; документ окна уничтожен один раз, документ места цел', async () => {
    const onOpenChange = vi.fn();
    const overlay = (open: boolean) => <PdfViewer {...props({ overlay: { open, onOpenChange } })} />;
    const { rerender } = render(overlay(true));
    const dialog = await screen.findByRole('dialog');
    await waitFor(() => expect(dialog.querySelector('[data-testid=viewer-toolbar]')).toBeTruthy());
    expect(docs).toHaveLength(2);

    fireEvent.keyDown(dialog, { key: 'Escape' });
    await waitFor(() => expect(onOpenChange).toHaveBeenCalledWith(false));
    rerender(overlay(false));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(docs.map(d => d.destroyed).sort()).toEqual([0, 1]);
  });
});
