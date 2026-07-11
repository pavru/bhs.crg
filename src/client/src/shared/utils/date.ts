import type { DatePrecision } from '@/shared/api/types';

/** Converts ISO date (YYYY-MM-DD) → Russian display format (DD.MM.YYYY). Passes through anything else. */
export function isoToRu(iso: string | null | undefined): string {
  if (!iso) return '';
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(iso);
  return m ? `${m[3]}.${m[2]}.${m[1]}` : iso;
}

/**
 * Форматирует ISO-дату для показа человеку с учётом точности типа (issue #60):
 * 'year' → «2026», 'month' → «07.2026», 'day'/по умолчанию → «01.07.2026».
 * Хранение всегда полный ISO; здесь лишь скрываем допоненные части. Непонятный вход — как есть.
 */
export function formatDateRu(iso: string | null | undefined, precision: DatePrecision = 'day'): string {
  if (!iso) return '';
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(iso);
  if (!m) return iso;
  const [, y, mo, d] = m;
  if (precision === 'year') return y;
  if (precision === 'month') return `${mo}.${y}`;
  return `${d}.${mo}.${y}`;
}

/** Converts Russian format (DD.MM.YYYY) → ISO (YYYY-MM-DD). Passes through incomplete/other strings. */
export function ruToISO(ru: string): string {
  const m = /^(\d{2})\.(\d{2})\.(\d{4})$/.exec(ru.trim());
  return m ? `${m[3]}-${m[2]}-${m[1]}` : ru;
}
