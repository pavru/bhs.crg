// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { StrictMode } from 'react';
import { openPdf, type LoadingDocument, type PageTask, type PdfDocument } from './pdfEngine';
import { PdfViewer, type PdfViewerProps } from './PdfViewer';
import { fakeDocument, opening, supportedBrowser, type FakeDocument } from './testEngine';

vi.mock('./pdfEngine', () => ({
  openPdf: vi.fn(),
  ViewerFailure: class extends Error {},
}));
vi.mock('./printPdf', () => ({ printPdf: vi.fn(), canPrintInline: () => true }));

/**
 * Подмена документа тем же ключом (issue #1493, [UI-25]) с подменённым движком: новый вариант
 * рисуется вне экрана, пока на экране прежний; лента ни разу не пустая; позиция — та, что была в
 * момент подмены; устаревшая загрузка гасится; прежний документ уничтожается ровно раз.
 */

const BYTES = new Uint8Array([37, 80, 68, 70]);
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
  localStorage.clear();
});
afterEach(() => {
  cleanup();
  restore();
  vi.restoreAllMocks();
});

const props = (over: Partial<PdfViewerProps> = {}): PdfViewerProps =>
  ({ source: BYTES, documentKey: 'a', title: 'Предпросмотр документа', ...over });

const view = (over: Partial<PdfViewerProps> = {}) => <StrictMode><PdfViewer {...props(over)} /></StrictMode>;

const pageField = () => screen.findByRole('textbox', { name: 'Номер страницы' }) as Promise<HTMLInputElement>;

async function goToPage(n: number) {
  const field = await pageField();
  fireEvent.focus(field);
  fireEvent.change(field, { target: { value: String(n) } });
  fireEvent.keyDown(field, { key: 'Enter' });
}

/** Открытие, которое отвечает, когда скажут. */
function pendingOpening() {
  let answer!: (d: PdfDocument) => void;
  const loading: LoadingDocument = { promise: new Promise(r => { answer = r; }), cancel: vi.fn() };
  return { loading, answer };
}

/** Документ, чьи листы рисуются, когда скажут: так видно, что идёт до подмены, а что после. */
function slowDocument(numPages: number) {
  const d = fakeDocument(numPages);
  const drawn: (() => void)[] = [];
  const cancels: ReturnType<typeof vi.fn>[] = [];
  // На экране ли был холст, в который движку велели рисовать: обнулив его размеры, движок выбелил бы лист.
  const onScreen: boolean[] = [];
  d.render = vi.fn((_n: number, canvas: HTMLCanvasElement): PageTask => {
    onScreen.push(canvas.isConnected);
    const cancel = vi.fn();
    cancels.push(cancel);
    return { promise: new Promise<void>(r => { drawn.push(r); }), cancel };
  });
  return { d, finish: () => drawn.splice(0).forEach(r => r()), cancels, onScreen };
}

/** Меньшее число холстов в ленте за время наблюдения. */
function watchCanvases() {
  const region = screen.getByTestId('viewer-region');
  const count = () => region.querySelectorAll('canvas').length;
  let min = count();
  const observer = new MutationObserver(() => { min = Math.min(min, count()); });
  observer.observe(region, { childList: true, subtree: true });
  return { min: () => { observer.disconnect(); return Math.min(min, count()); } };
}

