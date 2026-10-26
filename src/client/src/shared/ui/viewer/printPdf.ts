import { openInNewTab, viewerBlob, type PdfBytes } from './openInNewTab';

/**
 * Ctrl+P в просмотрщике печатает сам файл, а не страницу приложения (issue #1490, [UI-13]): так
 * печатал встроенный просмотрщик браузера во фрейме, и привычку ломать нельзя.
 *
 * Печатает скрытый фрейм с `blob:` и `application/pdf` — он остаётся единственным фреймом
 * просмотрщика; CSP поставки (`frame-src blob:`) пускает его и только его ([UI-26]). Где у браузера
 * нет встроенного показа PDF (`navigator.pdfViewerEnabled === false`, телефоны), напечатать фрейм нечем — тогда файл
 * открывается во вкладке, а печатает уже она. Картинку ([UI-24]) печатает тот же фрейм — и так же
 * уходит во вкладку на телефоне: там `print()` фрейма печатает страницу приложения или пустой лист и
 * при этом не бросает, так что запасной путь в `catch` не сработал бы.
 */

let current: { frame: HTMLIFrameElement; url: string } | null = null;

function cleanup(): void {
  if (!current) return;
  current.frame.remove();
  URL.revokeObjectURL(current.url);
  current = null;
}

export function canPrintInline(): boolean {
  return (navigator as Navigator & { pdfViewerEnabled?: boolean }).pdfViewerEnabled !== false;
}

export function printPdf(source: PdfBytes): void {
  if (!canPrintInline()) {
    openInNewTab(source);
    return;
  }
  cleanup();
  const url = URL.createObjectURL(viewerBlob(source));
  const frame = document.createElement('iframe');
  frame.setAttribute('aria-hidden', 'true');
  frame.tabIndex = -1;
  // Не display:none и не visibility:hidden — такой фрейм часть браузеров не печатает.
  frame.style.cssText = 'position:fixed;right:0;bottom:0;width:0;height:0;border:0;opacity:0;pointer-events:none';
  frame.onload = () => {
    try {
      frame.contentWindow?.focus();
      frame.contentWindow?.print();
    } catch {
      cleanup();
      openInNewTab(source);
    }
  };
  frame.src = url;
  document.body.appendChild(frame);
  current = { frame, url };
  // Фрейм убирается следующей печатью, а не по таймеру: окно печати держит фрейм, пока открыто, и
  // убранный раньше фрейм оборвал бы печать. Один невидимый фрейм на странице ничему не мешает.
}
