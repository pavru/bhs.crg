// Типов Node у приложения нет нарочно (tsconfig.app), а ссылка на них отсюда подмешала бы их всему
// приложению. Этому тесту Node нужен — он читает эталонный PDF с диска, — поэтому без типов, здесь.
// @ts-expect-error — модуль Node без типов
import { readFileSync } from 'node:fs';
// @ts-expect-error — модуль Node без типов
import path from 'node:path';
// @ts-expect-error — модуль Node без типов
import { fileURLToPath } from 'node:url';
import { describe, expect, it, vi } from 'vitest';
import { openPdf, setAssetBase, ViewerFailure, wrap } from './pdfEngine';

/**
 * Настоящий движок в Node, без подмен (issue #1490, [UI-10], [UI-17], [UI-21]).
 *
 * Это и сторож заплаток: в Node 22 из CI нет функций, которые вызывает сборка pdf.js 6
 * (`Map.getOrInsertComputed`, `Math.sumPrecise`…), а по набору функций Node 22 не новее Chrome 126 —
 * границы просмотрщика. Обновлённый pdf.js, вызвавший новую функцию без заплатки, краснит этот тест.
 *
 * Эталон — PDF, который конвертер поставки делает из `converter-reference.xlsx` (сверяет пару
 * `deploy/converter.tests.sh`): текст со шрифтом LibreOffice, кириллица и числа по-русски.
 */

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '../../../../../..');
setAssetBase(`${path.resolve(here, '../../../../node_modules/pdfjs-dist').replace(/\\/g, '/')}/`);

const reference = () => new Uint8Array(readFileSync(path.join(root, 'deploy/converter-reference.pdf')));

async function failureOf(bytes: Uint8Array): Promise<string> {
  try {
    const doc = await openPdf(bytes).promise;
    await doc.destroy();
    return 'открылся';
  } catch (e) {
    return e instanceof ViewerFailure ? e.kind : `чужая ошибка: ${String(e)}`;
  }
}

describe('движок просмотрщика на эталонном PDF', () => {
  it('число листов и кириллица в тексте', async () => {
    const doc = await openPdf(reference()).promise;
    try {
      expect(doc.numPages).toBe(1);
      const text = (await doc.text(1)).replace(/[\u00a0\u202f]/g, ' ');
      // Порядок фрагментов у pdf.js свой (номер стоит отдельной ячейкой), поэтому — по частям.
      for (const part of ['Счёт на оплату', '№ 17', 'ООО «Эталон-Снаб», ИНН 7700000017', '3 834,16']) {
        expect(text).toContain(part);
      }
    } finally {
      await doc.destroy();
    }
  });

  it('размер листа известен до отрисовки — в CSS-пикселях', async () => {
    const doc = await openPdf(reference()).promise;
    try {
      const [size] = await doc.pageSizes();
      // A4 — 595×842 пунктов, в CSS-пикселях ×96/72; лист может быть и альбомным.
      expect([Math.round(Math.min(size.width, size.height)), Math.round(Math.max(size.width, size.height))])
        .toEqual([794, 1123]);
    } finally {
      await doc.destroy();
    }
  });

  it('байты вызывающего остаются целыми: второе открытие тех же байтов работает', async () => {
    const bytes = reference();
    const first = await openPdf(bytes).promise;
    await first.destroy();
    expect(bytes.byteLength).toBeGreaterThan(0);
    const second = await openPdf(bytes).promise;
    expect(second.numPages).toBe(1);
    await second.destroy();
  });

  it('Blob читается так же', async () => {
    const doc = await openPdf(new Blob([reference()], { type: 'application/pdf' })).promise;
    expect(doc.numPages).toBe(1);
    await doc.destroy();
  });

  it('поддельный PDF — «не PDF», а не чужая ошибка', async () => {
    expect(await failureOf(new TextEncoder().encode('%PDF-1.4 не настоящий'))).toBe('not-pdf');
  });

  it('освобождение листа после уничтожения документа — без необработанного отказа', async () => {
    // React гасит документ раньше его листов: release приходит в уже разобранный транспорт.
    const unhandled: unknown[] = [];
    const onUnhandled = (e: unknown) => { unhandled.push(e); };
    const proc = (globalThis as unknown as { process: { on(e: string, f: (e: unknown) => void): void; off(e: string, f: (e: unknown) => void): void } }).process;
    proc.on('unhandledRejection', onUnhandled);
    try {
      const doc = await openPdf(reference()).promise;
      await doc.pageSizes();
      const gone = doc.destroy();
      doc.release(1);
      await gone;
      doc.release(1);
      await new Promise(resolve => setTimeout(resolve, 50));
      expect(unhandled).toEqual([]);
    } finally {
      proc.off('unhandledRejection', onUnhandled);
    }
  });

  it('уничтожение повторно безопасно', async () => {
    const doc = await openPdf(reference()).promise;
    await doc.destroy();
    await expect(doc.destroy()).resolves.toBeUndefined();
  });
});

describe('отмена работы над листом', () => {
  /** Документ pdf.js, чья отрисовка не кончается, пока её не отменят. В Node холста нет — он не нужен. */
  function endlessRender() {
    const job = { cancel: vi.fn(), promise: new Promise<void>(() => {}) };
    const page = {
      rotate: 0,
      getViewport: () => ({ width: 10, height: 10, scale: 1 }),
      render: vi.fn(() => job),
    };
    const proxy = { numPages: 1, getPage: () => Promise.resolve(page), loadingTask: { destroy: () => Promise.resolve() } };
    const canvas = { width: 0, height: 0, getContext: () => ({}) } as unknown as HTMLCanvasElement;
    return { doc: wrap(proxy as unknown as Parameters<typeof wrap>[0]), job, page, canvas };
  }

  it('отмена идущей отрисовки доходит до pdf.js — иначе следующая в тот же холст отказала бы', async () => {
    const { doc, job, page, canvas } = endlessRender();
    const task = doc.render(1, canvas, { scale: 1, rotation: 0, pixelRatio: 1 });
    await vi.waitFor(() => expect(page.render).toHaveBeenCalled());
    task.cancel();
    expect(job.cancel).toHaveBeenCalledOnce();
  });

  it('отмена до получения листа — отрисовка не начинается', async () => {
    const { doc, page, canvas } = endlessRender();
    const task = doc.render(1, canvas, { scale: 1, rotation: 0, pixelRatio: 1 });
    task.cancel();
    await task.promise;
    expect(page.render).not.toHaveBeenCalled();
  });
});
