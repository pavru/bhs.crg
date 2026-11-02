import { useRef, useState, type PointerEvent, type ReactNode, type RefObject } from 'react';
import * as Dialog from '@radix-ui/react-dialog';
import { IconButton } from './Button';
import { BOTTOM_SHEET_SURFACE, DIALOG_SCRIM, SHEET_TITLE } from './dialogSurface';
import { useHistoryLayer } from './historyLayers';
import { Close } from './icons';
import { focusFromLayer } from './layerFocus';
import { LayerBoundary } from './LayerBoundary';

/**
 * Лист снизу — bottom sheet MD3 ([UI-77], sharedui-md3-13): у нижнего края во всю ширину, верх 28,
 * `surface-container-low` уровня 1, затемнение. Высота — по содержимому, не выше 90 % экрана: дальше тело
 * прокручивается, ручка и шапка стоят. Закрывается ручкой вниз, щелчком по затемнению, Esc и «Назад».
 *
 * <p>Жест — pointer events без библиотеки, только с ручки и шапки (`touch-action: none`, захват указателя):
 * тело листа прокручивается, и жест в нём спорил бы с прокруткой. Стянули дальше половины высоты — лист
 * закрыт; отпустили раньше или жест отменён (`pointercancel`) — лист возвращается на место, без анимации
 * при `prefers-reduced-motion`. Ручка — кнопка «Закрыть» для читалки и клавиатуры.</p>
 */
export interface SheetBase {
  open: boolean;
  onClose: () => void;
  /** Подпись крестика. */
  closeLabel?: string;
  children: ReactNode;
}

/**
 * Без имени лист не бывает: `title` — заголовок шапки с крестиком, без него имя листа для читалки —
 * `label`, и тогда он обязателен.
 */
export type SheetProps = SheetBase & ({ title: string; label?: string } | { title?: undefined; label: string });

export function BottomSheet({ open, onClose, title, label, closeLabel = 'Закрыть', children }: SheetProps) {
  // Системная «Назад» закрывает лист, а не страницу под ним (issue #1418).
  useHistoryLayer(open, () => { onClose(); return true; });
  return (
    <Dialog.Root open={open} onOpenChange={o => { if (!o) onClose(); }}>
      {/* Лист — новое место: размер поля клетки сквозь портал сюда не идёт (как у `Modal`). */}
      <LayerBoundary>
      <Dialog.Portal>
        <Dialog.Overlay className={DIALOG_SCRIM} />
        <SheetContent title={title} label={label ?? title ?? ''} closeLabel={closeLabel} onClose={onClose}>{children}</SheetContent>
      </Dialog.Portal>
      </LayerBoundary>
    </Dialog.Root>
  );
}

function SheetContent({ title, label, closeLabel, onClose, children }: Omit<SheetBase, 'open' | 'closeLabel'> & {
  title?: string; label: string; closeLabel: string;
}) {
  // Открывший лист — тот, на ком фокус в миг открытия: содержимое монтируется вместе с листом.
  const [opener] = useState(() => document.activeElement as HTMLElement | null);
  const sheet = useRef<HTMLDivElement>(null);
  const drag = useDragToClose(sheet, onClose);
  return (
    <Dialog.Content ref={sheet} aria-describedby={undefined} data-modal-state="free"
      style={drag.offset ? { transform: `translateY(${drag.offset}px)` } : undefined}
      className={`fixed inset-x-0 bottom-0 z-50 max-h-[90dvh] flex flex-col overflow-hidden ${BOTTOM_SHEET_SURFACE} pb-[env(safe-area-inset-bottom)] focus:outline-none ${
        drag.settling ? 'motion-safe:transition-transform motion-safe:duration-200' : ''}`}
      onCloseAutoFocus={e => {
        if (!opener?.isConnected || opener === document.body) return;
        e.preventDefault();
        focusFromLayer(opener);
      }}>
      <div className="shrink-0 touch-none" {...drag.handlers}>
        <button type="button" data-sheet-handle aria-label={closeLabel} onClick={drag.handleClick}
          className="flex w-full justify-center pt-5.5 pb-1.5 focus-ring">
          <span aria-hidden className="h-1 w-8 rounded-full bg-on-surface-variant/40" />
        </button>
        {title ? (
          <div className="flex items-center gap-1 pt-1 pr-3 pb-2 pl-6">
            <Dialog.Title className={`flex-1 min-w-0 truncate ${SHEET_TITLE}`}>{title}</Dialog.Title>
            <IconButton label={closeLabel} icon={Close} onClick={onClose} />
          </div>
        ) : <Dialog.Title className="sr-only">{label}</Dialog.Title>}
      </div>
      <div className="flex-1 min-h-0 overflow-auto px-6 pb-6">{children}</div>
    </Dialog.Content>
  );
}

/** Сколько пикселей — уже жест, а не нажатие ручки. */
const SLOP = 4;

/** Жест «стянуть вниз» с ручки и шапки листа. */
function useDragToClose(sheet: RefObject<HTMLDivElement | null>, onClose: () => void) {
  const drag = useRef<{ id: number; y: number } | null>(null);
  // Жест с ручки кончается щелчком по ней — щелчок после жеста лист не закрывает.
  const dragged = useRef(false);
  const [offset, setOffset] = useState(0);
  const [settling, setSettling] = useState(false);
  const settle = () => { setSettling(true); setOffset(0); };
  const end = (e: PointerEvent) => {
    const d = drag.current;
    if (!d || d.id !== e.pointerId) return null;
    drag.current = null;
    return Math.max(0, e.clientY - d.y);
  };
  const handlers = {
    onPointerDown(e: PointerEvent<HTMLDivElement>) {
      // Крестик шапки — своя цель, не начало жеста.
      if (e.button !== 0 || (e.target as Element).closest('button:not([data-sheet-handle])')) return;
      drag.current = { id: e.pointerId, y: e.clientY };
      dragged.current = false;
      setSettling(false);
    },
    onPointerMove(e: PointerEvent<HTMLDivElement>) {
      const d = drag.current;
      if (!d || d.id !== e.pointerId) return;
      const dy = Math.max(0, e.clientY - d.y);
      // Указатель захватываем, только когда начался жест: захват с нажатия уводит щелчок с ручки на шапку,
      // и касание ручки лист не закрывало бы.
      if (dy > SLOP && !dragged.current) {
        dragged.current = true;
        e.currentTarget.setPointerCapture?.(e.pointerId);
      }
      setOffset(dy);
    },
    onPointerUp(e: PointerEvent) {
      const dy = end(e);
      if (dy === null) return;
      if (dy > (sheet.current?.offsetHeight ?? Infinity) / 2) onClose();
      else settle();
    },
    // Жест отняла система (прокрутка, звонок) — лист остаётся.
    onPointerCancel(e: PointerEvent) {
      if (end(e) !== null) settle();
    },
  };
  const handleClick = () => {
    if (dragged.current) { dragged.current = false; return; }
    onClose();
  };
  return { offset, settling, handlers, handleClick };
}
