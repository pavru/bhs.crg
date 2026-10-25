/**
 * Ворота просмотрщика PDF без интерфейса (issue #1490): ленивая загрузка ядра и проверка браузера.
 * Отдельно от `PdfViewer.tsx`, чтобы тот экспортировал одни компоненты (быстрая перезагрузка Vite).
 */

export const loadViewer = () => import('./PdfViewerImpl');

/** Начать грузить ядро заранее — например, когда место уже знает, что покажет PDF. */
export function preloadPdfViewer(): void {
  void loadViewer().catch(() => { /* отказ покажет сам просмотрщик */ });
}

/**
 * Хватает ли браузера (D5 зонтика viewer). Граница — Chrome/Firefox 126, Safari 18: ниже нет
 * `URL.parse` и `Promise.withResolvers`, без которых pdf.js 6 не обходится и заплатки не ставятся.
 * Остальное новое, что он зовёт, закрывают заплатки (`pdfPolyfills.ts`).
 */
export function viewerSupported(): boolean {
  return typeof (Promise as unknown as { withResolvers?: unknown }).withResolvers === 'function'
    && typeof (URL as unknown as { parse?: unknown }).parse === 'function'
    && typeof Worker !== 'undefined';
}
