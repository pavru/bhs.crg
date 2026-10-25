import { vi } from 'vitest';
import type { LoadingDocument, PdfDocument } from './pdfEngine';

/**
 * Подменный движок для тестов просмотрщика (issue #1490): документ из N листов A4, считающий
 * уничтожения. Настоящий pdf.js проверяет `pdfEngine.test.ts` в Node; здесь — поведение интерфейса.
 */

export interface FakeDocument extends PdfDocument {
  destroyed: number;
}

export function fakeDocument(numPages: number): FakeDocument {
  const done = () => ({ promise: Promise.resolve(), cancel: () => {} });
  const doc: FakeDocument = {
    numPages,
    destroyed: 0,
    pageSizes: () => Promise.resolve(Array.from({ length: numPages }, () => ({ width: 595, height: 842 }))),
    render: vi.fn(done),
    textLayer: vi.fn(done),
    text: () => Promise.resolve(''),
    release: () => {},
    destroy: () => { doc.destroyed++; return Promise.resolve(); },
  };
  return doc;
}

/** Открытие, которое отвечает документом или отказом; отменённое — не отвечает вовсе. */
export function opening(result: PdfDocument | Error): LoadingDocument {
  return {
    promise: result instanceof Error ? Promise.reject(result) : Promise.resolve(result),
    cancel: () => {},
  };
}

/** Браузер, которого хватает просмотрщику (D5): jsdom сам `Worker` и `URL.parse` не даёт. */
export function supportedBrowser(): () => void {
  const g = globalThis as Record<string, unknown>;
  const url = URL as unknown as Record<string, unknown>;
  const had = { worker: g.Worker, parse: url.parse };
  g.Worker = class {};
  if (typeof url.parse !== 'function') url.parse = (s: string) => { try { return new URL(s); } catch { return null; } };
  return () => { g.Worker = had.worker; url.parse = had.parse; };
}
