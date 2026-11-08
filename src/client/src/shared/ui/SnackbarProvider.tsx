import * as RT from '@radix-ui/react-toast';
import { useCallback, useEffect, useMemo, useReducer, useRef, type ReactNode } from 'react';
import { Button, IconButton } from './Button';
import { Close } from './icons';
import { traceIdOf } from '@/shared/api/client';
import { apiError } from '@/shared/utils/apiError';
import { openBugReport } from './bugReportBus';
import {
  SNACKBAR_UNDO_MS, SnackbarCtx, type SnackbarApi, type SnackbarCloseReason, type SnackbarOptions, type SnackbarVariant,
} from './Snackbar';
import { EMPTY_QUEUE, SNACKBAR_EXIT_MS, snackbarKey, snackbarQueue, type SnackbarItem } from './snackbarQueue';

/**
 * Провайдер уведомлений и snackbar. Отдельным файлом от `useSnackbar` (issue #858): модуль,
 * экспортирующий и компонент, и хук, теряет горячую перезагрузку — правка компонента перезагружает
 * страницу целиком вместо подмены на месте. Хук живёт в `Snackbar.ts`: его зовут почти сто экранов.
 */

const DEFAULT_DURATION: Record<SnackbarVariant, number> = { success: 4000, info: 4000, error: 8000 };

/**
 * Срок показа. Уведомление с откликом держит отложенное действие, которое ещё можно отменить, поэтому без
 * своего срока живёт как «Вернуть» — 8 с, а не 4: забытый `duration` не должен укорачивать окно отмены.
 */
const durationOf = (item: SnackbarItem) =>
  item.duration ?? (item.onClose ? SNACKBAR_UNDO_MS : DEFAULT_DURATION[item.variant ?? 'info']);

/**
 * Отклик — код потребителя, и его сбой должен быть виден, но не ронять остальное: провайдер стоит выше
 * корневой границы ошибок (`App.tsx`), и исключение из эффекта размонтировало бы всё приложение, а
 * отклики прочих отброшенных не позвались бы вовсе.
 */
function callOnClose(item: SnackbarItem, reason: SnackbarCloseReason) {
  try {
    item.onClose?.(reason);
  } catch (e) {
    if (typeof globalThis.reportError === 'function') globalThis.reportError(e);
    else console.error(e);
  }
}

export function SnackbarProvider({ children }: { children: ReactNode }) {
  const [queue, dispatch] = useReducer(snackbarQueue, EMPTY_QUEUE);
  const idRef = useRef(0);

  const push = useCallback((o: SnackbarOptions) => {
    dispatch({ type: 'push', item: { ...o, id: ++idRef.current, at: Date.now(), key: snackbarKey(o) } });
  }, []);

  // Отклик закрытия — ровно один раз на уведомление ([UI-71]): Radix может сообщить о закрытии дважды,
  // а эффект отброшенных — повториться. Слабое множество: ушедшее из очереди уходит и отсюда.
  const closedRef = useRef(new WeakSet<SnackbarItem>());
  const finish = useCallback((item: SnackbarItem, reason: SnackbarCloseReason) => {
    if (!item.onClose || closedRef.current.has(item)) return;
    closedRef.current.add(item);
    callOnClose(item, reason);
  }, []);

  // Отброшенное очередью считается закрытым (Э5): копит его чистый редьюсер, отклик зовём здесь.
  const { shown, leaving, dropped } = queue;
  useEffect(() => {
    if (!dropped.length) return;
    for (const d of dropped) finish(d, 'dropped');
    dispatch({ type: 'flush', ids: dropped.map(d => d.id) });
  }, [dropped, finish]);

  useEffect(() => {
    if (!leaving) return;
    const t = setTimeout(() => dispatch({ type: 'next', now: Date.now() }), SNACKBAR_EXIT_MS);
    return () => clearTimeout(t);
  }, [leaving, shown?.id]);

  const api = useMemo<SnackbarApi>(() => ({
    success: (message, o) => push({ ...o, message, variant: 'success' }),
    error: (message, o) => push({ ...o, message, variant: 'error' }),
    info: (message, o) => push({ ...o, message, variant: 'info' }),
    apiError: (e, fallback, o) => push({
      ...o, message: apiError(e, fallback ?? 'Ошибка'), variant: 'error', traceId: traceIdOf(e),
    }),
  }), [push]);

  // Поле Radix — заново под каждое уведомление. Radix ставит срок на паузу, пока указатель или фокус в поле,
  // а снимает её уходом, который слушает, только пока в поле есть уведомление. Нажатое действие закрывает
  // уведомление под указателем: уход приходит, когда слушать уже некому, флаг паузы остаётся — и следующее
  // уведомление не закрывалось сроком, пока на него не наведут и не уведут (с ним висело и отложенное
  // удаление черновика, costs-invoice-screen-13-undo). Новое поле паузы не помнит; указатель над ним снова
  // ставит её первым же движением. Потомки — вне провайдера Radix: его ключ не пересоздаёт приложение.
  return (
    <SnackbarCtx.Provider value={api}>
      {children}
      <RT.Provider key={shown?.id} swipeDirection="right">
        {shown && (
          <SnackbarCard key={shown.id} item={shown} open={!leaving}
            onClose={reason => {
              // Сначала закрыть, потом отклик: уведомление, поставленное откликом, — новое, а не повтор.
              dispatch({ type: 'close', id: shown.id });
              finish(shown, reason);
            }} />
        )}
        {/* Рабочий стол — у ведущего края содержимого, правее рейла: отступ ставит оболочка
            (`--snackbar-inset`). Телефон — по центру над нижней навигацией и плавающей кнопкой
            (`--snackbar-lift`, `phoneLayout.ts`): у края уведомление закрывало бы разделы. */}
        <RT.Viewport className="fixed z-[100] bottom-4 left-[calc(var(--snackbar-inset,0px)+1rem)] flex flex-col items-start outline-none
          max-w-[min(560px,calc(100vw-var(--snackbar-inset,0px)-2rem))]
          max-md:left-4 max-md:right-4 max-md:max-w-none max-md:items-center max-md:bottom-[calc(1rem+var(--snackbar-lift,0px))]" />
      </RT.Provider>
    </SnackbarCtx.Provider>
  );
}

