import * as RT from '@radix-ui/react-toast';
import { useCallback, useMemo, useRef, useState, type ReactNode } from 'react';
import { Icon } from './Icon';
import { CheckCircle, Close, Info, Warning, type IconDef } from './icons';
import { traceIdOf } from '@/shared/api/client';
import { apiError } from '@/shared/utils/apiError';
import { openBugReport } from './bugReportBus';
import { ToastCtx, type ToastApi, type ToastItem, type ToastOptions, type ToastVariant } from './Toast';

/**
 * Провайдер тостов и карточка. Отдельным файлом от `useToast` (issue #858): модуль, экспортирующий
 * и компонент, и хук, теряет горячую перезагрузку — правка компонента перезагружает страницу целиком
 * вместо подмены на месте. Хук оставлен на прежнем месте: его зовут два десятка экранов.
 */

const MAX_VISIBLE = 3;
const DEFAULT_DURATION: Record<ToastVariant, number> = { success: 4000, info: 4000, error: 8000 };

const VARIANT_ICON: Record<ToastVariant, IconDef> = { success: CheckCircle, error: Warning, info: Info };
const VARIANT_ICON_COLOR: Record<ToastVariant, string> = {
  success: 'text-success', error: 'text-error', info: 'text-on-surface-variant',
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
    apiError: (e, fallback, o) => push({
      ...o, message: apiError(e, fallback ?? 'Ошибка'), variant: 'error', traceId: traceIdOf(e),
    }),
  }), [push]);

  return (
    <ToastCtx.Provider value={api}>
      <RT.Provider swipeDirection="right">
        {children}
        {items.map(t => <ToastCard key={t.id} item={t} onRemove={() => remove(t.id)} />)}
        {/* На телефоне — над нижней навигацией и плавающей кнопкой (#1434), если они есть: подъём
            ставит оболочка (`--toast-lift`, `phoneLayout.ts`). У края тост закрывал бы разделы. */}
        <RT.Viewport className="fixed bottom-4 max-md:bottom-[calc(1rem+var(--toast-lift,0px))] right-4 z-[100] flex w-[380px] max-w-[calc(100vw-2rem)] flex-col gap-2 outline-none" />
      </RT.Provider>
    </ToastCtx.Provider>
  );
}

function ToastCard({ item, onRemove }: { item: ToastItem; onRemove: () => void }) {
  const variant = item.variant ?? 'info';
  const icon = VARIANT_ICON[variant];
  const bg = variant === 'error' ? 'bg-error-container' : 'bg-surface';
  return (
    <RT.Root
      duration={item.duration ?? DEFAULT_DURATION[variant]}
      // error — foreground (assertive/role=alert); успех/инфо — background (polite/role=status).
      type={variant === 'error' ? 'foreground' : 'background'}
      onOpenChange={open => { if (!open) onRemove(); }}
      className={`flex items-start gap-2.5 rounded-lg border border-outline-variant ${bg} px-3.5 py-3 shadow-[var(--md-sys-elevation-level2)]
        data-[state=open]:[animation:toast-in_var(--md-sys-motion-duration-short3)_var(--md-sys-motion-easing-standard-decelerate)]
        data-[swipe=move]:translate-x-[var(--radix-toast-swipe-move-x)]
        data-[swipe=cancel]:translate-x-0 data-[swipe=cancel]:transition-transform data-[swipe=cancel]:ease-standard
        data-[swipe=end]:translate-x-[var(--radix-toast-swipe-end-x)]`}
    >
      <Icon icon={icon} size={18} className={`mt-0.5 shrink-0 ${VARIANT_ICON_COLOR[variant]}`} />
      <RT.Description className="flex-1 min-w-0 text-sm text-on-surface">{item.message}</RT.Description>
      {!item.action && item.traceId && (
        <RT.Action altText="Сообщить об ошибке" asChild
          onClick={() => openBugReport({ origin: 'toast', received: item.message })}>
          <button type="button" className="shrink-0 -my-1 px-2 py-1 rounded-full text-sm font-medium text-primary state-layer focus-ring">
            Сообщить
          </button>
        </RT.Action>
      )}
      {item.action && (
        <RT.Action altText={item.action.label} asChild onClick={() => item.action!.onClick()}>
          <button type="button" className="shrink-0 -my-1 px-2 py-1 rounded-full text-sm font-medium text-primary state-layer focus-ring">
            {item.action.label}
          </button>
        </RT.Action>
      )}
      <RT.Close aria-label="Закрыть" className="shrink-0 rounded-full text-on-surface-variant state-layer focus-ring">
        <Icon icon={Close} size={18} />
      </RT.Close>
    </RT.Root>
  );
}
