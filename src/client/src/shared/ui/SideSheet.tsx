import { useEffect, useRef, useState, type ReactNode } from 'react';
import * as Dialog from '@radix-ui/react-dialog';
import { NARROW, useMedia } from '@/shared/hooks/useMedia';
import { BottomSheet, type SheetProps } from './BottomSheet';
import { IconButton } from './Button';
import { DIALOG_SCRIM, SHEET_TITLE } from './dialogSurface';
import { useHistoryLayer } from './historyLayers';
import { Close } from './icons';
import { focusFromLayer } from './layerFocus';

/**
 * Боковая панель — side sheet MD3 ([UI-77], sharedui-md3-13), шириной 400, заголовок `title-large`, крестик у
 * замыкающего края шапки.
 *
 * <ul>
 *   <li><b>standard</b> стоит в раскладке рядом с содержимым и сдвигает его — затемнения нет. Экран ставит её
 *   последней в flex-ряд, только пока она открыта. Esc из панели закрывает её. Уже 768 — лист во весь экран
 *   поверх содержимого, крестик у ведущего края, и «Назад» закрывает его своим слоем истории.</li>
 *   <li><b>modal</b> лежит над содержимым: затемнение, `surface-container-low`, уровень 1, ведущие углы 16;
 *   фокус, Esc и «Назад» — как у окна. Уже 768 — `BottomSheet` с тем же содержимым.</li>
 * </ul>
 *
 * <p>Смена ширины перемонтирует содержимое — набранное в листе при повороте экрана теряется (design 2б).</p>
 */
export interface SideSheetProps extends Omit<SheetProps, 'title'> {
  variant: 'standard' | 'modal';
  title: ReactNode;
  /** Действия шапки — между заголовком и крестиком. */
  actions?: ReactNode;
  /** Standard: сменился — фокус снова на крестик (панель показала другую запись). */
  focusKey?: unknown;
}

export function SideSheet(props: SideSheetProps) {
  if (props.variant === 'modal') return <ModalSideSheet {...props} />;
  return props.open ? <StandardSheet {...props} /> : null;
}

function ModalSideSheet({ open, onClose, title, label, closeLabel = 'Закрыть', actions, children }: SideSheetProps) {
  const narrow = useMedia(NARROW);
  // Шапка BottomSheet — строка заголовка; ReactNode в заголовке листа снизу — только как имя.
  if (narrow) return (
    <BottomSheet open={open} onClose={onClose} title={typeof title === 'string' ? title : label} label={label} closeLabel={closeLabel}>
      {children}
    </BottomSheet>
  );
  return <ModalSideDialog open={open} onClose={onClose} title={title} closeLabel={closeLabel} actions={actions}>{children}</ModalSideDialog>;
}

function ModalSideDialog({ open, onClose, title, closeLabel, actions, children }: {
  open: boolean; onClose: () => void; title: ReactNode; closeLabel: string; actions?: ReactNode; children: ReactNode;
}) {
  useHistoryLayer(open, () => { onClose(); return true; });
  return (
    <Dialog.Root open={open} onOpenChange={o => { if (!o) onClose(); }}>
      <Dialog.Portal>
        <Dialog.Overlay className={DIALOG_SCRIM} />
        <ModalSideContent title={title} closeLabel={closeLabel} onClose={onClose} actions={actions}>{children}</ModalSideContent>
      </Dialog.Portal>
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

function StandardSheet({ onClose, title, label, closeLabel = 'Закрыть', actions, focusKey, children }: SideSheetProps) {
  const narrow = useMedia(NARROW);
  const close = useRef<HTMLButtonElement>(null);
  const [opener] = useState(() => document.activeElement as HTMLElement | null);
  // Во весь экран «Назад» закрывает лист, а не уводит со страницы (на столе слоёв истории нет вовсе).
  useHistoryLayer(narrow, () => { onClose(); return true; });

  // Фокус — в лист: Esc закрывает его с клавиатуры, и читалка узнаёт, что лист открылся.
  useEffect(() => { if (close.current) focusFromLayer(close.current); }, [focusKey, narrow]);
  // Ушёл лист с фокусом внутри — фокус возвращается к тому, что его открыло.
  useEffect(() => () => {
    if (opener?.isConnected && opener !== document.body && document.activeElement === document.body) focusFromLayer(opener);
  }, [opener]);

  const closeButton = <IconButton label={closeLabel} ref={close} icon={Close} onClick={onClose} />;
  return (
    <aside aria-label={label ?? (typeof title === 'string' ? title : undefined)}
      onKeyDown={e => { if (e.key === 'Escape') { e.stopPropagation(); onClose(); } }}
      className={narrow
        ? 'fixed inset-0 z-40 flex flex-col bg-surface'
        : 'w-100 shrink-0 flex flex-col min-h-0 border-l border-outline-variant bg-surface'}>
      <SheetHeader lead={narrow ? closeButton : undefined} close={narrow ? undefined : closeButton} actions={actions}
        title={<h2 className={`truncate ${SHEET_TITLE}`}>{title}</h2>} />
      <div className="flex-1 min-h-0 overflow-auto px-6 pb-6">{children}</div>
    </aside>
  );
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
