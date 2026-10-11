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
  | { kind: 'missing'; id: string }
  | { kind: 'unread'; id: string; error: unknown }
  | { kind: 'open'; id: string; data: T; gone: boolean; rereadFailed: unknown };

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
 * <p>⚠️ <b>Отказ доступа (403) — не обрыв связи</b> (ревью PR #1300): право чтения отозвали, пока документ
 * был открыт. Форма снимается, как до правки: данные, к которым доступа уже нет, на экране не остаются,
 * а «Повторить» отвечало бы тем же отказом. Набранное при этом теряется — сохранить его всё равно
 * было бы нельзя.</p>
 *
 * @param knownGone про выбранный документ уже узнано, что его нет: по одной последней ошибке судить
 *   нельзя — следующее перечитывание может кончиться обрывом связи, а не тем же 404. Удачное чтение
 *   это знание отменяет: документ вернули (восстановлением из копии), либо «нет такого» было чужим.
 */
export function openRecord<T>(selected: string | null, read: RecordRead<T>, knownGone: boolean): OpenRecord<T> {
  if (!selected) return { kind: 'none' };
  const id = selected;
  if (read.data == null) {
    if (read.isPending) return { kind: 'loading' };
    return isNotFound(read.error) ? { kind: 'missing', id } : { kind: 'unread', id, error: read.error };
  }
  if (read.isError && isForbidden(read.error)) return { kind: 'unread', id, error: read.error };

  const gone = read.isError && (knownGone || isNotFound(read.error));
  return { kind: 'open', id, data: read.data, gone, rereadFailed: !gone && read.isError ? read.error : null };
}

const isForbidden = (error: unknown): boolean =>
  (error as { response?: { status?: number } })?.response?.status === 403;

/** `openRecord` с памятью о том, что документа больше нет. */
export function useOpenRecord<T>(selected: string | null, read: RecordRead<T>): OpenRecord<T> {
  const [goneId, setGoneId] = useState<string | null>(null);
  const state = openRecord(selected, read, selected !== null && goneId === selected);
  if (state.kind === 'open' && state.gone && goneId !== selected) setGoneId(selected);
  // Прочитался — значит, есть: без этого однажды узнанное «нет такого» запирало бы вернувшийся документ
  // навсегда (ревью PR #1300).
  if (state.kind === 'open' && !state.gone && goneId === selected) setGoneId(null);
  return state;
}
