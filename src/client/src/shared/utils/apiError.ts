/** Достаёт человекочитаемое сообщение об ошибке из ответа axios/Error (единый хелпер). */
export function apiError(e: unknown, fallback = 'Ошибка'): string {
  const err = e as { response?: { data?: { error?: string; detail?: string } }; message?: string };
  return err?.response?.data?.error || err?.response?.data?.detail || err?.message || fallback;
}
