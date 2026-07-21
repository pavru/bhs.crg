import { AlertCircle } from 'lucide-react';

/**
 * Индикатор БИТОЙ ссылки на поле редактора реквизитов (issue #332): целевой объект удалён.
 * Сигнал = danger (красный): warning уже занят под НОРМАЛЬНЫЕ состояния ссылок (catalog «Общие
 * данные», элементы массива). От обязательного-незаполненного отличается анатомией — битая плитка
 * ЗАПОЛНЕНА (есть displayName) + красная рамка + AlertCircle + нота «объект удалён» + приглушённый
 * (зачёркнутый) displayName как «что было».
 */
export const BROKEN_REF_MESSAGE = 'Ссылка не разрешена — целевой объект не найден или удалён';

/** Классы danger-плитки битой ссылки (единый вид для скалярной/каталожной/элемента массива). */
export const BROKEN_PLATE = 'border border-danger bg-danger-subtle';

/** Приглушённый/зачёркнутый displayName битой ссылки — «что было». */
export const BROKEN_LABEL = 'text-danger line-through opacity-70';

/** Нота под битой плиткой. compact — для элементов массива/вложенных строк. */
export function BrokenRefNote({ compact = false }: { compact?: boolean }) {
  return (
    <div className={`flex items-center gap-1.5 text-danger ${compact ? 'text-[11px] px-3 pb-1.5' : 'text-xs mt-1'}`}>
      <AlertCircle size={compact ? 11 : 13} className="shrink-0" />
      <span>{BROKEN_REF_MESSAGE}</span>
    </div>
  );
}
