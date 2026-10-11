import { useState } from 'react';
import { isNotFound } from '@/shared/api/invoices';

/** Что страница знает о чтении выбранного документа — ровно то, что отдаёт `useQuery`. */
export interface RecordRead<T> {
  isPending: boolean;
  isError: boolean;
  data: T | undefined;
  error: unknown;
}

/**
 * Что показать на месте открытого документа (issue #1287). Пять положений, и путать их нельзя:
 *
 * - `none` — ничего не выбрано;
 * - `loading` — выбран, ещё читается;
 * - `missing` — прочитать не удалось ни разу, и ответ — «нет такого»: мёртвая ссылка;
 * - `unread` — прочитать не удалось ни разу по другой причине: отказ чтения, а не пустой документ;
 * - `open` — документ в руках. Он остаётся открытым и тогда, когда ПЕРЕчитать его не удалось:
 *   `gone` — перечитывание ответило «нет такого» (документ удалили, пока он был открыт, #1282),
 *   `rereadFailed` — перечитывание не удалось иначе (обрыв связи, отказ сервера).
 */
export type OpenRecord<T> =
  | { kind: 'none' }
  | { kind: 'loading' }
  | { kind: 'missing' }
  | { kind: 'unread'; error: unknown }
  | { kind: 'open'; data: T; gone: boolean; rereadFailed: unknown };

/**
 * Решить, что показать. Правило одно: <b>неудачное перечитывание форму не снимает</b>.
 *
 * <p>Раньше страница показывала форму, только пока чтение не в ошибке. Фоновое перечитывание — возврат в
 * окно, сброс кэша после отказа записи — при неудаче ставит ошибку, хотя документ на месте: форма
 * размонтировалась, и набранное пропадало молча. Минутный обрыв связи стоил человеку введённых строк.</p>
 *
 * <p>⚠️ Обратного хода нет: данных, которых не было, правило не подставляет. Документ, не прочитанный
 * ни разу, — это `unread` или `missing`, а не форма с пустыми полями («ещё не знаем» — не «пусто»).</p>
 *
 * @param knownGone про выбранный документ уже узнано, что его нет: по одной последней ошибке судить
 *   нельзя — следующее перечитывание может кончиться обрывом связи, а не тем же 404.
 */
export function openRecord<T>(selected: string | null, read: RecordRead<T>, knownGone: boolean): OpenRecord<T> {
  if (!selected) return { kind: 'none' };
  if (read.data == null) {
    if (read.isPending) return { kind: 'loading' };
    return isNotFound(read.error) ? { kind: 'missing' } : { kind: 'unread', error: read.error };
  }
  const gone = knownGone || isNotFound(read.error);
  return { kind: 'open', data: read.data, gone, rereadFailed: !gone && read.isError ? read.error : null };
}

/** `openRecord` с памятью о том, что документа больше нет. */
export function useOpenRecord<T>(selected: string | null, read: RecordRead<T>): OpenRecord<T> {
  const [goneId, setGoneId] = useState<string | null>(null);
  const state = openRecord(selected, read, selected !== null && goneId === selected);
  if (state.kind === 'open' && state.gone && goneId !== selected) setGoneId(selected);
  return state;
}
