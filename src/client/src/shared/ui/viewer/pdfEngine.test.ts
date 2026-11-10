// Первым: делает в среде границу браузеров — до pdfEngine, который ставит заплатки (см. ниже).
import { KEPT_AT_BORDER, REMOVED_AT_BORDER, REMOVED_KEYS, UNKNOWN_AT_BORDER } from '@/shared/testing/browserBorder';
import { resolveBorderPath } from '@/shared/testing/browserBorderPaths';
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
import { IMPLS, ITERATOR_HELPER_NAMES, ITERATOR_PROTOTYPE } from './pdfPolyfills';

/**
 * Настоящий движок в Node, без подмен (issue #1490, [UI-10], [UI-17], [UI-21]).
 *
 * Это и сторож заплаток: до загрузки библиотеки `browserBorder` убирает из среды функции, которых нет
 * хотя бы у одного браузера границы (Chrome 126, Firefox 126, Safari 18) — `Map.getOrInsertComputed`,
 * глобал `Iterator`, `Response.bytes`… — на любой версии Node. Обновлённый pdf.js, вызвавший новую
 * функцию без заплатки, краснит этот тест: загрузкой и открытием документа — или, если путь в Node не
 * проходится (worker качает файлы, пользователь выделяет текст), сверкой вызовов по имени.
 *
 * Эталон — PDF, который конвертер поставки делает из `converter-reference.xlsx` (сверяет пару
 * `deploy/converter.tests.sh`): текст со шрифтом LibreOffice, кириллица и числа по-русски.
 */

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '../../../../../..');
setAssetBase(`${path.resolve(here, '../../../../node_modules/pdfjs-dist').replace(/\\/g, '/')}/`);

const reference = () => new Uint8Array(readFileSync(path.join(root, 'deploy/converter-reference.pdf')));
/**
 * Обращения, заведомо безвредные: ключ убран границей, а сборка трогает одноимённый у другого
 * объекта — или проверяет возможность и без неё идёт запасным путём. Каждое — с причиной, сверенной
 * по тексту сборки.
 */
const FALSE_MATCHES: Record<string, string> = {
  'SharedArrayBuffer.prototype.grow': '`.grow(` у pdf.js — WebAssembly.Memory и WebAssembly.Table (wasm-декодеры)',
  'Set.prototype.union': '`t.union = …` — запись таблицы имён глифов, не метод Set',
  'ArrayBuffer.prototype.resizable': '`HEAP8?.buffer?.resizable` — проверка возможности в коде Emscripten (wasm-декодеры): без неё виды кучи пересоздаются',
};

/**
 * Ключи без данных MDN, к которым сборка обращается, — по имени: почему это не пробел. Ключ без
 * записи в MDN остаётся в среде теста, и обращение к нему иначе прикрывало бы пропуск заплатки.
 */
const UNKNOWN_USED: Record<string, string> = {
  message: 'свойство ошибок — у всех браузеров; MDN не ведёт его отдельной записью у каждого вида ошибки',
  BYTES_PER_ELEMENT: 'у типизированных массивов с ES2015 — у всех браузеров; MDN ведёт его одной записью у TypedArray',
  WebAssembly: 'у всех браузеров границы; MDN ведёт его разделом webassembly, а не javascript.builtins',
  process: 'только под проверкой isNodeJS (`typeof process === "object"`) — в браузере путь не идёт',
  Buffer: 'только под `isNodeJS && typeof Buffer !== "undefined"` — в браузере путь не идёт',
};

const builds = ['pdf.mjs', 'pdf.worker.mjs'].map(file => ({
  file, text: readFileSync(path.resolve(here, '../../../../node_modules/pdfjs-dist/build', file), 'utf8') as string,
}));

const escape = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
/** Имя, за которым не идёт продолжение идентификатора: `.grow` не находит `.growable`. */
const word = (name: string) => `${escape(name)}${/[\w$]$/.test(name) ? '(?![\\w$])' : ''}`;
const TYPED_ARRAYS = Object.getOwnPropertyNames(globalThis).filter(n => /^(Big)?(Uint|Int|Float)\d+(Clamped)?Array$/.test(n));

/**
 * Обращается ли сборка к ключу — вызовом или чтением (свойство-получатель без скобок тоже
 * обращение). Ключ прототипа ищется как `.имя`, статический — как `Объект.имя`, статический
 * `%TypedArray%` — через любой типизированный массив (`Uint8Array.from`).
 */
