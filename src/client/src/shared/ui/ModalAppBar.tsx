import { IconButton } from './Button';
import { ArrowBack, Close } from './icons';
import type { BarAction } from './modalControl';
import { RowActionsMenu } from './RowActionsMenu';

/**
 * Шапка окна во весь экран (sharedui-md3-13, [UI-75]) по образцу top app bar: у ведущего края крестик
 * (на шаге — стрелка «Назад»), заголовок `title-large`, место главного действия, «⋮» с прочими
 * действиями; под ней — место пояснения. Места заполняют сами кнопки подвала с ролью (`ModalRoles`).
 */
export function ModalAppBar({ title, back, busy, onExit, primaryRef, noteRef, actions, edge }: {
  title: string;
  /** На шаге: выход ведёт назад, к форме окна. */
  back: boolean;
  busy: boolean;
  /** Тот же охраняемый выход, что Esc и «Назад». */
  onExit: () => void;
  primaryRef: (el: HTMLElement | null) => void;
  noteRef: (el: HTMLElement | null) => void;
  actions: BarAction[];
  /** Класс черты под шапкой — она видна, когда тело ушло под шапку. */
  edge: string;
}) {
  return (
    <div className={`shrink-0 border-b ${edge}`}>
      <div className="flex items-center gap-1 h-16 px-1">
        {/* Выход виден и при идущем запросе, но не нажимается — как `ModalExit` (см. `BUSY_EXIT`). */}
        <IconButton icon={back ? ArrowBack : Close} label={back ? 'Назад' : 'Закрыть'} data-modal-exit=""
          aria-disabled={busy || undefined} className={busy ? 'pointer-events-none' : ''} onClick={onExit} />
        <span className="flex-1 min-w-0 truncate ml-1 text-title-large text-on-surface">{title}</span>
        <div ref={primaryRef} className="flex items-center empty:hidden" />
        {actions.length > 0 && (
          <RowActionsMenu ariaLabel="Другие действия" actions={actions.map((a, i) => ({
            key: String(i), label: a.label, danger: a.danger, disabled: a.disabled, onSelect: a.run,
          }))} />
        )}
      </div>
      <div ref={noteRef} className="px-4 pb-3 empty:hidden" />
    </div>
  );
}