describe('просмотрщик: новые байты того же документа', () => {
  it('новый вариант рисуется вне экрана, лента ни разу не пустая, прежний уничтожен ровно раз', async () => {
    const { rerender, unmount } = render(view());
    await pageField();
    const first = docs.at(-1)!;
    const canvases = watchCanvases();

    const next = slowDocument(12);
    vi.mocked(openPdf).mockImplementation(() => { docs.push(next.d); return opening(next.d); });
    rerender(view({ source: new Uint8Array(BYTES) }));
    await waitFor(() => expect(next.d.render).toHaveBeenCalled());

    // Листы нового рисуются в холсты вне ленты; на экране прежний документ, он жив.
    const offscreen = vi.mocked(next.d.render).mock.calls.map(([, canvas]) => canvas);
    expect(offscreen.every(c => !c.isConnected)).toBe(true);
    expect(first.destroyed).toBe(0);
    expect(screen.getAllByRole('group', { name: /^Страница \d+ из 12$/ }).length).toBe(12);

    await act(async () => next.finish());
    await waitFor(() => expect(first.destroyed).toBe(1));
    expect(canvases.min()).toBeGreaterThan(0);
    expect(screen.queryByText('Файл загружается…')).toBeNull();
    // Лист, нарисованный заранее, забран лентой, а не нарисован второй раз.
    const firstPageDraws = vi.mocked(next.d.render).mock.calls.filter(([n]) => n === 1);
    expect(firstPageDraws).toHaveLength(1);

    unmount();
    await act(() => Promise.resolve());
    expect(docs.every(d => d.destroyed === 1)).toBe(true);
  });

  it('прокрутили во время сборки — после подмены та страница, что была в момент подмены', async () => {
    const { rerender } = render(view());
    await goToPage(3);
    const late = pendingOpening();
    vi.mocked(openPdf).mockImplementation(() => late.loading);
    rerender(view({ source: new Uint8Array(BYTES) }));
    await act(() => Promise.resolve());

    await goToPage(5);
    const next = fakeDocument(12);
    docs.push(next);
    await act(async () => late.answer(next));
    await waitFor(() => expect(docs[docs.length - 2].destroyed).toBe(1));
    expect((await pageField()).value).toBe('5');
  });

  it('две загрузки подряд — показана вторая, первая погашена и уничтожена', async () => {
    const { rerender } = render(view());
    await pageField();
    const shownFirst = docs.at(-1)!;

    // Первые новые байты: документ открыт, его листы ещё рисуются вне экрана.
    const second = slowDocument(12);
    vi.mocked(openPdf).mockImplementation(() => { docs.push(second.d); return opening(second.d); });
    rerender(view({ source: new Uint8Array(BYTES) }));
    await waitFor(() => expect(second.d.render).toHaveBeenCalled());

    // Вторые — раньше, чем первые встали, и ещё одни, чьё открытие не успело ответить.
    const stale = pendingOpening();
    vi.mocked(openPdf).mockImplementation(() => stale.loading);
    rerender(view({ source: new Uint8Array(BYTES) }));
    const third = fakeDocument(4);
    vi.mocked(openPdf).mockImplementation(() => { docs.push(third); return opening(third); });
    rerender(view({ source: new Uint8Array(BYTES) }));

    expect(await screen.findByText('из 4')).toBeTruthy();
    expect(second.cancels.every(c => c.mock.calls.length > 0)).toBe(true);
    expect(stale.loading.cancel).toHaveBeenCalled();
    await waitFor(() => expect(second.d.destroyed).toBe(1));
    expect(shownFirst.destroyed).toBe(1);

    // Погашенное открытие всё же ответило — его документ уничтожен и поверх нового не лёг.
    const lateDoc = fakeDocument(7);
    await act(async () => stale.answer(lateDoc));
    expect(lateDoc.destroyed).toBe(1);
    expect(screen.getByText('из 4')).toBeTruthy();
    expect(third.destroyed).toBe(0);
  });

  it('вид сменился во время сборки — лист рисуется вне экрана, прежняя картинка на месте до готовой', async () => {
    const { rerender } = render(view());
    await pageField();
    const next = slowDocument(12);
    vi.mocked(openPdf).mockImplementation(() => { docs.push(next.d); return opening(next.d); });
    rerender(view({ source: new Uint8Array(BYTES) }));
    await waitFor(() => expect(next.d.render).toHaveBeenCalled());

    // Подготовленное нарисовано без поворота — после поворота его забрать нельзя.
    const sheet = () => screen.getByRole('group', { name: 'Страница 1 из 12' });
    const unrotated = sheet().querySelector('canvas');
    fireEvent.click(screen.getByRole('button', { name: 'Повернуть по часовой' }));
    // Прежний документ перерисован с поворотом — это и есть картинка, которая держится до подмены.
    const before = await waitFor(() => {
      const c = sheet().querySelector('canvas');
      expect(c && c !== unrotated).toBe(true);
      return c!;
    });
    const prepared = vi.mocked(next.d.render).mock.calls.length;
    await act(async () => next.finish());
    await waitFor(() => expect(vi.mocked(next.d.render).mock.calls.length).toBeGreaterThan(prepared));

    // Новый вариант встал, его лист ещё рисуется — на экране прежняя картинка, а не обнулённый холст.
    expect(sheet().querySelectorAll('canvas')).toHaveLength(1);
    expect(sheet().querySelector('canvas')).toBe(before);
    expect(before.width).toBeGreaterThan(0);

    await act(async () => next.finish());
    const after = sheet().querySelector('canvas');
    expect(after).not.toBe(before);
    expect(before.isConnected).toBe(false);
    expect(before.width).toBe(0);
    expect(next.onScreen.every(on => !on)).toBe(true);
  });

  it('листов стало меньше — номер прижат к последнему', async () => {
    const { rerender } = render(view());
    await goToPage(10);
    vi.mocked(openPdf).mockImplementation(() => { const d = fakeDocument(3); docs.push(d); return opening(d); });
    rerender(view({ source: new Uint8Array(BYTES) }));
    expect(await screen.findByText('из 3')).toBeTruthy();
    expect((await pageField()).value).toBe('3');
  });
});
