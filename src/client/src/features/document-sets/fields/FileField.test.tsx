// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { apiClient } from '@/shared/api/client';
import type { FileAttachment } from '@/shared/api/attachments';
import { openPdf } from '@/shared/ui/viewer/pdfEngine';
import { fakeDocument, opening, supportedBrowser } from '@/shared/ui/viewer/testEngine';
import { FilePreviewModal } from './FileField';

vi.mock('@/shared/api/client', () => ({ apiClient: { get: vi.fn() } }));
// Движок подменён целиком: настоящий pdf.js в jsdom не нужен (его проверяет pdfEngine.test.ts в Node).
vi.mock('@/shared/ui/viewer/pdfEngine', () => ({
  openPdf: vi.fn(),
  ViewerFailure: class extends Error {
    readonly kind: string;
    constructor(kind: string) { super(kind); this.kind = kind; }
  },
}));
vi.mock('@/shared/ui/viewer/printPdf', () => ({ printPdf: vi.fn(), canPrintInline: () => true }));

/**
 * Окно поля-файла документа (issue #1492): PDF — общим просмотрщиком в области явной высоты, а не
 * фреймом браузера; офисный файл — как прежде, «Предпросмотр недоступен» и «Скачать».
 */

const PDF: FileAttachment = {
  $type: 'file', blobPath: 'attachments/a.pdf', fileName: 'Паспорт.pdf', mimeType: 'application/pdf', size: 4,
};
const XLSX: FileAttachment = {
  $type: 'file', blobPath: 'attachments/b.xlsx', fileName: 'Ведомость.xlsx',
  mimeType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', size: 4,
};

let restore: () => void;

beforeEach(() => {
  restore = supportedBrowser();
  vi.mocked(openPdf).mockReset().mockImplementation(() => opening(fakeDocument(2)));
  vi.mocked(apiClient.get).mockReset().mockImplementation((_url, config) => Promise.resolve({
    data: new Blob([new Uint8Array([37, 80, 68, 70])]),
    headers: { 'content-type': (config?.params as { path: string }).path.endsWith('.pdf')
      ? 'application/pdf'
      : 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' },
  }));
  URL.createObjectURL = vi.fn(() => 'blob:test');
  URL.revokeObjectURL = vi.fn();
});
afterEach(() => {
  cleanup();
  restore();
});

describe('FilePreviewModal', () => {
  it('PDF — область просмотра с листами, высота области задана, фрейма нет', async () => {
    render(<FilePreviewModal open onOpenChange={() => {}} attachment={PDF} />);

    const region = await screen.findByRole('region', { name: 'Паспорт.pdf' });
    expect(screen.getByTestId('file-preview-area').contains(region)).toBe(true);
    // Без явной высоты лента листов нулевой высоты, и лист пуст без ошибки.
    expect(screen.getByTestId('file-preview-area').className).toContain('h-[70vh]');
    expect(document.querySelector('iframe')).toBeNull();
    await waitFor(() => expect(openPdf).toHaveBeenCalled());
  });

  it('PDF — верхняя строка окна: имя, «Открыть во вкладке», «Закрыть»', async () => {
    const onOpenChange = vi.fn();
    render(<FilePreviewModal open onOpenChange={onOpenChange} attachment={PDF} />);

    expect(await screen.findByRole('button', { name: 'Открыть во вкладке' })).toBeTruthy();
    // Своя шапка окна убрана (её крестика нет): имя и выход — в строке просмотрщика.
    expect(document.querySelector('[data-modal-cross]')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Закрыть' }));
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });

  it('офисный файл — прежний текст и «Скачать», просмотрщика нет', async () => {
    render(<FilePreviewModal open onOpenChange={() => {}} attachment={XLSX} />);

    expect(await screen.findByText('Предпросмотр недоступен для этого формата.')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Скачать' }).getAttribute('download')).toBe('Ведомость.xlsx');
    expect(screen.queryByRole('region')).toBeNull();
    expect(openPdf).not.toHaveBeenCalled();
  });
});
