import { createContext, useContext } from 'react';

/**
 * Эфемерные уведомления — snackbar MD3 (issue #281, [UI-71]) поверх `@radix-ui/react-toast`:
 * смахивание, пауза на наведении и фокусе, `aria-live` и время показа даёт Radix, очередь своя
 * (`snackbarQueue.ts`) — видно одно уведомление. НЕ для guard-отказов удаления (те в диалоге, #273) и
 * не дублирует контекстные места (flash у кнопки, кнопка активности). Инвариант: уведомление — для
 * результата, не видимого на текущем экране, или не-блокирующего сетевого сбоя без своего места.
 * Одно действие человека — одно уведомление: два следствия сводит в одну фразу вызывающий код.
 */
export type SnackbarVariant = 'success' | 'error' | 'info';

export interface SnackbarOptions {
  message: string;
  variant?: SnackbarVariant;
  /** Мс до авто-скрытия. По умолчанию: success/info 4с, error 8с. */
  duration?: number;
  /**
   * Одиночное действие (текст-кнопка справа) — напр. «Вернуться». Такое уведомление в очереди идёт
   * сразу за ошибками. Действие у уведомления одно: с ним «Сообщить» не показывается.
   */
  action?: { label: string; onClick: () => void };
  /**
   * Идентификатор запроса (issue #834). Есть — уведомление само предлагает «Сообщить»: оно живёт
   * секунды, а идентификатор из него никто не перепечатает. Ставится сервером только у 500, поэтому
   * кнопка не появляется на доменных отказах («укажите название») — сообщать о них нечего.
   */
  traceId?: string;
}

export interface SnackbarApi {
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
