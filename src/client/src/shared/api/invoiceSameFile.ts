import { apiClient } from './client';
import type { InvoiceSameFile } from './invoices';

/**
 * Ответ вопроса «нет ли уже счёта с этим файлом» — запись на каждый спрошенный отпечаток, в порядке
 * вопроса (COST-6.7). Пустой `twins` — двойников среди счетов с доверенным отпечатком нет; это НЕ «файл
 * новый»: счета, заведённые до отпечатка, не сравниваются вовсе.
 */
export interface SameFileAnswer {
  /** Отпечаток в нижнем регистре — как его вернул сервер. */
  fingerprint: string;
  twins: InvoiceSameFile[];
}

/**
 * Спросить по отпечаткам SHA-256 (64 шестнадцатеричных знака), есть ли уже счета с теми же файлами, —
 * ДО выгрузки, одним запросом на бросок (лист «Ф» 27.10.2026). Только читает.
 *
 * Параметр повторяется (`fingerprint=…&fingerprint=…`) — так его разбирает сервер. Строка запроса
 * собирается здесь, а не опцией `params`: axios по умолчанию написал бы `fingerprint[]=…`, и сервер не
 * увидел бы ни одного отпечатка.
 */
export async function sameFileByFingerprints(prints: readonly string[]): Promise<SameFileAnswer[]> {
  const query = new URLSearchParams();
  for (const print of prints) query.append('fingerprint', print);
  return (await apiClient.get<SameFileAnswer[]>(`/costs/invoices/same-file?${query.toString()}`)).data;
}