function mentioned(path: string, name: string): boolean {
  const owners = path.endsWith('.prototype') || path === '%IteratorPrototype%' ? ['']
    : path === '%TypedArray%' ? TYPED_ARRAYS : [path];
  const re = new RegExp(`(?:${owners.map(escape).join('|')})\\.${word(name)}`);
  return builds.some(b => re.test(b.text));
}

/** Упоминается ли имя где угодно: `.имя`, а у глобала — само имя. Шире `mentioned` нарочно. */
function nameUsed(path: string, name: string): boolean {
  const re = new RegExp(path === 'globalThis' ? `(?<![\\w$.])${word(name)}` : `\\.${word(name)}`);
  return builds.some(b => re.test(b.text));
}

const split = (key: string) => [key.slice(0, key.lastIndexOf('.')), key.slice(key.lastIndexOf('.') + 1)] as const;

async function failureOf(bytes: Uint8Array): Promise<string> {
  try {
    const doc = await openPdf(bytes).promise;
    await doc.destroy();
    return 'открылся';
  } catch (e) {
    return e instanceof ViewerFailure ? e.kind : `чужая ошибка: ${String(e)}`;
  }
}

describe('граница браузеров в среде теста', () => {
  it('на местах функций из заплаток — заплатки, а не встроенные функции среды', () => {
    // На Node, где функции свои, это верно только при сработавшей границе: иначе сторож молча зелёный.
    const proto = Uint8Array.prototype as unknown as Record<string, unknown>;
    const places: [string, unknown, unknown][] = [
      ['Map.prototype.getOrInsert', (Map.prototype as unknown as Record<string, unknown>).getOrInsert, IMPLS.getOrInsert],
      ['Map.prototype.getOrInsertComputed', (Map.prototype as unknown as Record<string, unknown>).getOrInsertComputed, IMPLS.getOrInsertComputed],
      ['WeakMap.prototype.getOrInsertComputed', (WeakMap.prototype as unknown as Record<string, unknown>).getOrInsertComputed, IMPLS.getOrInsertComputed],
      ['Math.sumPrecise', (Math as unknown as Record<string, unknown>).sumPrecise, IMPLS.sumPrecise],
      ['Promise.try', (Promise as unknown as Record<string, unknown>).try, IMPLS.promiseTry],
      ['Uint8Array.prototype.toBase64', proto.toBase64, IMPLS.toBase64],
      ['Uint8Array.prototype.toHex', proto.toHex, IMPLS.toHex],
      ['Uint8Array.fromBase64', (Uint8Array as unknown as Record<string, unknown>).fromBase64, IMPLS.fromBase64],
      ['globalThis.Iterator', (globalThis as Record<string, unknown>).Iterator, IMPLS.iteratorCtor],
      ...ITERATOR_HELPER_NAMES.map(name => [`%IteratorPrototype%.${name}`, (ITERATOR_PROTOTYPE as Record<string, unknown>)[name], IMPLS[name]] as [string, unknown, unknown]),
      ['Set.prototype.intersection', (Set.prototype as unknown as Record<string, unknown>).intersection, IMPLS.setIntersection],
      ['Response.prototype.bytes', (Response.prototype as unknown as Record<string, unknown>).bytes, IMPLS.bytes],
      ['Blob.prototype.bytes', (Blob.prototype as unknown as Record<string, unknown>).bytes, IMPLS.bytes],
      ['ReadableStream.prototype.values', (ReadableStream.prototype as unknown as Record<string, unknown>).values, IMPLS.streamValues],
      ['ReadableStream.prototype[Symbol.asyncIterator]', (ReadableStream.prototype as unknown as Record<symbol, unknown>)[Symbol.asyncIterator], IMPLS.streamValues],
    ];
    expect(places.filter(([, actual, impl]) => actual !== impl).map(([name]) => name)).toEqual([]);
  });

  it('Node новее границы — её функции убраны и названы', () => {
    // Node один на всё (.nvmrc) и новее снимка с Node 22: убранное обязано быть.
    expect(REMOVED_AT_BORDER).toEqual(expect.arrayContaining([
      'Map.prototype.getOrInsertComputed', 'Uint8Array.prototype.toBase64', 'Promise.try',
      // Пробелы Firefox и Safari и веб-API — их Node 22 имел, а граница нет.
      'globalThis.Iterator', '%IteratorPrototype%.toArray', 'Set.prototype.intersection',
      'Response.prototype.bytes', 'Blob.prototype.bytes', 'ReadableStream.prototype.Symbol.asyncIterator',
    ]));
  });

  it('каждый убранный границей ключ, к которому сборка обращается по имени, после заплаток на месте', () => {
    // Загрузка в Node проходит не все пути: worker берёт файлы с диска, а не `fetch` (`bytes`),
    // выделения текста нет (итераторные помощники слоя текста). Поэтому — по тексту сборки, и вызов,
    // и чтение свойства. Совпадение имён (`.filter` и у массивов) даёт лишний красный, а не пропуск:
    // так и задумано, заплатка дешевле разбора.
    // Глобалы и символы — не здесь: глобал без проверки краснит загрузку, `for await` — `doc.text()`.
    const used = REMOVED_KEYS
      .filter(({ path, name }) => path !== 'globalThis' && !name.startsWith('Symbol.'))
      .filter(({ path, name }) => mentioned(path, name))
      .map(({ path, name }) => `${path}.${name}`);
    const unpatched = used.filter(key => {
      const [owner, name] = split(key);
      return !(key in FALSE_MATCHES) && !(name in (resolveBorderPath(owner) as object));
    });
    expect(unpatched).toEqual([]);
    // Исключение без совпадения устарело: библиотека перестала звать это имя — убрать.
    expect(Object.keys(FALSE_MATCHES).filter(key => !used.includes(key))).toEqual([]);
  });

  it('оставленное ради среды тестов сборка не трогает', () => {
    // Иначе нарочно не убранное прикрывало бы пропуск заплатки.
    expect(Object.keys(KEPT_AT_BORDER).filter(key => nameUsed(...split(key)))).toEqual([]);
  });

  it('оставленное без данных MDN сборка трогает только с названной причиной', () => {
    // Ключ без записи в MDN снимок не убирает: такой, которого у браузера нет, иначе прошёл бы молча.
    const used = UNKNOWN_AT_BORDER.filter(key => nameUsed(...split(key)));
    expect(used.filter(key => !(split(key)[1] in UNKNOWN_USED))).toEqual([]);
    const usedNames = new Set(used.map(key => split(key)[1]));
    expect(Object.keys(UNKNOWN_USED).filter(name => !usedNames.has(name))).toEqual([]);
  });
});

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

  it('кусок листа: холст размера области, лист сдвинут на её начало, отмена доходит до pdf.js', async () => {
    const { doc, job, page, canvas } = endlessRender();
    const task = doc.renderArea(1, canvas, { scale: 2, rotation: 0, pixelRatio: 2, area: { x: 30, y: 40, width: 96, height: 48 } });
    await vi.waitFor(() => expect(page.render).toHaveBeenCalled());
    expect([canvas.width, canvas.height]).toEqual([192, 96]);
    expect(page.render.mock.calls[0]).toEqual([expect.objectContaining({ transform: [2, 0, 0, 2, -60, -80] })]);
    task.cancel();
    expect(job.cancel).toHaveBeenCalledOnce();
  });

  it.each([0, 90, 180, 270])('кусок повёрнутого на %i° листа — в повороте показа, размер холста от поворота не зависит', async rotation => {
    const { doc, page, canvas } = endlessRender();
    const getViewport = vi.spyOn(page, 'getViewport');
    doc.renderArea(1, canvas, { scale: 1, rotation, pixelRatio: 1, area: { x: 0, y: 5, width: 20, height: 10 } });
    await vi.waitFor(() => expect(page.render).toHaveBeenCalled());
    expect(getViewport).toHaveBeenCalledWith(expect.objectContaining({ rotation }));
    expect([canvas.width, canvas.height]).toEqual([20, 10]);
    expect(page.render.mock.calls[0]).toEqual([expect.objectContaining({ transform: [1, 0, 0, 1, -0, -5] })]);
  });

  it('отмена до получения листа — отрисовка не начинается', async () => {
    const { doc, page, canvas } = endlessRender();
    const task = doc.render(1, canvas, { scale: 1, rotation: 0, pixelRatio: 1 });
    task.cancel();
    await task.promise;
    expect(page.render).not.toHaveBeenCalled();
  });
});
