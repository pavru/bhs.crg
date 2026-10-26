import './pdfPolyfills';
import {
  getDocument, InvalidPDFException, PasswordException, PDFWorker, PixelsPerInch, TextLayer, version,
} from 'pdfjs-dist';
import type { PDFDocumentLoadingTask, PDFDocumentProxy, PDFPageProxy, PageViewport } from 'pdfjs-dist';
import PdfWorker from './pdfWorker?worker';

/**
 * Движок просмотрщика PDF — единственный файл главного потока, которому можно импортировать
 * `pdfjs-dist` (`no-restricted-imports`, issue #1490). Остальной просмотрщик знает только типы
 * отсюда; в тестах компонента этот модуль подменяется целиком.
 *
 * - Байты, а не адрес: под `connect-src 'self'` поставки pdf.js не прочёл бы `blob:`.
 * - Worker один на приложение, создаётся при первом открытии и не гасится; гасятся документы.
 *   Запасного разбора в главном потоке нет: чужой PDF не разбирается рядом с интерфейсом.
 * - **Документ открывается на своём `PDFWorker`**, а не через `GlobalWorkerOptions.workerPort`
 *   (issue #1493): с общим портом pdf.js считает worker собственностью документа — уничтожение
 *   документа помечает его «уничтожается» и гасит, и открытие, начатое в этот миг, отказывает
 *   («PDFWorker.create - the worker is being destroyed»). Ровно так встают рядом прежний и новый
 *   документ: смена ключа, погашенная подмена и следующая за ней загрузка.
 * - Аннотации рисуются в холст; слой форм, XFA и скрипты PDF не подключаются вовсе.
 */

export type FailureKind = 'not-pdf' | 'password' | 'no-pages' | 'worker' | 'broken';

/** Отказ движка, переведённый в наш вид: место показывает по нему слова и выход. */
export class ViewerFailure extends Error {
  readonly kind: FailureKind;
  constructor(kind: FailureKind, cause?: unknown) {
    super(`Просмотрщик: ${kind}`, { cause });
    this.name = 'ViewerFailure';
    this.kind = kind;
  }
}

/** Размер листа в CSS-пикселях при масштабе 100 %, с собственным поворотом страницы. */
export interface PageSize { width: number; height: number }

/** Идущая работа над листом: отрисовка или слой текста. */
export interface PageTask { promise: Promise<void>; cancel(): void }

export interface PdfDocument {
  readonly numPages: number;
  /** Размеры всех листов — по одному, в фоне: листы бывают разные, А3 рядом с А4. */
  pageSizes(): Promise<PageSize[]>;
  /** Рисует лист в холст. `pixelRatio` — плотность холста (DPR, уже срезанный потолком пикселей). */
  render(pageNumber: number, canvas: HTMLCanvasElement, view: PageView & { pixelRatio: number }): PageTask;
  /** Слой текста листа в `container`; масштаб и поворот — те же, что у холста. */
  textLayer(pageNumber: number, container: HTMLElement, view: PageView): PageTask;
  /** Текст листа одной строкой — для проверок, интерфейсу он не нужен. */
  text(pageNumber: number): Promise<string>;
  /** Освобождает ресурсы листа, ушедшего из видимой области. */
  release(pageNumber: number): void;
  destroy(): Promise<void>;
}

/** Масштаб (1 = 100 %) и поворот показа (0/90/180/270). */
export interface PageView { scale: number; rotation: number }

export interface LoadingDocument { promise: Promise<PdfDocument>; cancel(): void }

let workerFailure: ((e: unknown) => void)[] = [];
let workerBroken: unknown = null;
/** Worker pdf.js на всё приложение; документ, которому его передали, его не уничтожает. */
let pdfWorker: PDFWorker | undefined;

/** Поднимает worker, если его ещё нет. В Node (тест движка) pdf.js разбирает сам, в том же потоке. */
function ensureWorker(): void {
  if (pdfWorker || typeof Worker === 'undefined') return;
  try {
    const worker = new PdfWorker();
    // Кусок worker'а не загрузился (выпуск сменил хеши) — pdf.js об этом не узнаёт и ждал бы вечно.
    worker.addEventListener('error', e => {
      workerBroken = e;
      const waiting = workerFailure;
      workerFailure = [];
      waiting.forEach(fail => fail(e));
    });
    // Типы pdf.js объявляют `port` как `null` — уже кода, который принимает `Worker` (так открывает и
    // `GlobalWorkerOptions.workerPort`).
    pdfWorker = new PDFWorker({ port: worker as unknown as null });
  } catch (e) {
    workerBroken = e;
  }
}

let assetBase: string | null = null;

/** Откуда брать файлы рядом с библиотекой. Только для теста движка в Node: там это путь на диске. */
export function setAssetBase(base: string): void { assetBase = base.endsWith('/') ? base : `${base}/`; }

/** `pdfjs/<версия>/` — туда их кладёт плагин сборки (`viteSupport.ts`). */
function assets(): string {
  return assetBase ?? new URL(`${import.meta.env.BASE_URL}pdfjs/${version}/`, document.baseURI).href;
}

function translate(e: unknown): ViewerFailure {
  if (e instanceof ViewerFailure) return e;
  const name = (e as { name?: string } | null)?.name;
  if (e instanceof PasswordException || name === 'PasswordException') return new ViewerFailure('password', e);
  if (e instanceof InvalidPDFException || name === 'InvalidPDFException') return new ViewerFailure('not-pdf', e);
  return new ViewerFailure('broken', e);
}

