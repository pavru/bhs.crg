// Типов Node у приложения нет нарочно (tsconfig.app), а ссылка на них отсюда подмешала бы их всему
// приложению. Этому тесту Node нужен — он читает эталонный PDF с диска, — поэтому без типов, здесь.
// @ts-expect-error — модуль Node без типов
import { readFileSync } from 'node:fs';
// @ts-expect-error — модуль Node без типов
import path from 'node:path';
// @ts-expect-error — модуль Node без типов
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { openPdf, setAssetBase, ViewerFailure } from './pdfEngine';

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

  it('уничтожение повторно безопасно', async () => {
    const doc = await openPdf(reference()).promise;
    await doc.destroy();
    await expect(doc.destroy()).resolves.toBeUndefined();
  });
});
