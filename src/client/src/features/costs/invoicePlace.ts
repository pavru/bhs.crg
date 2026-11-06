/**
 * Место на экране счёта (costs-invoice-screen-10-layout, D-10.3, ТЗ COST-6.3): где стоят список и файл.
 *
 * <p>Считается по ширине РАБОЧЕЙ ОБЛАСТИ — места под рейлом, которое меряет `ResizeObserver` страницы, — а
 * не по окну: окно 1280 с открытой подпанелью и окно 1024 дают форме разное место, хотя «по окну» первое
 * шире. Подпанель при открытом счёте скрыта (D-10.5), поэтому окно 1280 даёт 1200.</p>
 *
 * <p>Числа выведены из решения владельца 28.09.2026 («файл рядом — от 1280») при новых размерах: окно 1280
 * даёт файлу 456 и форме 744 — рядом, окно 1024 даёт 420 и 524 — поверх. Порог 720 встаёт между ними.
 * Меняется число — одной константой здесь (риски design.md).</p>
 */

/** Сколько нужно форме рядом с файлом. */
export const FORM_BESIDE_FILE = 720;
/** Сколько нужно форме рядом со списком. */
export const FORM_BESIDE_LIST = 940;
/** Ширина колонки списка (`ListDetailShell`, `w-80`). */
export const LIST_WIDTH = 320;
/** Панель файла — 38 % рабочей области, от 420 до 520 (решение Э10): опорная, а не главная. */
const FILE_SHARE = 0.38;
const FILE_MIN = 420;
const FILE_MAX = 520;

export interface InvoicePlace {
  /** Список слева; нет — он свёрнут в «‹ N из M ›» сводки. */
  list: boolean;
  /** Файл рядом, поверх формы или закрыт. */
  file: 'beside' | 'over' | 'closed';
  /** Ширина панели файла, px. */
  fileWidth: number;
}

/**
 * Место по ширине рабочей области и желанию видеть файл. Файл открыт — списка нет, как в макете Г:
 * «список ИЛИ файл» на 1366 владелец принял ответом 6.
 */
export function placeOf(workWidth: number, fileWanted: boolean): InvoicePlace {
  const fileWidth = Math.min(FILE_MAX, Math.max(FILE_MIN, Math.round(workWidth * FILE_SHARE)));
  const file = !fileWanted ? 'closed' : workWidth - fileWidth >= FORM_BESIDE_FILE ? 'beside' : 'over';
  const list = file !== 'beside' && workWidth - LIST_WIDTH >= FORM_BESIDE_LIST;
  return { list, file, fileWidth };
}

/** Причина «поверх» — словами подсказки кнопки «Файл» (ТЗ COST-6.2, «Узкий экран»). */
export const FILE_OVER_REASON = `Файл откроется поверх формы: рядом форме нужно не меньше ${FORM_BESIDE_FILE} px`;

/**
 * Хочет ли человек видеть файл рядом (ТЗ COST-6.3). У черновика с непроверенными значениями со скана или
 * со сбоем распознавания — открыт: сверять их не с чем, кроме бумаги. Остальным — как человек оставил
 * (`remembered`). Выбор по ЭТОМУ счёту (`chosen`) сильнее обоих: закрытый у распознанного черновика
 * файл не открывается снова на каждой перерисовке.
 */
export function fileWanted({ hasFile, needsPaper, remembered, chosen }: {
  hasFile: boolean;
  /** Черновик с метками «со скана, не проверено» или со сбоем распознавания. */
  needsPaper: boolean;
  remembered: boolean;
  chosen: boolean | null;
}): boolean {
  if (!hasFile) return false;
  if (chosen !== null) return chosen;
  return needsPaper || remembered;
}

/** «‹ N из M ›» (D-10.4): место открытого счёта в отборе списка. */
export interface InvoicePosition {
  /** С единицы; `null` — счёта в отборе нет (открыт адресом и скрыт отбором): «— из M». */
  index: number | null;
  total: number;
  prev: string | null;
  next: string | null;
}

/** Место счёта среди строк, которые показал бы список при текущих отборе, поиске и чипе. */
export function positionOf(ids: readonly string[], selected: string): InvoicePosition {
  const at = ids.indexOf(selected);
  if (at < 0) return { index: null, total: ids.length, prev: null, next: null };
  return { index: at + 1, total: ids.length, prev: ids[at - 1] ?? null, next: ids[at + 1] ?? null };
}
