/**
 * «Открыть во вкладке» — одна функция на все места просмотрщика (issue #1490, D9 зонтика, [UI-20]).
 * Во вкладке браузера у файла полный просмотрщик: поиск, печать с настройками, пароль.
 *
 * - **Окно — синхронно**, в самом обработчике нажатия: открытое после `await` блокировщик всплывающих
 *   окон режет. Байты, которые ещё грузятся, приходят в уже открытую вкладку.
 * - **Тип ставим сами**, `application/pdf`: типу ответа сервера не доверяем — `text/html` во вкладке
 *   исполнился бы как страница нашего адреса.
 * - **Ссылка отзывается с отсрочкой**: отозванная сразу не успела бы открыться.
 */

export type PdfBytes = Blob | Uint8Array;

/** Сколько живёт ссылка на файл во вкладке. Открытой вкладке она уже не нужна. */
const REVOKE_AFTER_MS = 60_000;

export function pdfBlob(source: PdfBytes): Blob {
  return new Blob([source as BlobPart], { type: 'application/pdf' });
}

/**
 * Открывает файл во вкладке. С загрузчиком вкладка открыта сразу и получает файл по готовности;
 * отказ загрузчика закрывает её и возвращается вызывающему — он и скажет человеку, что не так.
 */
export function openInNewTab(source: PdfBytes | (() => Promise<PdfBytes>)): Promise<void> {
  const tab = window.open('', '_blank');
  if (tab) tab.opener = null;
  const show = (bytes: PdfBytes) => {
    const url = URL.createObjectURL(pdfBlob(bytes));
    if (tab && !tab.closed) tab.location.href = url;
    else window.open(url, '_blank', 'noopener');
    setTimeout(() => URL.revokeObjectURL(url), REVOKE_AFTER_MS);
  };
  if (typeof source !== 'function') {
    show(source);
    return Promise.resolve();
  }
  return source().then(show, (e: unknown) => {
    tab?.close();
    throw e;
  });
}
