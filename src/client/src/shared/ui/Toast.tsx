import * as RT from '@radix-ui/react-toast';
import { createContext, useCallback, useContext, useMemo, useRef, useState, type ReactNode } from 'react';
import { CheckCircle2, AlertTriangle, Info, X, type LucideIcon } from 'lucide-react';

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
}

interface ToastItem extends ToastOptions { id: number }

interface ToastApi {
  toast: (o: ToastOptions) => void;
  success: (message: string, o?: Omit<ToastOptions, 'message' | 'variant'>) => void;
  error: (message: string, o?: Omit<ToastOptions, 'message' | 'variant'>) => void;
  info: (message: string, o?: Omit<ToastOptions, 'message' | 'variant'>) => void;
}

const ToastCtx = createContext<ToastApi | null>(null);

const MAX_VISIBLE = 3;
const DEFAULT_DURATION: Record<ToastVariant, number> = { success: 4000, info: 4000, error: 8000 };

const VARIANT_ICON: Record<ToastVariant, LucideIcon> = { success: CheckCircle2, error: AlertTriangle, info: Info };
const VARIANT_ICON_COLOR: Record<ToastVariant, string> = {
  success: 'text-success', error: 'text-danger', info: 'text-fg3',
};

export function ToastProvider({ children }: { children: ReactNode }) {
  const [items, setItems] = useState<ToastItem[]>([]);
  const idRef = useRef(0);

  const push = useCallback((o: ToastOptions) => {
    setItems(prev => [...prev, { ...o, id: ++idRef.current }].slice(-MAX_VISIBLE));
  }, []);
  const remove = useCallback((id: number) => setItems(prev => prev.filter(t => t.id !== id)), []);

  const api = useMemo<ToastApi>(() => ({
    toast: push,
    success: (message, o) => push({ ...o, message, variant: 'success' }),
    error: (message, o) => push({ ...o, message, variant: 'error' }),
    info: (message, o) => push({ ...o, message, variant: 'info' }),
  }), [push]);

  return (
    <ToastCtx.Provider value={api}>
      <RT.Provider swipeDirection="right">
        {children}
        {items.map(t => <ToastCard key={t.id} item={t} onRemove={() => remove(t.id)} />)}
        <RT.Viewport className="fixed bottom-4 right-4 z-[100] flex w-[380px] max-w-[calc(100vw-2rem)] flex-col gap-2 outline-none" />
      </RT.Provider>
    </ToastCtx.Provider>
  );
}

/** Доступ к тостам. Должен вызываться под `ToastProvider` (смонтирован в App). */
export function useToast(): ToastApi {
  const ctx = useContext(ToastCtx);
  if (!ctx) throw new Error('useToast должен использоваться внутри ToastProvider');
  return ctx;
}

function ToastCard({ item, onRemove }: { item: ToastItem; onRemove: () => void }) {
  const variant = item.variant ?? 'info';
  const Icon = VARIANT_ICON[variant];
  const bg = variant === 'error' ? 'bg-danger-subtle' : 'bg-surface';
  return (
    <RT.Root
      duration={item.duration ?? DEFAULT_DURATION[variant]}
      // error — foreground (assertive/role=alert); успех/инфо — background (polite/role=status).
      type={variant === 'error' ? 'foreground' : 'background'}
      onOpenChange={open => { if (!open) onRemove(); }}
      className={`flex items-start gap-2.5 rounded-lg border border-stroke ${bg} px-3.5 py-3 shadow-[var(--f-shadow16)]
        data-[state=open]:[animation:toast-in_150ms_ease-out]
        data-[swipe=move]:translate-x-[var(--radix-toast-swipe-move-x)]
        data-[swipe=cancel]:translate-x-0 data-[swipe=cancel]:transition-transform
        data-[swipe=end]:translate-x-[var(--radix-toast-swipe-end-x)]`}
    >
      <Icon size={18} className={`mt-0.5 shrink-0 ${VARIANT_ICON_COLOR[variant]}`} />
      <RT.Description className="flex-1 min-w-0 text-sm text-fg1">{item.message}</RT.Description>
      {item.action && (
        <RT.Action altText={item.action.label} asChild onClick={() => item.action!.onClick()}>
          <button type="button" className="shrink-0 text-sm font-medium text-brand hover:text-brand-hover transition-colors">
            {item.action.label}
          </button>
        </RT.Action>
      )}
      <RT.Close aria-label="Закрыть" className="shrink-0 text-fg4 hover:text-fg2 transition-colors">
        <X size={15} />
      </RT.Close>
    </RT.Root>
  );
}
