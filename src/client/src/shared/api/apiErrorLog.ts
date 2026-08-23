/**
 * Кольцевой буфер последних ошибок API (issue #834).
 *
 * Нужен потому, что пользователь сообщает об ошибке ПОСЛЕ того, как она случилась: тост уже погас,
 * идентификатор запроса он не перепечатает, а без идентификатора сообщение в логе `api` не найти.
 * Буфер даёт форме то, чего у человека нет, — адрес запроса, код ответа и `traceId`.
 *
 * Что сюда НЕ попадает: тела ответов и содержимое форм. В сообщении они оказались бы у
 * администратора, а после его правки — в публичном репозитории; для поиска в логе достаточно
 * идентификатора.
 */
export interface ApiErrorRecord {
  /** Время в местном формате — «12:31:05». Абсолютная дата не нужна: буфер живёт минуты. */
  at: string;
  method: string;
  /** Путь запроса без базового префикса — «/generate/…». */
  url: string;
  /** Код ответа; 0 — ответа не было вовсе (сеть, обрыв, отмена). */
  status: number;
  /** Идентификатор запроса: у 500 его присылает сервер, у прочих ответов его нет. */
  traceId?: string;
}

const CAPACITY = 10;
const buffer: ApiErrorRecord[] = [];

export function recordApiError(record: ApiErrorRecord): void {
  buffer.push(record);
  if (buffer.length > CAPACITY) buffer.splice(0, buffer.length - CAPACITY);
}

/** Копия буфера, новые последними. Копия, а не сам массив: форма не должна его править. */
export function recentApiErrors(): ApiErrorRecord[] {
  return [...buffer];
}

/** Последняя записанная ошибка — ею предзаполняется форма, открытая с тоста. */
export function lastApiError(): ApiErrorRecord | null {
  return buffer.length > 0 ? buffer[buffer.length - 1] : null;
}

/** Только для тестов: буфер модульный и переживает переходы между проверками. */
export function clearApiErrorLog(): void {
  buffer.length = 0;
}
