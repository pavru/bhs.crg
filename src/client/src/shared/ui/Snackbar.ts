import { createContext, useContext } from 'react';

/**
 * Эфемерные уведомления (issue #281) поверх `@radix-ui/react-toast` — swipe-dismiss, пауза на
 * hover/focus, `aria-live`, очередь и таймеры даёт Radix. НЕ для guard-отказов удаления (те в
 * диалоге, #273) и не дублирует контекстные места (flash у кнопки, кнопка активности).
 * Инвариант: тост — для результата, не видимого на текущем экране, или не-блокирующего сетевого
 * сбоя без своего места.
 */
export type SnackbarVariant = 'success' | 'error' | 'info';

export interface SnackbarOptions {
  message: string;
  variant?: SnackbarVariant;
  /** Мс до авто-скрытия. По умолчанию: success/info 4с, error 8с. */
  duration?: number;
  /** Опциональное одиночное действие (текст-кнопка справа) — напр. «Перейти». */
  action?: { label: string; onClick: () => void };
  /**
   * Идентификатор запроса (issue #834). Есть — тост сам предлагает «Сообщить»: он живёт секунды, а
   * идентификатор из него никто не перепечатает. Ставится сервером только у 500, поэтому кнопка не
   * появляется на доменных отказах («укажите название») — сообщать о них нечего.
   */
  traceId?: string;
}

export interface SnackbarItem extends SnackbarOptions { id: number }

export interface SnackbarApi {
  toast: (o: SnackbarOptions) => void;
  success: (message: string, o?: Omit<SnackbarOptions, 'message' | 'variant'>) => void;
  error: (message: string, o?: Omit<SnackbarOptions, 'message' | 'variant'>) => void;
  info: (message: string, o?: Omit<SnackbarOptions, 'message' | 'variant'>) => void;
  /**
   * Ошибка ИЗ ОТВЕТА API: текст берётся из тела, идентификатор запроса — из структурного поля.
   * Отдельный метод, а не разбор внутри `error`, потому что `error` зовут и с придуманной строкой,
   * и подсовывать ей объект ошибки значило бы гадать, что пришло.
   */
  apiError: (e: unknown, fallback?: string, o?: Omit<SnackbarOptions, 'message' | 'variant'>) => void;
}

export const SnackbarCtx = createContext<SnackbarApi | null>(null);

/** Доступ к уведомлениям. Должен вызываться под `SnackbarProvider` (смонтирован в App). */
export function useSnackbar(): SnackbarApi {
  const ctx = useContext(SnackbarCtx);
  if (!ctx) throw new Error('useSnackbar должен использоваться внутри SnackbarProvider');
  return ctx;
}
