/**
 * «Открыть во вкладке» — одна функция на все места просмотрщика (issue #1490, D9 зонтика, [UI-20]).
 * Во вкладке браузера у файла полный просмотрщик: поиск, печать с настройками, пароль.
 *
 * - **Окно — синхронно**, в самом обработчике нажатия: открытое после `await` блокировщик всплывающих
 *   окон режет. Байты, которые ещё грузятся, приходят в уже открытую вкладку.
 * - **Тип ставим сами**: `application/pdf` либо тип картинки из белого списка (`IMAGE_TYPES`). Типу
 *   ответа сервера не доверяем — `text/html` во вкладке исполнился бы как страница нашего адреса.
 * - **Ссылка отзывается с отсрочкой**: отозванная сразу не успела бы открыться.
 * - **Вкладка говорит словами** (issue #1493): пока байты грузятся — «Загрузка файла…», отказ
 *   загрузчика — что файл не открыт и что делать. Пустая белая вкладка не значила бы ничего.
 */

export type PdfBytes = Blob | Uint8Array;

/** Сколько живёт ссылка на файл во вкладке или в загрузке: браузеру она нужна, пока он читает файл. */
const REVOKE_AFTER_MS = 60_000;

/** Отозвать объектную ссылку с отсрочкой — отозванная сразу не успела бы открыться или скачаться. */
export function revokeLater(url: string): void {
  setTimeout(() => URL.revokeObjectURL(url), REVOKE_AFTER_MS);
}

/**
 * Картинки, которые просмотрщик показывает сам ([UI-24]) — свой белый список, не тип из ответа:
 * векторная картинка (`image/svg+xml`) — документ со сценариями, её здесь нет и не будет.
 */
export const IMAGE_TYPES: readonly string[] = ['image/png', 'image/jpeg', 'image/gif', 'image/webp', 'image/bmp'];

/** Тип картинки из белого списка, если байты — такая картинка; иначе `null` (показ как PDF). */
export function imageTypeOf(source: PdfBytes | null): string | null {
  const type = source instanceof Blob ? source.type.split(';')[0].trim().toLowerCase() : '';
  return IMAGE_TYPES.includes(type) ? type : null;
}

/** Байты со СВОИМ типом: картинка из белого списка — своим, всё прочее — `application/pdf`. */
export function viewerBlob(source: PdfBytes): Blob {
  return new Blob([source as BlobPart], { type: imageTypeOf(source) ?? 'application/pdf' });
}

const LOADING_TEXT = 'Загрузка файла…';
const FAILURE_TEXT = 'Не удалось открыть файл. Обновите страницу и попробуйте снова.';

/**
 * Строка во вкладке, пока она пустая (`about:blank` — наш источник). Не вышло — `false`. Темы
 * приложения во вкладке нет, поэтому цвет — системный: `color-scheme` следует теме устройства.
 */
function say(tab: Window, text: string): boolean {
  try {
    const doc = tab.document;
    doc.open();
    doc.write('<!doctype html><meta charset="utf-8"><meta name="color-scheme" content="light dark">'
      + `<p style="font:14px sans-serif;padding:16px">${text}</p>`);
    doc.close();
    return true;
  } catch {
    return false;
  }
}

/**
 * Открывает файл во вкладке. С загрузчиком вкладка открыта сразу, говорит «Загрузка файла…» и получает
 * файл по готовности. Отказ загрузчика вкладка называет словами (не смогла — закрывается), и отказ
 * возвращается вызывающему: ему решать, говорить ли что-то ещё.
 */
export function openInNewTab(source: PdfBytes | (() => Promise<PdfBytes>)): Promise<void> {
  const tab = window.open('', '_blank');
  if (tab) tab.opener = null;
  const show = (bytes: PdfBytes) => {
    const url = URL.createObjectURL(viewerBlob(bytes));
    if (tab && !tab.closed) tab.location.href = url;
    else window.open(url, '_blank', 'noopener');
    revokeLater(url);
  };
  if (typeof source !== 'function') {
    show(source);
    return Promise.resolve();
  }
  if (tab) say(tab, LOADING_TEXT);
  return source().then(show, (e: unknown) => {
    if (tab && !tab.closed && !say(tab, FAILURE_TEXT)) tab.close();
    throw e;
  });
}
