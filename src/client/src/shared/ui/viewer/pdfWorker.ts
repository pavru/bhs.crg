/**
 * Вход worker'а pdf.js (issue #1490). Свой, а не `pdf.worker.mjs` напрямую, ради порядка: заплатки
 * ставятся до библиотеки, а глобалов главного потока worker не видит (D5 зонтика viewer).
 *
 * Собирается через `?worker` в `.js` — `.mjs` nginx поставки отдал бы как octet-stream, и worker не
 * запустился бы. Запускает его `pdfEngine.ts`; сам он поднимается, увидев, что исполняется в worker'е.
 */
import './pdfPolyfills';
import 'pdfjs-dist/build/pdf.worker.mjs';
