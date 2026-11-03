import { useEffect, useRef, useState, type ReactNode, type RefObject } from 'react';
import * as Dialog from '@radix-ui/react-dialog';
import { NARROW, useMedia } from '@/shared/hooks/useMedia';
import { BottomSheet, type SheetBase } from './BottomSheet';
import { IconButton } from './Button';
import { DIALOG_SCRIM, SHEET_TITLE } from './dialogSurface';
import { useHistoryLayer } from './historyLayers';
import { Close } from './icons';
import { focusFromLayer } from './layerFocus';
import { LayerBoundary } from './LayerBoundary';

/**
 * Боковая панель — side sheet MD3 ([UI-77], sharedui-md3-13), шириной 400, заголовок `title-large`, крестик у
 * замыкающего края шапки.
 *
 * <ul>
 *   <li><b>standard</b> стоит в раскладке рядом с содержимым и сдвигает его — затемнения нет. Экран ставит её
 *   последней в flex-ряд, только пока она открыта. Esc из панели закрывает её. Уже 768 — лист во весь экран
 *   поверх содержимого, крестик у ведущего края, и «Назад» закрывает его своим слоем истории. Страницу он
 *   закрывает целиком, а потому он окно (Radix Dialog без затемнения): страница под ним не в Tab и не в
 *   читалке.</li>
 *   <li><b>modal</b> лежит над содержимым: затемнение, `surface-container-low`, уровень 1, ведущие углы 16;
 *   фокус, Esc и «Назад» — как у окна. Уже 768 — `BottomSheet` с тем же содержимым.</li>
 * </ul>
 *
 * <p>Смена ширины перемонтирует содержимое — набранное в листе при повороте экрана теряется (design 2б).</p>
 *
 * <p><b>Standard внутри окна</b> ([UI-81], sharedui-md3-17: карточка позиции в окне выбора). Esc закрывает
 * сначала лист, затем окно — и тогда, когда фокус не в листе, а в поиске окна. `Modal` отвечает на Esc, только
 * если клавиша дошла до документа (`afterFields`); лист слушает её там же раньше окна и останавливает. Esc,
 * которым распорядилось поле или слой над окном (меню, список), до листа не доходит. `autoFocus={false}` —
 * фокус остаётся у того, кто лист открыл (поиск окна: набор не прерывается выбором строки).</p>
 */
export type SideSheetProps = SheetBase & {
  variant: 'standard' | 'modal';
  /** Действия шапки — между заголовком и крестиком. */
  actions?: ReactNode;
  /** Standard: сменился — фокус снова на крестик (панель показала другую запись). */
  focusKey?: unknown;
  /** Standard: `false` — фокус в лист не переходит, остаётся у открывшего. */
  autoFocus?: boolean;
  // Заголовок не строкой именем листа не служит — тогда имя для читалки (`label`) обязательно.
} & ({ title: string; label?: string } | { title: Exclude<ReactNode, string>; label: string });

export function SideSheet(props: SideSheetProps) {
  if (props.variant === 'modal') return <ModalSideSheet {...props} />;
  return props.open ? <StandardSheet {...props} /> : null;
}

function ModalSideSheet({ open, onClose, title, label, closeLabel = 'Закрыть', actions, children }: SideSheetProps) {
  const narrow = useMedia(NARROW);
  // Шапка BottomSheet — строка заголовка; заголовок не строкой лист снизу не показывает и зовётся `label`.
  if (narrow) return typeof title === 'string'
    ? <BottomSheet open={open} onClose={onClose} title={title} label={label} closeLabel={closeLabel}>{children}</BottomSheet>
    : <BottomSheet open={open} onClose={onClose} label={label ?? ''} closeLabel={closeLabel}>{children}</BottomSheet>;
  return <ModalSideDialog open={open} onClose={onClose} title={title} closeLabel={closeLabel} actions={actions}>{children}</ModalSideDialog>;
}

function ModalSideDialog({ open, onClose, title, closeLabel, actions, children }: {
  open: boolean; onClose: () => void; title: ReactNode; closeLabel: string; actions?: ReactNode; children: ReactNode;
}) {
  useHistoryLayer(open, () => { onClose(); return true; });
  return (
    <Dialog.Root open={open} onOpenChange={o => { if (!o) onClose(); }}>
      {/* Лист — новое место: размер поля клетки сквозь портал сюда не идёт (как у `Modal`). */}
      <LayerBoundary>
      <Dialog.Portal>
        <Dialog.Overlay className={DIALOG_SCRIM} />
        <ModalSideContent title={title} closeLabel={closeLabel} onClose={onClose} actions={actions}>{children}</ModalSideContent>
      </Dialog.Portal>
      </LayerBoundary>
    </Dialog.Root>
  );
}

function ModalSideContent({ title, closeLabel, onClose, actions, children }: {
  title: ReactNode; closeLabel: string; onClose: () => void; actions?: ReactNode; children: ReactNode;
}) {
  const [opener] = useState(() => document.activeElement as HTMLElement | null);
  return (
    <Dialog.Content aria-describedby={undefined} data-modal-state="free"
      className="fixed inset-y-0 right-0 z-50 w-full max-w-100 flex flex-col rounded-l-lg bg-surface-container-low shadow-elevation-1 focus:outline-none"
      onCloseAutoFocus={e => {
        if (!opener?.isConnected || opener === document.body) return;
        e.preventDefault();
        focusFromLayer(opener);
      }}>
      <SheetHeader title={<Dialog.Title className={`truncate ${SHEET_TITLE}`}>{title}</Dialog.Title>} actions={actions}
        close={<IconButton label={closeLabel} icon={Close} onClick={onClose} />} />
      <div className="flex-1 min-h-0 overflow-auto px-6 pb-6">{children}</div>
    </Dialog.Content>
  );
}

