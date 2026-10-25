// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { ToastProvider } from '@/shared/ui/ToastProvider';
import { apiClient } from '@/shared/api/client';
import type { FileKindsInfo } from '@/shared/api/fileKinds';
import { preloadPdfViewer } from '@/shared/ui/viewer/viewerGate';
import { ScanTooNarrow } from './InvoiceScanPanel';

vi.mock('@/shared/api/client', () => ({ apiClient: { post: vi.fn(), get: vi.fn(), put: vi.fn(), delete: vi.fn() } }));
vi.mock('@/shared/ui/viewer/viewerGate', () => ({ preloadPdfViewer: vi.fn() }));

// Просмотрщик подменён: здесь проверяется место — что и когда оно кладёт в слой, а не сам показ.
vi.mock('@/shared/ui/viewer/PdfViewer', () => ({
  PdfViewer: () => null,
  PdfViewerOverlay: (p: {
    open: boolean; onOpenChange: (open: boolean) => void; source: Blob | null; documentKey: string; title: string; note?: ReactNode;
  }) => (p.open
    ? (
      <div role="dialog" aria-label={p.title} data-key={p.documentKey} data-type={p.source?.type ?? ''}>
        {p.note}{p.source ? 'байты' : 'загружается'}
        <button onClick={() => p.onOpenChange(false)}>Закрыть</button>
      </div>
    )
    : null),
}));

/**
 * Файл счёта на экране уже 1280 px (issue #1491, [COST-6.2]): открывается просмотрщиком поверх формы,
 * ни в одной ветке не новым окном; файл, который показать нечем, скачивается.
 */

const KINDS: FileKindsInfo = {
  unknown: 'application/octet-stream', maxBytes: 50 * 1024 * 1024,
  kinds: [
    { mime: 'application/pdf', label: 'PDF', extensions: ['.pdf'], aliases: [], view: 'pdf', recognized: true },
    { mime: 'image/jpeg', label: 'JPEG', extensions: ['.jpg'], aliases: [], view: 'image', recognized: true },
    { mime: 'application/zip', label: 'ZIP', extensions: ['.zip'], aliases: [], view: null, recognized: false },
  ],
};

let scanType: string;
let imageState: 'original' | 'built';
let kindsUp: boolean;
let saved: string[];
/** Ответы на вид для чтения по очереди запросов; пусто — ответ сразу. */
let readings: Array<{ resolve: (b: Blob) => void; reject: (e: unknown) => void }> | null;

beforeEach(() => {
  scanType = 'application/pdf';
  imageState = 'original';
  kindsUp = true;
  saved = [];
  readings = null;
  vi.mocked(preloadPdfViewer).mockClear();
  vi.spyOn(window, 'open').mockImplementation(() => null);
  URL.createObjectURL = vi.fn(() => 'blob:test/1');
  URL.revokeObjectURL = vi.fn();
  vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) { saved.push(this.download); });
  vi.mocked(apiClient.get).mockReset().mockImplementation((async (url: string) => {
    if (url === '/files/kinds') {
      if (!kindsUp) throw new Error('нет связи');
      return { data: KINDS };
    }
    if (url === '/costs/invoices/i1/scan/image') return { data: { state: imageState, pages: 2, notes: [], reason: null, aboutFile: false } };
    if (url === '/costs/invoices/i1/scan') return { data: new Blob(['x']), headers: { 'content-type': scanType } };
    if (url === '/costs/invoices/i1/scan/image/content') {
      const data = readings ? await new Promise<Blob>((resolve, reject) => readings!.push({ resolve, reject })) : new Blob(['%PDF']);
      return { data, headers: {} };
    }
    throw new Error(`нет в тесте: ${url}`);
  }) as never);
});
afterEach(() => {
  cleanup();
  expect(window.open).not.toHaveBeenCalled();
  vi.restoreAllMocks();
});

