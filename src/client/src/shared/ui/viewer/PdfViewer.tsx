import { Component, lazy, Suspense, type ReactNode } from 'react';
import { ExternalLink, FileText, X } from 'lucide-react';
import { Modal } from '../Modal';
import { Button, IconButton } from '../Button';
import { ViewerMessage } from './ViewerMessage';
import { openInNewTab, type PdfBytes } from './openInNewTab';
import { loadViewer, viewerSupported } from './viewerGate';

/**
 * Просмотрщик PDF — один на всё приложение (issue #1490, эпик #1489, [UI-10]). Встроенный просмотрщик
 * браузера во фрейме заменён своим на pdf.js: своя панель (страница, масштаб, поворот), слой текста,
 * Ctrl+P печатает файл. Полный функционал браузера — «Открыть во вкладке» у места.
 *
 * Здесь только ворота (`viewerGate.ts`): проверка браузера ДО загрузки ядра (D5 зонтика) и ленивый кусок — pdf.js весит
 * около мегабайта, а нужен он лишь там, где показывают PDF. Ядро — `PdfViewerImpl`.
 */

export interface PdfViewerProps {
  /** Байты документа; `null` — место ещё грузит их, просмотрщик говорит «загружается». */
  source: PdfBytes | null;
  /** Новый ключ — новый документ (показ с начала); тот же ключ с новыми байтами — позиция остаётся. */
  documentKey: string;
  /** Подпись области листов для читалки и e2e: «Вид для чтения: Счёт.xlsx». */
  title: string;
  /** Кнопки места («Оригинал») — выход из отказа, когда файл не откроет и браузер. */
  actions?: ReactNode;
  /** Ключ места, под которым запоминается режим масштаба («По ширине», «Страница целиком», %). */
  rememberZoomAs?: string;
  /** Режим поверх экрана: тот же документ окном во весь экран ([UI-14]). */
  overlay?: { open: boolean; onOpenChange: (open: boolean) => void };
}

const PdfViewerImpl = lazy(loadViewer);

export function PdfViewer(props: PdfViewerProps) {
  const { overlay, ...inline } = props;
  return (
    <>
      <ViewerGate {...inline} />
      {overlay && (
        <Modal open={overlay.open} onOpenChange={overlay.onOpenChange} title={props.title} fullScreen headerless flushBody nothingToLose>
          <div className="h-full flex flex-col min-h-0">
            <div className="shrink-0 h-12 flex items-center gap-2 pl-4 pr-2 border-b border-stroke bg-surface">
              <FileText size={16} className="text-fg3 shrink-0" aria-hidden />
              <span className="flex-1 min-w-0 truncate text-sm font-medium text-fg1">{props.title}</span>
              {props.source && (
                <Button size="sm" variant="outlined" icon={<ExternalLink size={13} />} onClick={() => openInNewTab(props.source!)}>
                  Открыть во вкладке
                </Button>
              )}
              <IconButton label="Закрыть" data-modal-exit onClick={() => overlay.onOpenChange(false)}>
                <X size={18} />
              </IconButton>
            </div>
            <div className="flex-1 min-h-0">
              <ViewerGate {...inline} />
            </div>
          </div>
        </Modal>
      )}
    </>
  );
}

function ViewerGate(props: Omit<PdfViewerProps, 'overlay'>) {
  if (!viewerSupported()) return <ViewerMessage state="old-browser" source={props.source} />;
  return (
    <ChunkBoundary source={props.source}>
      <Suspense fallback={<ViewerMessage state="loading" source={props.source} />}>
        <PdfViewerImpl {...props} />
      </Suspense>
    </ChunkBoundary>
  );
}

/**
 * Ленивый кусок не загрузился — чаще всего после выпуска: прежний хеш куска пропал с сервера. Это
 * говорится прямо, а не «файл повреждён»: файл цел, обновилось приложение.
 */
class ChunkBoundary extends Component<{ source: PdfBytes | null; children: ReactNode }, { failed: boolean }> {
  state = { failed: false };

  static getDerivedStateFromError() {
    return { failed: true };
  }

  render() {
    return this.state.failed ? <ViewerMessage state="chunk" source={this.props.source} /> : this.props.children;
  }
}