/**
 * Snackbar MD3 ([UI-71]): `inverse-surface`, `body-medium`, форма 4, уровень 3; 48 px в строку, 68 — в
 * две. Знака и цветной подложки нет ни у кого — ошибка отличается текстом, временем, «Закрыть» и тем,
 * что диктор читает её сразу. Действие одно: своё или «Сообщить». Внутри — роли `inverse-scope`:
 * text-кнопка становится `inverse-primary`, кнопка-иконка — `inverse-on-surface`.
 */
function SnackbarCard({ item, open, onClose }: {
  item: SnackbarItem; open: boolean; onClose: (reason: SnackbarCloseReason) => void;
}) {
  // Radix закрывает без причины, поэтому её ставим до него: обработчики свойств Radix зовёт раньше своих.
  // Не поставлена — значит, закрыл таймер: больше Radix сам не закрывает ничем.
  const reasonRef = useRef<SnackbarCloseReason | null>(null);
  const byHuman = () => { reasonRef.current = 'dismissed'; };
  const variant = item.variant ?? 'info';
  const error = variant === 'error';
  const action = item.action
    ?? (error && item.traceId
      ? { label: 'Сообщить', altText: 'Сообщить об ошибке', onClick: () => openBugReport({ origin: 'toast', received: item.message }) }
      : null);
  return (
    <RT.Root
      open={open}
      duration={durationOf(item)}
      // error — foreground (assertive/role=alert); успех/инфо — background (polite/role=status).
      type={error ? 'foreground' : 'background'}
      onOpenChange={o => { if (!o) onClose(reasonRef.current ?? 'timeout'); }}
      onSwipeEnd={byHuman}
      onEscapeKeyDown={byHuman}
      className={`inverse-scope flex items-center gap-1 min-h-12 py-1 pl-4 ${action || error ? 'pr-2' : 'pr-4'} max-w-full
        rounded-xs bg-inverse-surface text-inverse-on-surface shadow-elevation-3
        data-[state=open]:[animation:snackbar-in_var(--md-sys-motion-duration-short3)_var(--md-sys-motion-easing-standard-decelerate)]
        data-[state=closed]:[animation:snackbar-out_var(--md-sys-motion-duration-short3)_var(--md-sys-motion-easing-standard-accelerate)]
        data-[swipe=move]:translate-x-[var(--radix-toast-swipe-move-x)]
        data-[swipe=cancel]:translate-x-0 data-[swipe=cancel]:transition-transform data-[swipe=cancel]:ease-standard
        data-[swipe=end]:translate-x-[var(--radix-toast-swipe-end-x)]`}
    >
      <RT.Description className="flex-1 min-w-0 py-2.5 text-body-medium">{item.message}</RT.Description>
      {action && (
        <RT.Action altText={'altText' in action ? action.altText : action.label} asChild
          onClick={e => {
            // Щелчок, которым кончилось смахивание, Radix гасит `preventDefault`, но наш обработчик зовёт
            // первым: без проверки смахнутое «Вернуть» и закрылось бы `dismissed`, и сработало бы.
            if (e.defaultPrevented) return;
            action.onClick();
            // Причина — после действия: упавшее действие не закрывает уведомление, и срок не должен потом
            // прийти под видом «нажато».
            reasonRef.current = 'action';
          }}>
          <Button variant="text" className="shrink-0">{action.label}</Button>
        </RT.Action>
      )}
      {error && (
        <RT.Close asChild onClick={byHuman}>
          <IconButton label="Закрыть" icon={Close} />
        </RT.Close>
      )}
    </RT.Root>
  );
}