function StandardSheet({ onClose, title, label, closeLabel = 'Закрыть', actions, focusKey, autoFocus = true, children }: SideSheetProps) {
  const narrow = useMedia(NARROW);
  const close = useRef<HTMLButtonElement>(null);
  const aside = useRef<HTMLElement>(null);
  const [opener] = useState(() => document.activeElement as HTMLElement | null);
  // Во весь экран «Назад» закрывает лист, а не уводит со страницы (на столе слоёв истории нет вовсе).
  useHistoryLayer(narrow, () => { onClose(); return true; });

  // Фокус — в лист: Esc закрывает его с клавиатуры, и читалка узнаёт, что лист открылся.
  useEffect(() => { if (autoFocus && close.current) focusFromLayer(close.current); }, [focusKey, narrow, autoFocus]);
  useWindowEscape(narrow ? null : aside, onClose);
  // Ушёл лист с фокусом внутри — фокус возвращается к тому, что его открыло.
  useEffect(() => () => {
    if (opener?.isConnected && opener !== document.body && document.activeElement === document.body) focusFromLayer(opener);
  }, [opener]);

  const closeButton = <IconButton label={closeLabel} ref={close} icon={Close} onClick={onClose} />;
  const body = <div className="flex-1 min-h-0 overflow-auto px-6 pb-6">{children}</div>;
  if (narrow) return (
    // Во весь экран — окно без затемнения: Esc, ловушку фокуса и страницу вне Tab и читалки даёт Radix.
    <Dialog.Root open onOpenChange={o => { if (!o) onClose(); }}>
      {/* Лист — новое место: размер поля клетки сквозь портал сюда не идёт (как у `Modal`). */}
      <LayerBoundary>
      <Dialog.Portal>
        <Dialog.Content aria-describedby={undefined} aria-label={label} data-modal-state="free"
          className="fixed inset-0 z-40 flex flex-col bg-surface focus:outline-none"
          // Содержимое портала монтируется позже эффекта выше — первый фокус на крестик ставим здесь.
          onOpenAutoFocus={e => { e.preventDefault(); if (close.current) focusFromLayer(close.current); }}
          // Фокус к открывшему возвращает эффект выше — один владелец на оба вида.
          onCloseAutoFocus={e => e.preventDefault()}>
          <SheetHeader lead={closeButton} actions={actions}
            title={<Dialog.Title className={`truncate ${SHEET_TITLE}`}>{title}</Dialog.Title>} />
          {body}
        </Dialog.Content>
      </Dialog.Portal>
      </LayerBoundary>
    </Dialog.Root>
  );
  return (
    <aside ref={aside} aria-label={label ?? (typeof title === 'string' ? title : undefined)}
      onKeyDown={e => {
        // Esc — свой: не поднятый порталом из слоя (меню, список), не обработанный полем или меню внутри и не
        // конец набора IME. В окне Esc ловит `useWindowEscape`: `Modal` уже отменил клавишу (`defaultPrevented`).
        if (e.key !== 'Escape' || e.defaultPrevented || e.nativeEvent.isComposing || !e.currentTarget.contains(e.target as Node)) return;
        e.stopPropagation();
        onClose();
      }}
      className="w-100 shrink-0 flex flex-col min-h-0 border-l border-outline-variant bg-surface">
      <SheetHeader close={closeButton} actions={actions} title={<h2 className={`truncate ${SHEET_TITLE}`}>{title}</h2>} />
      {/* Лист рядом со страницей — тоже слой: портала нет, но место своё. */}
      <LayerBoundary>{body}</LayerBoundary>
    </aside>
  );
}

/**
 * Esc листа внутри окна Radix: слушатель на документе, на всплытии — после полей (поле, обработавшее Esc,
 * останавливает её, как требует `Modal`) и раньше `Modal`, чей слушатель ставится уже во время нажатия. Клавиша
 * — из того же окна: из меню или списка над ним (свой портал) она принадлежит им.
 */
function useWindowEscape(sheet: RefObject<HTMLElement | null> | null, onClose: () => void) {
  useEffect(() => {
    const dialog = sheet?.current?.closest('[role=dialog]');
    if (!dialog) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || e.isComposing || !dialog.contains(e.target as Node)) return;
      e.stopImmediatePropagation();
      onClose();
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [sheet, onClose]);
}

/** Шапка листа, не ниже 72: крестик у замыкающего края, а во весь экран — у ведущего. */
function SheetHeader({ lead, title, actions, close }: { lead?: ReactNode; title: ReactNode; actions?: ReactNode; close?: ReactNode }) {
  return (
    <div className={`shrink-0 flex items-center gap-1 min-h-18 py-3 pr-3 ${lead ? 'pl-1' : 'pl-6'}`}>
      {lead}
      <div className={`flex-1 min-w-0 ${lead ? 'ml-2' : ''}`}>{title}</div>
      {actions}
      {close}
    </div>
  );
}
