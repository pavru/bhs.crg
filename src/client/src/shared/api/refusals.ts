/**
 * Отказ сервера, разобранный для показа человеку.
 *
 * Тело отказа у нас одно на все адреса: `{ error, traceId, details }` (см. `ApiErrorMapping` и
 * обработчик в `Program.cs`). `error` — текст, написанный для пользователя и доезжающий дословно;
 * `details` — МЕСТА, на которые отказ указывает (issue #957): путь в данных, код и сообщение.
 *
 * Адреса нужны там, где нарушений бывает несколько и лежат они внутри структуры: отказ без адреса
 * превращается в баннер, по которому неверное значение внутри строки таблицы не найти — её ещё надо
 * догадаться открыть.
 *
 * ⚠️ Здесь ТОЛЬКО адреса. Текст отказа берёт общий `apiError` — он был раньше, и пятая формулировка
 * одного и того же рядом с ним никому не нужна (issue #1008).
 */

/** Место, на которое указывает отказ. Зеркало серверного `RefusalDetail`. */
export interface RefusalDetail {
  code: string;
  path: string;
  message: string;
}

interface RefusalBody {
  response?: { data?: { error?: string; details?: RefusalDetail[] | null } };
}

/** Места нарушений: путь → сообщение. Пусто — отказ без адресов (их называет большинство). */
export function refusalIssues(e: unknown): Record<string, string> {
  const details = (e as RefusalBody)?.response?.data?.details;
  if (!details?.length) return {};
  const byPath: Record<string, string> = {};
  // Одно сообщение на путь: у поля показывается одна подпись, и склеивать их в неё значит делать
  // нечитаемой самую частую, одиночную, ошибку.
  for (const d of details) byPath[d.path] ??= d.message;
  return byPath;
}