/**
 * Открывает PDF. Байты копируются на каждое открытие: pdf.js передаёт буфер в worker, и у
 * вызывающего он остался бы пустым — второй показ того же документа (режим поверх экрана) не открылся бы.
 */
export function openPdf(source: Blob | Uint8Array): LoadingDocument {
  let task: PDFDocumentLoadingTask | null = null;
  let cancelled = false;
  const promise = (async () => {
    ensureWorker();
    if (workerBroken) throw new ViewerFailure('worker', workerBroken);
    const data = source instanceof Uint8Array ? source.slice() : new Uint8Array(await source.arrayBuffer());
    if (cancelled) throw new ViewerFailure('broken', 'отменено');
    const base = assets();
    task = getDocument({
      data,
      worker: pdfWorker,
      wasmUrl: `${base}wasm/`,
      iccUrl: `${base}iccs/`,
      cMapUrl: `${base}cmaps/`,
      cMapPacked: true,
      standardFontDataUrl: `${base}standard_fonts/`,
      enableXfa: false,
    });
    let onBroken: (e: unknown) => void = () => {};
    const broken = new Promise<never>((_, reject) => { onBroken = e => reject(new ViewerFailure('worker', e)); });
    workerFailure.push(onBroken);
    let doc: PDFDocumentProxy;
    try {
      doc = await Promise.race([task.promise, broken]);
    } catch (e) {
      void task.destroy();
      throw translate(e);
    } finally {
      workerFailure = workerFailure.filter(f => f !== onBroken);
    }
    if (doc.numPages === 0) {
      void task.destroy();
      throw new ViewerFailure('no-pages');
    }
    return wrap(doc);
  })();
  return {
    promise,
    cancel() {
      cancelled = true;
      void task?.destroy();
    },
  };
}

function viewportOf(page: PDFPageProxy, { scale, rotation }: PageView): PageViewport {
  return page.getViewport({ scale: scale * PixelsPerInch.PDF_TO_CSS_UNITS, rotation: (page.rotate + rotation) % 360 });
}

/**
 * Работа над листом с отменой. Задача pdf.js называет свою отмену (`signal.stop`), КАК ТОЛЬКО создана,
 * а не по окончании: отмена идущей отрисовки должна до неё дойти. Иначе следующая отрисовка в тот же
 * холст (новый масштаб, поворот) отказывает — «Cannot use the same canvas during multiple render()».
 */
interface TaskSignal { cancelled: boolean; stop: () => void }

function pageTask(run: (signal: TaskSignal) => Promise<void>): PageTask {
  const signal: TaskSignal = { cancelled: false, stop: () => {} };
  return { promise: run(signal), cancel() { signal.cancelled = true; signal.stop(); } };
}

/** Обёртка документа pdf.js. Экспортируется для теста движка: отмену в Node без холста иначе не проверить. */
export function wrap(doc: PDFDocumentProxy): PdfDocument {
  let destroyed: Promise<void> | null = null;
  return {
    numPages: doc.numPages,
    async pageSizes() {
      const sizes: PageSize[] = [];
      for (let n = 1; n <= doc.numPages; n++) {
        const vp = viewportOf(await doc.getPage(n), { scale: 1, rotation: 0 });
        sizes.push({ width: vp.width, height: vp.height });
      }
      return sizes;
    },
    render(pageNumber, canvas, view) {
      return pageTask(async signal => {
        const page = await doc.getPage(pageNumber);
        if (signal.cancelled) return;
        const viewport = viewportOf(page, view);
        canvas.width = Math.floor(viewport.width * view.pixelRatio);
        canvas.height = Math.floor(viewport.height * view.pixelRatio);
        const ratio = view.pixelRatio;
        const job = page.render({
          canvas,
          canvasContext: canvas.getContext('2d')!,
          viewport,
          transform: ratio === 1 ? undefined : [ratio, 0, 0, ratio, 0, 0],
        });
        signal.stop = () => job.cancel();
        await job.promise;
        signal.stop = () => {};
      });
    },
    textLayer(pageNumber, container, view) {
      return pageTask(async signal => {
        const page = await doc.getPage(pageNumber);
        if (signal.cancelled) return;
        const viewport = viewportOf(page, view);
        container.replaceChildren();
        container.style.setProperty('--scale-factor', String(viewport.scale));
        const layer = new TextLayer({ textContentSource: page.streamTextContent(), container, viewport });
        // Неотменённый прежний слой дописывал бы строки в контейнер нового — текст листа удваивался.
        signal.stop = () => layer.cancel();
        await layer.render();
        signal.stop = () => {};
      });
    },
    async text(pageNumber) {
      const content = await (await doc.getPage(pageNumber)).getTextContent();
      return content.items.map(item => ('str' in item ? item.str : '')).join(' ');
    },
    release(pageNumber) {
      // После уничтожения освобождать нечего, а getPage ушёл бы в разобранный транспорт и отказал
      // («Transport destroyed») мимо всех обработчиков: React гасит документ раньше его листов.
      if (destroyed || pageNumber < 1 || pageNumber > doc.numPages) return;
      doc.getPage(pageNumber).then(page => page.cleanup(), () => { /* документ ушёл — ресурсы ушли с ним */ });
    },
    destroy() {
      destroyed ??= doc.loadingTask.destroy();
      return destroyed;
    },
  };
}
