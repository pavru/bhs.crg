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
 * Сколько отпечатков сервер принимает за один вопрос (`InvoiceSameFiles.MaxFingerprints`; сверяет
 * `ScanLimitsAgreeTests`). Больше — 400 на весь вопрос, поэтому функция сама делит перечень на части.
 */
export const MAX_FINGERPRINTS = 50;

/**
 * Спросить по отпечаткам SHA-256 (64 шестнадцатеричных знака), есть ли уже счета с теми же файлами, —
 * ДО выгрузки, одним запросом на бросок (лист «Ф» 27.10.2026). Только читает.
 *
 * Перечень приводится к виду сервера — нижний регистр, без повторов, в порядке первого появления, — и
 * ответ идёт в том же порядке, по записи на отпечаток. Перечень длиннее {@link MAX_FINGERPRINTS} делится
 * на части: иначе сервер отказал бы целиком, и не проверен был бы ни один файл. Пустой — без запроса.
 *
 * Параметр повторяется (`fingerprint=…&fingerprint=…`) — так его разбирает сервер. Строка запроса
 * собирается здесь, а не опцией `params`: axios по умолчанию написал бы `fingerprint[]=…`, и сервер не
 * увидел бы ни одного отпечатка.
 */
export async function sameFileByFingerprints(prints: readonly string[]): Promise<SameFileAnswer[]> {
  const distinct = [...new Set(prints.map(print => print.toLowerCase()))];
  const parts: string[][] = [];
  for (let at = 0; at < distinct.length; at += MAX_FINGERPRINTS) parts.push(distinct.slice(at, at + MAX_FINGERPRINTS));
  const answers = await Promise.all(parts.map(async part => {
    const query = new URLSearchParams();
    for (const print of part) query.append('fingerprint', print);
    return (await apiClient.get<SameFileAnswer[]>(`/costs/invoices/same-file?${query.toString()}`)).data;
  }));
  return answers.flat();
}
