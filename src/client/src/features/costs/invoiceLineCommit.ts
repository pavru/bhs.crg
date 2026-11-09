/**
 * Повод записи строк счёта — уход из строки (costs-invoice-screen-12-autosave, D-12.2, ТЗ COST-6.5).
 *
 * <p>Строка пишется, когда фокус ушёл ИЗ НЕЁ, а не из клетки: пауза посреди строки закрепляла бы
 * промежуточное («1 × 100», пока набирается «10»). Переход между клетками одной строки записи не даёт.</p>
 *
 * <p>Слой, открытый из клетки (список единиц, окно выбора позиции, окно разноски), лежит порталом вне
 * таблицы — фокус в нём уходом из строки не считается: его повод — сам выбор. Событие из такого слоя
 * всплывает сюда по дереву React, но в таблице не лежит — его тоже не считаем.</p>
 *
 * <p>Чистые функции над DOM — их проверяют тесты без формы.</p>
 */

/** Слои, вынесенные порталом: фокус в них — не уход из строки. */
const LAYER = '[data-radix-popper-content-wrapper], [role="dialog"], [role="alertdialog"], [role="listbox"], [role="menu"]';

/** Строка сетки, в которой лежит элемент, — если он в этой таблице. */
function rowOf(el: EventTarget | null, table: Element): Element | null {
  if (!(el instanceof Element) || !table.contains(el)) return null;
  return el.closest('tr[data-row]');
}

/**
 * Ушёл ли фокус из строки: `from` — откуда (цель `focusout`), `to` — куда (`relatedTarget`; `null` — в
 * никуда, щелчок мимо полей).
 */
export function leftRow(from: EventTarget | null, to: EventTarget | null, table: Element | null): boolean {
  if (!table) return false;
  const row = rowOf(from, table);
  if (!row) return false;
  if (!(to instanceof Element)) return true;
  if (row.contains(to)) return false;
  return table.contains(to) || !to.closest(LAYER);
}

/** Поля строки, по которым ходит Tab, — в порядке документа. */
const FIELDS = '[data-cell] input:not([type="hidden"]):not([disabled]), [data-cell] textarea:not([disabled])';

/** Enter в последнем поле строки — «как уход из строки» (D-12.2). */
export function enterInLastField(key: string, target: EventTarget | null, table: Element | null): boolean {
  if (key !== 'Enter' || !table) return false;
  const row = rowOf(target, table);
  if (!row || !(target instanceof HTMLElement) || target instanceof HTMLTextAreaElement) return false;
  const fields = row.querySelectorAll(FIELDS);
  return fields.length > 0 && fields[fields.length - 1] === target;
}
