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
