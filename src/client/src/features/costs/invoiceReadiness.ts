import type { InvoiceReadinessGap, InvoiceView } from '@/shared/api/invoices';

/**
 * Строка готовности счёта — правила экрана (issue #1291).
 *
 * <p>⚠️ Перечень «чего не хватает до „Разобран“» строит СЕРВЕР, той же функцией, что проверяет переход.
 * Здесь нет ни одного условия готовности: только то, куда пункт ведёт на форме и можно ли туда вести.
 * Выводи форма перечень сама, она разошлась бы с переходом на первом новом условии — обязательные поля
 * и «ссылки живы» знает только сервер.</p>
 */

/** Место формы, к которому ведёт пункт: поле шапки, таблица строк или выбор объекта разноски. */
export type ReadinessTarget = { place: 'field'; key: string } | { place: 'lines' } | { place: 'allocation' };

export interface ReadinessItem {
  /** Устойчивый ключ пункта — для списка. */
  id: string;
  label: string;
  /** Куда пункт ведёт; `null` — вести некуда: действия у человека нет. */
  target: ReadinessTarget | null;
  /** Почему перехода нет — подсказкой у пункта. */
  why?: string;
}

/** Куда ведёт пункт сам по себе, без оглядки на права. */
function targetOf(gap: InvoiceReadinessGap): ReadinessTarget {
  switch (gap.kind) {
    case 'field':
    case 'reference':
      return gap.field ? { place: 'field', key: gap.field } : { place: 'lines' };
    case 'allocation':
      return { place: 'allocation' };
    default:
      // Строк нет, строки ждут позиции, позиции нет в справочнике, справочника нет — всё про таблицу
      // строк. Сюда же попадёт вид пункта, которого эта форма ещё не знает: пункт не теряется.
      return { place: 'lines' };
  }
}

/**
 * Пункты строки. `null` — строки нет: счёт разобран либо отклонён, либо сервер перечня не прислал
 * (прежняя версия). ⚠️ Пустой массив — другое: «всё готово, можно нажать „Разобран“».
 *
 * <p>Пункт, на действие которого у человека нет права, <b>виден, но не ведёт</b> (решение владельца):
 * перечень не делится на «ваше» и «чужое», а счёт доводят разные люди — тот, кто продолжает чужой
 * счёт, обязан видеть всё, что осталось.</p>
 */
export function readinessItems(
  view: InvoiceView, can: { edit: boolean; allocate: boolean },
): ReadinessItem[] | null {
  const gaps = view.readiness?.gaps;
  if (gaps == null) return null;

  return gaps.map((gap, index) => {
    const target = targetOf(gap);
    const allowed = target.place === 'allocation' ? can.allocate : can.edit;
    return {
      id: `${gap.kind}:${gap.field ?? index}`,
      label: gap.label,
      target: allowed ? target : null,
      why: allowed ? undefined
        : target.place === 'allocation' ? 'Разносит счёт тот, у кого есть право разносить по стройкам'
          : 'Заполняет тот, у кого есть право вводить счета',
    };
  });
}

/** Обязательно ли поле шапки — по перечню сервера; старый сервер его не присылает, и пометок нет. */
export const isRequired = (view: InvoiceView, fieldKey: string): boolean =>
  view.readiness?.required.includes(fieldKey) === true;

/** Имя атрибута, которым место формы называет себя строке готовности. */
export const READINESS_ATTR = 'data-readiness';

export const readinessAnchor = (target: ReadinessTarget): string =>
  target.place === 'field' ? `field:${target.key}` : target.place;

/**
 * Привести человека к месту формы: показать его и поставить туда ввод. Места называют себя сами —
 * атрибутом; протягивать ссылки на них через четыре слоя формы ради одной прокрутки незачем.
 */
export function goTo(target: ReadinessTarget, root: ParentNode = document): boolean {
  const place = root.querySelector<HTMLElement>(`[${READINESS_ATTR}="${readinessAnchor(target)}"]`);
  if (!place) return false;

  place.scrollIntoView({ block: 'center', behavior: 'smooth' });
  place.querySelector<HTMLElement>('input:not([readonly]):not([disabled]), button:not([disabled]), select, textarea')
    ?.focus({ preventScroll: true });
  return true;
}
