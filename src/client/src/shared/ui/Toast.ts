import { createContext, useContext } from 'react';

/**
 * Эфемерные уведомления (issue #281) поверх `@radix-ui/react-toast` — swipe-dismiss, пауза на
 * hover/focus, `aria-live`, очередь и таймеры даёт Radix. НЕ для guard-отказов удаления (те в
 * диалоге, #273) и не дублирует контекстные места (flash у кнопки, колокольчик фон-задач).
 * Инвариант: тост — для результата, не видимого на текущем экране, или не-блокирующего сетевого
 * сбоя без своего места.
 */
export type ToastVariant = 'success' | 'error' | 'info';

export interface ToastOptions {
  message: string;
  variant?: ToastVariant;
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

export interface ToastItem extends ToastOptions { id: number }

export interface ToastApi {
  toast: (o: ToastOptions) => void;
  success: (message: string, o?: Omit<ToastOptions, 'message' | 'variant'>) => void;
  error: (message: string, o?: Omit<ToastOptions, 'message' | 'variant'>) => void;
  info: (message: string, o?: Omit<ToastOptions, 'message' | 'variant'>) => void;
  /**
   * Ошибка ИЗ ОТВЕТА API: текст берётся из тела, идентификатор запроса — из структурного поля.
   * Отдельный метод, а не разбор внутри `error`, потому что `error` зовут и с придуманной строкой,
   * и подсовывать ей объект ошибки значило бы гадать, что пришло.
   */
  apiError: (e: unknown, fallback?: string, o?: Omit<ToastOptions, 'message' | 'variant'>) => void;
}

export const ToastCtx = createContext<ToastApi | null>(null);

/** Доступ к тостам. Должен вызываться под `ToastProvider` (смонтирован в App). */
export function useToast(): ToastApi {
  const ctx = useContext(ToastCtx);
  if (!ctx) throw new Error('useToast должен использоваться внутри ToastProvider');
  return ctx;
}
