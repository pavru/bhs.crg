// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { openPdf } from './pdfEngine';
import { PdfViewer, type PdfViewerProps } from './PdfViewer';
import { fakeDocument, opening, supportedBrowser } from './testEngine';

vi.mock('./pdfEngine', () => ({ openPdf: vi.fn(), ViewerFailure: class extends Error {} }));
vi.mock('./printPdf', () => ({ printPdf: vi.fn(), canPrintInline: () => true }));

/**
 * Лупа в просмотрщике (issue #1871, [UI-92], [UI-94]; макет А): кнопка включает и озвучивает, при лупе
 * текст не выделяется, Esc выключает — поверх экрана первый Esc лупе, второй окну, где бы ни был фокус;
 * новый документ и масштаб от 300 % лупу выключают. PDF и картинка — одно поведение.
 */

const BYTES = new Uint8Array([37, 80, 68, 70]);
const PHOTO = new Blob([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], { type: 'image/png' });
let restore: () => void;

beforeEach(() => {
  restore = supportedBrowser();
  // Область измерена: без размера «по ширине» лента PDF стоит на 100 % при любом режиме.
  vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(800);
  vi.spyOn(HTMLElement.prototype, 'clientHeight', 'get').mockReturnValue(600);
  URL.createObjectURL = vi.fn(() => 'blob:test/1');
  URL.revokeObjectURL = vi.fn();
  vi.mocked(openPdf).mockReset().mockImplementation(() => opening(fakeDocument(3)));
  localStorage.clear();
});
afterEach(() => {
  cleanup();
  restore();
  vi.restoreAllMocks();
});

const props = (over: Partial<PdfViewerProps> = {}): PdfViewerProps =>
  ({ source: BYTES, documentKey: 'a', title: 'Счёт.pdf', ...over });

const toggle = (within: ParentNode = document) =>
  waitFor(() => {
    const b = within.querySelector<HTMLButtonElement>('[data-testid=viewer-loupe-toggle]');
    if (!b) throw new Error('кнопки «Лупа» нет');
    return b;
  });

/** Картинка «загрузилась»: jsdom картинок не декодирует. */
async function photoLoaded() {
  const img = await screen.findByRole('img', { name: 'Счёт.png' });
  Object.defineProperty(img, 'naturalWidth', { value: 400, configurable: true });
  Object.defineProperty(img, 'naturalHeight', { value: 200, configurable: true });
  fireEvent.load(img);
}

describe.each([
  ['PDF', () => props(), async () => {}],
  ['картинка', () => props({ source: PHOTO, title: 'Счёт.png' }), photoLoaded],
])('лупа: %s', (_kind, make, ready) => {
  it('кнопка включает: aria-pressed, озвучка, текст под мышью не выделяется', async () => {
    render(<PdfViewer {...make()} />);
    await ready();
    const button = await toggle();
    expect(button.getAttribute('aria-pressed')).toBe('false');
    fireEvent.click(button);
    expect(button.getAttribute('aria-pressed')).toBe('true');
    expect(screen.getByTestId('viewer-live').textContent).toBe('Лупа включена');
    expect(screen.getByTestId('viewer-region').hasAttribute('data-loupe')).toBe(true);
    expect(screen.getByTestId('viewer-loupe').getAttribute('aria-hidden')).toBe('true');
    fireEvent.click(button);
    expect(screen.getByTestId('viewer-live').textContent).toBe('Лупа выключена');
    expect(screen.getByTestId('viewer-region').hasAttribute('data-loupe')).toBe(false);
    expect(screen.queryByTestId('viewer-loupe')).toBeNull();
  });

  it('Esc с фокусом в области выключает', async () => {
    render(<PdfViewer {...make()} />);
    await ready();
    fireEvent.click(await toggle());
    const region = screen.getByTestId('viewer-region');
    region.focus();
    fireEvent.keyDown(region, { key: 'Escape' });
    expect((await toggle()).getAttribute('aria-pressed')).toBe('false');
  });

  it('новый ключ документа — лупа выключена', async () => {
    const { rerender } = render(<PdfViewer {...make()} />);
    await ready();
    fireEvent.click(await toggle());
    rerender(<PdfViewer {...make()} documentKey="b" />);
    await ready();
    expect((await toggle()).getAttribute('aria-pressed')).toBe('false');
  });

  it('масштаб от 300 % — лупа выключена, кнопка неактивна; ниже — снова доступна', async () => {
    render(<PdfViewer {...make()} />);
    await ready();
    fireEvent.click(await toggle());
    const region = screen.getByTestId('viewer-region');
    // Ступенями крупнее, пока лупа не станет недоступна: первая такая ступень — ровно 300 %.
    const shown = () => document.querySelector('[data-testid=viewer-loupe-toggle]')!;
    for (let i = 0; i < 12 && shown().getAttribute('aria-disabled') !== 'true'; i++) {
      expect(shown().getAttribute('aria-pressed')).toBe('true');
      fireEvent.keyDown(region, { key: '+', code: 'Equal', ctrlKey: true });
    }
    expect(screen.getByTestId('viewer-zoom').textContent).toContain('300 %');
    await waitFor(async () => expect((await toggle()).getAttribute('aria-disabled')).toBe('true'));
    expect((await toggle()).getAttribute('aria-pressed')).toBe('false');
    expect(screen.queryByTestId('viewer-loupe')).toBeNull();
    fireEvent.keyDown(region, { key: '-', code: 'Minus', ctrlKey: true });
    await waitFor(async () => expect((await toggle()).getAttribute('aria-disabled')).toBeNull());
  });
});

describe('лупа без точного указателя', () => {
  it('устройство только с сенсором — кнопки «Лупа» нет', async () => {
    const had = window.matchMedia;
    window.matchMedia = ((query: string) => ({ ...had(query), matches: false })) as typeof window.matchMedia;
    try {
      render(<PdfViewer {...props()} />);
      await screen.findByTestId('viewer-toolbar');
      expect(screen.queryByRole('button', { name: 'Лупа' })).toBeNull();
    } finally {
      window.matchMedia = had;
    }
  });
});

describe('лупа поверх экрана', () => {
  it('первый Esc — лупе, второй — окну; и когда фокус в шапке окна, а указатель над листом', async () => {
    const onOpenChange = vi.fn();
    render(<PdfViewer {...props({ overlay: { open: true, onOpenChange } })} />);
    const dialog = await screen.findByRole('dialog');
    const button = await toggle(dialog);
    fireEvent.click(button);
    expect(button.getAttribute('aria-pressed')).toBe('true');

    // Фокус — на «Закрыть» в шапке окна, вне просмотрщика; указатель — над листом.
    const close = screen.getAllByRole('button', { name: 'Закрыть' }).find(b => dialog.contains(b))!;
    close.focus();
    fireEvent.pointerOver(dialog.querySelector('[data-testid=viewer-region]')!);
    fireEvent.keyDown(close, { key: 'Escape' });
    await new Promise(r => setTimeout(r, 10));
    expect(onOpenChange).not.toHaveBeenCalled();
    expect(button.getAttribute('aria-pressed')).toBe('false');

    fireEvent.keyDown(close, { key: 'Escape' });
    await waitFor(() => expect(onOpenChange).toHaveBeenCalledWith(false));
  });
});
