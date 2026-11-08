import type { ReactNode } from 'react';
import { ChevronLeft, ChevronRight, DocumentScanner } from '@/shared/ui/icons';
import { IconButton } from '@/shared/ui/Button';
import { RowActionsMenu, type RowAction } from '@/shared/ui/RowActionsMenu';
import type { InvoicePosition } from './invoicePlace';

/**
 * Обвязка страницы счетов при открытом счёте (costs-invoice-screen-10-layout, D-10.3, D-10.4, D-10.8, ТЗ
 * COST-6.3): ширина рабочей области, «‹ N из M ›» и действия страницы вне её шапки.
 */

/**
 * «‹ N из M ›» в сводке, когда списка нет (D-10.4): N и M — по тем строкам, что показал бы список при
 * текущих отборе, поиске и чипе. Стрелки ведут к соседнему счёту отбора той же сменой счёта, что щелчок по
 * строке (через охрану ухода); на краю — неактивны. «N из M» закрывает счёт и показывает список. Подпись
 * под числом — имя отбора.
 */
export function InvoiceStepper({ position, filter, onGo, onList }: {
  position: InvoicePosition;
  /** Имя отбора: «Все», «Разобрать»… */
  filter: string;
  onGo: (id: string) => void;
  onList: () => void;
}) {
  const { index, total, prev, next } = position;
  const at = `${index ?? '—'} из ${total}`;
  return (
    <div className="shrink-0 flex items-center" role="group" aria-label="Счета отбора">
      <IconButton icon={ChevronLeft} label="Предыдущий счёт" disabled={!prev} onClick={() => prev && onGo(prev)} />
      <button type="button" onClick={onList} aria-label={`${at}, отбор «${filter}» — к списку`}
        className="flex flex-col items-center px-1 rounded-sm state-layer focus-ring">
        <span className="text-label-large text-on-surface tabular-nums whitespace-nowrap">{at}</span>
        <span className="text-label-small text-on-surface-variant whitespace-nowrap">{filter}</span>
      </button>
      <IconButton icon={ChevronRight} label="Следующий счёт" disabled={!next} onClick={() => next && onGo(next)} />
    </div>
  );
}

/**
 * «Счёт из файла» значком — в сводке открытого счёта, всегда (D7 у 14): при любой ширине и при видимом списке,
 * в шапке списка его нет. `label` — и имя для диктора, и подсказка: кнопка ставит `Tooltip` сама. Исполнитель —
 * тот же, что у палитры команд.
 */
export function FromFileButton({ loading, title, onClick }: { loading: boolean; title: string; onClick: () => void }) {
  // Подсказка — та же, что у кнопки шапки (ревью PR #1819): какие файлы распознаются, видно до выбора.
  return <IconButton icon={DocumentScanner} label="Счёт из файла — или перетащите файл в окно" title={title}
    loading={loading} onClick={onClick} />;
}

/**
 * Шапка списка при открытом счёте (D-10.8): заголовок страницы переезжает сюда, рядом — «⋮» со справочниками
 * и «Новый счёт». «Счёт из файла» здесь нет: при открытом счёте он в сводке (D7 у 14).
 */
export function InvoiceListHeader({ actions, fromFile }: { actions: RowAction[]; fromFile?: ReactNode }) {
  return (
    <div className="shrink-0 flex items-center gap-1 pl-4 pr-2 pt-3">
      <h2 className="flex-1 min-w-0 truncate text-title-large text-on-surface">Счета на оплату</h2>
      {fromFile}
      {actions.length > 0 && <RowActionsMenu actions={actions} ariaLabel="Действия списка" />}
    </div>
  );
}
