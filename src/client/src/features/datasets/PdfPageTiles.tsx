import { useEffect, useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { CircularProgress } from '@/shared/ui/CircularProgress';
import { Check, ErrorIcon, Warning, ZoomIn } from '@/shared/ui/icons';
import { loadPageImageUrl, loadPageThumbnailUrl } from '@/shared/api/datasets';
import { Modal } from '@/shared/ui/Modal';
import { IconButton } from '@/shared/ui/Button';
import { Tooltip } from '@/shared/ui/Tooltip';

/**
 * Листы PDF в редакторе разбиения: миниатюра, плитка с выбором и крупный просмотр. Вынесены из
 * `PdfGroupingEditor` — тот у порога размера, а листы от групп и сохранения не зависят.
 */

// ─── Миниатюра страницы (ленивая загрузка, без OCR — только чтобы узнать документ глазами) ────

function PageThumbnail({ fileId, pageIndex, selected = false }: { fileId: string; pageIndex: number; selected?: boolean }) {
  const [url, setUrl] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let cancelled = false;
    let objectUrl: string | null = null;
    loadPageThumbnailUrl(fileId, pageIndex)
      .then(u => { if (!cancelled) { objectUrl = u; setUrl(u); } })
      .catch(() => { if (!cancelled) setFailed(true); });
    return () => {
      cancelled = true;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [fileId, pageIndex]);

  return (
    <div className={`w-full aspect-[210/297] rounded-md bg-surface-container-highest flex items-center justify-center overflow-hidden ${
      selected ? 'outline-2 outline-primary -outline-offset-2' : ''}`}>
      {url ? (
        <img src={url} alt={`Страница ${pageIndex + 1}`} className="w-full h-full object-contain" />
      ) : failed ? (
        <span className="text-label-small text-on-surface-variant px-1 text-center">нет превью</span>
      ) : (
        <CircularProgress size={18} className="text-on-surface-variant" />
      )}
    </div>
  );
}

// ─── Одна миниатюра-страница со статусом выделения/подозрительности ────────────────────────────
// Внешний контейнер — div (не button): внутри лежит кнопка «просмотреть лист», а button-в-button
// — невалидный HTML. Клик по плитке переключает выделение, клик по лупе (stopPropagation) — открывает
// крупный просмотр листа.

export function PageTile({
  fileId, pageIndex, selected, suspicious, noAnswer, onToggle, onView,
}: {
  fileId: string; pageIndex: number; selected: boolean; suspicious: boolean;
  /** Движок не ответил по этому листу — признак ЛИСТА, а не группы (issue #803). */
  noAnswer: boolean;
  onToggle: (pageIndex: number, e: React.MouseEvent) => void;
  onView: (pageIndex: number) => void;
}) {
  return (
    <div onClick={e => onToggle(pageIndex, e)}
      className="group/tile relative p-1 text-left cursor-pointer">
      {/* Подсказка листа — на миниатюре с подписью, а лупа ей соседка, не потомок: у лупы своя
          подсказка, и под курсором над ней должна открываться одна, а не обе. */}
      <Tooltip content={noAnswer
          ? `Страница ${pageIndex + 1} — модель не ответила по этому листу, поля пусты`
          : `Страница ${pageIndex + 1}`}>
        <div>
          <PageThumbnail fileId={fileId} pageIndex={pageIndex} selected={selected} />
          <div className="flex items-center justify-between mt-1 px-0.5">
            <span className="text-label-small text-on-surface-variant">{pageIndex + 1}</span>
            {/* Лист без ответа виден отдельно от «подозрительной» группы: у первого поля пусты потому,
                что их неоткуда взять, у второй — потому что распозналось не то. Лечится это по-разному. */}
            {noAnswer ? <Icon icon={ErrorIcon} size={18} className="text-error" />
              : suspicious && <Icon icon={Warning} size={18} className="text-warning" />}
          </div>
        </div>
      </Tooltip>
      {/* Выбор — галкой в круге поверх миниатюры и обводкой (Р5), а не заливкой плитки. */}
      {selected && (
        <span aria-hidden className="absolute top-2.5 left-2.5 flex items-center justify-center w-6 h-6 rounded-full bg-primary text-on-primary">
          <Icon icon={Check} size={18} />
        </span>
      )}
      <IconButton label="Просмотреть лист крупно"
        className="absolute top-1.5 right-1.5 bg-surface/90 border border-outline-variant opacity-70 hover:opacity-100"
        onClick={e => { e.stopPropagation(); onView(pageIndex); }} icon={ZoomIn} />
    </div>
  );
}

// ─── Крупный просмотр одного листа (высокое DPI, клик — вписать ↔ 100%) ──────────────────────────

export function PageViewer({ fileId, pageIndex, onClose }: { fileId: string; pageIndex: number; onClose: () => void }) {
  const [url, setUrl] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);
  const [zoomed, setZoomed] = useState(false);

  // Ни одного «сбросить перед загрузкой»: просмотрщик заводится заново на каждый лист (ключ по
  // номеру листа на месте вызова, issue #858), поэтому «ещё грузится» — это начальное состояние
  // свежего компонента, а не то, в которое его надо возвращать эффектом.
  useEffect(() => {
    let cancelled = false;
    let objectUrl: string | null = null;
    loadPageImageUrl(fileId, pageIndex)
      .then(u => { if (!cancelled) { objectUrl = u; setUrl(u); } })
      .catch(() => { if (!cancelled) setFailed(true); });
    return () => { cancelled = true; if (objectUrl) URL.revokeObjectURL(objectUrl); };
  }, [fileId, pageIndex]);

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title={`Лист ${pageIndex + 1}`} wide nothingToLose>
      {failed ? (
        <p className="text-body-medium text-error py-10 text-center">Не удалось загрузить изображение листа.</p>
      ) : !url ? (
        <div className="flex items-center justify-center py-16 text-on-surface-variant"><CircularProgress size={20} /></div>
      ) : (
        <>
          {/* Центрируем img через mx-auto (НЕ flex justify-center): при зуме, когда картинка шире/выше
              контейнера, auto-поля схлопываются в 0 → доступен скролл ко ВСЕМ краям (иначе левый/верхний
              край центрированного флекс-элемента недостижим и лист «обрезается»). */}
          <div className="overflow-auto max-h-[78vh] rounded-md bg-surface-container-highest">
            <img src={url} alt={`Лист ${pageIndex + 1}`}
              onClick={() => setZoomed(z => !z)}
              className={`block mx-auto ${zoomed ? 'max-w-none cursor-zoom-out' : 'max-w-full max-h-[78vh] object-contain cursor-zoom-in'}`} />
          </div>
          <p className="text-body-small text-on-surface-variant mt-2 text-center">Клик по изображению — переключить масштаб (вписать ↔ 100%).</p>
        </>
      )}
    </Modal>
  );
}
