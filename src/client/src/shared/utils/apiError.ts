/** Достаёт человекочитаемое сообщение об ошибке из ответа axios/Error (единый хелпер).
 *  Понимает обе формы тела 409/400: объект `{ error | detail }` и сырую строку
 *  (некоторые эндпоинты отдают `Results.Conflict(ex.Message)` без обёртки). */
export function apiError(e: unknown, fallback = 'Ошибка'): string {
  const err = e as { response?: { data?: unknown }; message?: string };
  const data = err?.response?.data;
  if (typeof data === 'string' && data.trim()) return data;
  if (data && typeof data === 'object') {
    const d = data as { error?: string; detail?: string };
    if (d.error) return d.error;
    if (d.detail) return d.detail;
  }
  return err?.message || fallback;
}

/** Отказ «состояние не допускает операцию» (409) — по ответу axios. Что именно не допускает, знает
 *  тот, кто спрашивает: у распознавания это ручная правка разбиения, у источника — чужая правка. */
export function isConflict(e: unknown): boolean {
  return (e as { response?: { status?: number } })?.response?.status === 409;
}

/**
 * Дожидается запроса, отдающего ФАЙЛ, и разворачивает тело отказа из блоба в объект — чтобы дальше
 * всё работало как с обычным ответом (`toast.apiError`, `apiError`). Исключение перебрасывается:
 * что показать, решает вызывающий.
 *
 * Нужно потому, что при `responseType: 'blob'` тело отказа тоже приходит блобом: разбор ошибок
 * видит на месте `{ error }` объект Blob, текста не находит и показывает «Request failed with
 * status code 409». То есть текст, написанный сервером для человека, теряется ровно там, где он
 * нужен, — нашло ревью PR #1058 на выгрузке источника с битым отбором. Асинхронность и есть
 * причина отдельной обёртки: `Blob.text()` ждут, а `apiError` зовут по месту показа.
 */
export async function withBlobErrorBody<T>(request: Promise<T>): Promise<T> {
  try {
    return await request;
  } catch (e) {
    const err = e as { response?: { data?: unknown } };
    const data = err?.response?.data;
    if (typeof Blob !== 'undefined' && data instanceof Blob) {
      const text = await data.text().catch(() => '');
      // JSON — наше тело отказа (с traceId), иначе отдаём текстом: сырую строку разбор тоже умеет.
      try { err.response!.data = JSON.parse(text); }
      catch { err.response!.data = text || undefined; }
    }
    throw e;
  }
}
