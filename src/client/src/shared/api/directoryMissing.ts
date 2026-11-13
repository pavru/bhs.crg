/**
 * Отказ «справочник не заведён» — общий для дверей ядра: сотрудников и видов работ ([CORE-20.4]).
 * Сервер отвечает `409 { code: "directory-missing", reason, error }`; причина у каждой двери своя
 * (у сотрудников — нет «Персоны», у видов работ — нет справочника единиц), код — один.
 *
 * Узнаётся по ПОЛЮ ответа, а не по словам: текст сервер волен переписать, а экран по нему решает,
 * какой выход предложить. Причину двери сужают до своих значений поверх этого разбора — второй
 * разборщик того же кода был бы копией и однажды разошёлся бы с первым.
 */
export interface DirectoryMissingRefusal {
  reason: string;
  error: string;
}

export const DIRECTORY_MISSING = 'directory-missing';

export function directoryMissingOf(e: unknown, fallback = 'Справочник не заведён.'): DirectoryMissingRefusal | null {
  const r = (e as { response?: { status?: number; data?: { code?: unknown; reason?: unknown; error?: unknown } } })?.response;
  if (r?.status !== 409 || r.data?.code !== DIRECTORY_MISSING) return null;
  return {
    reason: typeof r.data.reason === 'string' ? r.data.reason : '',
    error: typeof r.data.error === 'string' && r.data.error.trim() ? r.data.error : fallback,
  };
}