function shown(fileName = 'Счёт 417') {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <ToastProvider>
        <ScanTooNarrow invoiceId="i1" blobPath="files/a" fileName={fileName} width={1100} />
      </ToastProvider>
    </QueryClientProvider>,
  );
}

const openFile = async () => {
  const button = await screen.findByRole('button', { name: 'Открыть файл' });
  await waitFor(() => expect(button.hasAttribute('disabled')).toBe(false));
  fireEvent.click(button);
};

describe('узкий экран: файл поверх формы', () => {
  it('причина названа; PDF открыт поверх формы, ключ — счёт и файл', async () => {
    shown();
    expect(await screen.findByText(/не меньше 1280 пикселей, а здесь 1100/)).toBeTruthy();
    await openFile();
    const dialog = await screen.findByRole('dialog', { name: 'Счёт 417' });
    expect(dialog.getAttribute('data-key')).toBe('i1|files/a');
    expect(dialog.getAttribute('data-type')).toBe('application/pdf');
    expect(saved).toEqual([]);
  });

  it('фото — тоже поверх формы', async () => {
    scanType = 'image/jpeg';
    shown();
    await openFile();
    expect((await screen.findByRole('dialog')).getAttribute('data-type')).toBe('image/jpeg');
  });

  it('файл, который показать нечем, скачивается, слоя нет', async () => {
    scanType = 'application/zip';
    shown();
    await openFile();
    await waitFor(() => expect(saved).toEqual(['Счёт 417']));
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('реестр видов не пришёл — файл скачан, и причина названа', async () => {
    kindsUp = false;
    shown();
    await openFile();
    await waitFor(() => expect(saved).toEqual(['Счёт 417']));
    expect(await screen.findByText(/список видов файлов с сервера не пришёл/)).toBeTruthy();
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('вид для чтения — поверх формы сразу, с оговоркой; байты приходят в слой', async () => {
    imageState = 'built';
    shown();
    fireEvent.click(await screen.findByRole('button', { name: 'Открыть вид для чтения' }));
    const dialog = screen.getByRole('dialog', { name: 'Счёт 417 — вид для чтения' });
    expect(dialog.textContent).toContain('вёрстка может отличаться, документ — оригинал');
    await waitFor(() => expect(dialog.textContent).toContain('байты'));
    expect(dialog.getAttribute('data-type')).toBe('application/pdf');
    // Вид — всегда PDF: ядро просмотрщика грузится вместе с байтами.
    expect(preloadPdfViewer).toHaveBeenCalled();
  });

  it('отказ прежнего открытия вида не закрывает слой, открытый после него', async () => {
    imageState = 'built';
    readings = [];
    shown();
    const reading = await screen.findByRole('button', { name: 'Открыть вид для чтения' });
    fireEvent.click(reading);
    fireEvent.click(screen.getByRole('button', { name: 'Закрыть' }));
    fireEvent.click(reading);
    await waitFor(() => expect(readings).toHaveLength(2));

    readings![1].resolve(new Blob(['%PDF']));
    const dialog = screen.getByRole('dialog', { name: 'Счёт 417 — вид для чтения' });
    await waitFor(() => expect(dialog.textContent).toContain('байты'));
    readings![0].reject(new Error('нет связи'));
    await new Promise(r => setTimeout(r, 0));
    expect(screen.getByRole('dialog', { name: 'Счёт 417 — вид для чтения' })).toBeTruthy();
    expect(screen.queryByText(/Вид для чтения не открыт/)).toBeNull();
  });

  it('PDF по имени — ядро просмотрщика грузится вместе с файлом; другое имя — нет', async () => {
    shown('Скан.pdf');
    await openFile();
    expect(preloadPdfViewer).toHaveBeenCalledTimes(1);
    await screen.findByRole('dialog');
    cleanup();
    vi.mocked(preloadPdfViewer).mockClear();
    shown('Фото.jpg');
    await openFile();
    await screen.findByRole('dialog');
    expect(preloadPdfViewer).not.toHaveBeenCalled();
  });
});
