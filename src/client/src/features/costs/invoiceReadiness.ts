import type { InvoiceReadinessGap, InvoiceView } from '@/shared/api/invoices';

/**
 * Строка готовности счёта — правила экрана (issue #1291).
 *
 * <p>⚠️ Перечень «чего не хватает до „Разобран“» строит СЕРВЕР, той же функцией, что проверяет переход.
 * Здесь нет ни одного условия готовности: только то, куда пункт ведёт на форме и можно ли туда вести.
 * Выводи форма перечень сама, она разошлась бы с переходом на первом новом условии — обязательные поля
 * и «ссылки живы» знает только сервер.</p>
 */

/**
 * Место формы, к которому ведёт пункт: поле шапки, таблица строк (или названная строка в ней) либо
 * выбор объекта разноски.
 */
export type ReadinessTarget =
  | { place: 'field'; key: string }
  | { place: 'lines'; line?: number }
  | { place: 'allocation' };

export interface ReadinessItem {
  /** Устойчивый ключ пункта — для списка. */
  id: string;
  label: string;
  /** Куда пункт ведёт; `null` — вести некуда: действия у человека нет. */
  target: ReadinessTarget | null;
  /** Почему перехода нет — подсказкой у пункта. */
  why?: string;
}

/**
 * Куда ведёт пункт сам по себе, без оглядки на права.
 *
 * <p>Сервер называет строки (`lines`) — и пункт ведёт к первой из них, а не к таблице целиком: в счёте
 * на сорок строк «позиции нет: строка 37» иначе оставляло искать её глазами (ревью PR #1301).</p>
 */
function targetOf(gap: InvoiceReadinessGap, lineCount: number): ReadinessTarget {
  const line = gap.lines?.[0];

  if (gap.kind === 'allocation') {
    // Не разнесено ничего — быстрее всего выбрать объект счёта: он разносит все строки разом. Не
    // разнесена часть строк — объект уже не поможет, чинится это в самой строке.
    const some = gap.lines != null && gap.lines.length > 0 && gap.lines.length < lineCount;
    return some ? { place: 'lines', line } : { place: 'allocation' };
  }

  // Поле шапки: не заполнено, запись потеряна, сумма расходится. По ключу, а не по виду пункта —
  // новый вид с полем доедет до своего поля без правки формы.
  if (gap.field) return { place: 'field', key: gap.field };

  // Строк нет, строки ждут позиции, позиции нет в справочнике, справочника нет — всё про таблицу
  // строк. Сюда же попадёт вид пункта, которого эта форма ещё не знает: пункт не теряется.
  return { place: 'lines', line };
}

/**
 * Пункты строки. `null` — строки нет: счёт разобран либо отклонён, либо сервер перечня не прислал
 * (прежняя версия). ⚠️ Пустой массив — другое: «всё готово, можно нажать „Разобран“».
 *
 * <p>`can` — ПРАВА человека, а не «можно ли сейчас»: запертому счёту звавший строку не рисует
 * (сервер перечня ему и не присылает), иначе подсказка «нет права» стояла бы там, где право есть, а
 * мешает закрытый период.</p>
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
    const target = targetOf(gap, view.lines.length);
    // Право — по ВИДУ пункта, а не по месту: разноска строки чинится в таблице строк, но правом
    // разносить, а не правом вводить.
    const allocation = gap.kind === 'allocation';
    const allowed = allocation ? can.allocate : can.edit;
    return {
      id: `${gap.kind}:${gap.field ?? index}`,
      label: gap.label,
      target: allowed ? target : null,
      // ⚠️ Причина здесь — только право. Замок (закрытый период, удалённый счёт) сюда не доходит:
      // запертому счёту строка не рисуется вовсе, и назвать замок «нет права» эта подсказка не может.
      why: allowed ? undefined
        : allocation ? 'Разносит счёт тот, у кого есть право разносить по стройкам'
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
  target.place === 'field' ? `field:${target.key}`
    : target.place === 'lines' && target.line != null ? lineAnchor(target.line)
      : target.place;

/** Имя, которым называет себя строка таблицы — по номеру, как её называет сервер. */
export const lineAnchor = (number: number): string => `line:${number}`;

/**
 * Привести человека к месту формы: показать его и поставить туда ввод. Места называют себя сами —
 * атрибутом; протягивать ссылки на них через четыре слоя формы ради одной прокрутки незачем.
 */
export function goTo(target: ReadinessTarget, root: ParentNode = document): boolean {
  const find = (anchor: string) => root.querySelector<HTMLElement>(`[${READINESS_ATTR}="${anchor}"]`);
  // Названной строки на экране может не быть (строки правят, номера сдвинулись) — тогда к таблице.
  const place = find(readinessAnchor(target)) ?? (target.place === 'lines' ? find('lines') : null);
  if (!place) return false;

  place.scrollIntoView({ block: 'center', behavior: 'smooth' });
  place.querySelector<HTMLElement>('input:not([readonly]):not([disabled]), button:not([disabled]), select, textarea')
    ?.focus({ preventScroll: true });
  return true;
}
