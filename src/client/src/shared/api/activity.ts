import { useQuery } from '@tanstack/react-query';
import { formatTime } from '@/shared/format/format';
import { apiClient } from './client';

/**
 * Журнал действий (issue #950, ТЗ CORE-28) — только чтение.
 *
 * Записывающих вызовов здесь нет и не будет: журнал пишет сервер там, где происходит само
 * действие. Возможность дописать запись из браузера означала бы журнал, в который можно положить
 * что угодно от чьего угодно имени.
 */

/**
 * Код канала действия (ТЗ CORE-28.1, issue #1336): `ui` — интерфейс и API, `mcp` — агент через MCP,
 * `system` — сам экземпляр. Его определяет сервер по контексту действия, а не писатель записи.
 *
 * Строкой, а не перечнем: каналы и их названия знает сервер (`useActivityChannels`), и новый канал не
 * обязан ждать правки клиента, чтобы попасть в отбор.
 */
export type ActivityChannel = string;

export interface ActivityChannelOption {
  code: ActivityChannel;
  title: string;
}

export interface ActivityRecord {
  id: string;
  occurredAt: string;
  /** Код действия — им же отбирают. */
  action: string;
  /** Название для человека; у записи с незнакомым кодом совпадает с кодом. */
  actionTitle: string;
  actorId: string | null;
  /** Имя автора на момент действия. Учётной записи может уже не быть — имя всё равно читается. */
  actorName: string;
  target: string | null;
  before: string | null;
  after: string | null;
  /** Каким путём сделано. У ряда — канал всей серии: другой канал рвёт сессию правки. */
  channel: ActivityChannel;
}

/**
 * Ряд журнала — серия записей одного вида одной сессии правки (ТЗ CORE-28.2): подряд идущие правки
 * одного объекта одним человеком. Запись, которая ни с чем не свернулась, — ряд из одной записи.
 *
 * Поля записи — от первой записи серии, кроме `target`: он от последней (цель могла переименоваться),
 * прежнее имя — в `firstTarget`. `before` и `after` уже слиты сервером по правилу вида.
 */
export interface ActivityRow extends ActivityRecord {
  /** Сколько записей в серии. */
  count: number;
  /** Время последней записи; у одиночной совпадает с `occurredAt`. */
  lastAt: string;
  /** Имя цели в начале серии — только если оно сменилось. */
  firstTarget: string | null;
  /** Ключ раскрытия — только у ряда, которому есть что раскрывать (`count > 1`). */
  sessionId: string | null;
}

export interface ActivityPage {
  /** Сколько РЯДОВ отвечает отбору: список показывает часть, и врать про конец нельзя. */
  total: number;
  items: ActivityRow[];
}

/** Раскрывается ли ряд в записи: у одиночного раскрывать нечего, и ключа сервер не отдаёт. */
export function isSeries(row: ActivityRow): row is ActivityRow & { sessionId: string } {
  return row.count > 1 && row.sessionId !== null;
}

/**
 * Подпись свёрнутого ряда: «×9 · 14:02–14:19». Время — по тем же правилам, что у колонки «Когда»;
 * совпавшие минуты не повторяются. У одиночного ряда подписи нет — `null`.
 */
export function seriesLabel(row: ActivityRow): string | null {
  if (!isSeries(row)) return null;
  const from = formatTime(row.occurredAt);
  const to = formatTime(row.lastAt);
  return `×${row.count} · ${from === to ? from : `${from}–${to}`}`;
}

export interface ActivityActionOption {
  code: string;
  title: string;
}

/** Страница журнала. `action` и `channel` — отборы; `null` — без отбора. */
export function useActivity(skip: number, take: number, action: string | null, channel: ActivityChannel | null = null) {
  return useQuery({
    queryKey: ['activity', skip, take, action, channel],
    queryFn: () => apiClient
      .get<ActivityPage>('/activity', {
        params: { skip, take, action: action || undefined, channel: channel || undefined },
      })
      .then(r => r.data),
  });
}

/**
 * Записи одного ряда (ТЗ CORE-28.2) — по времени. Читаются только раскрытым рядом: страница журнала
 * их не несёт, чтобы не возить сотню записей ради одной строки. Скрытая от читающего серия отвечает
 * так же, как несуществующая, — 404.
 *
 * `count` — число записей ряда, и оно в ключе нарочно: серия выросла — раскрытие перечитывается. Иначе
 * записи из кэша оказались бы короче свежего числа, и экран сказал бы «показана часть» там, где
 * сервер ничего не обрезал (ревью PR #1670).
 */
export function useActivitySeries(sessionId: string | null, action: string, count: number) {
  return useQuery({
    queryKey: ['activity', 'series', sessionId, action, count],
    queryFn: () => apiClient
      .get<ActivityRecord[]>(`/activity/series/${sessionId}`, { params: { action } })
      .then(r => r.data),
    enabled: sessionId !== null,
  });
}

/** Список действий для отбора — из каталога сервера, а не из того, что уже записано. */
export function useActivityActions() {
  return useQuery({
    queryKey: ['activity', 'actions'],
    queryFn: () => apiClient.get<ActivityActionOption[]>('/activity/actions').then(r => r.data),
    staleTime: Infinity,
  });
}

/** Каналы для отбора — с сервера, в порядке меню (ревью PR #1997: второй перечень в клиенте отставал бы). */
export function useActivityChannels() {
  return useQuery({
    queryKey: ['activity', 'channels'],
    queryFn: () => apiClient.get<ActivityChannelOption[]>('/activity/channels').then(r => r.data),
    staleTime: Infinity,
  });
}
