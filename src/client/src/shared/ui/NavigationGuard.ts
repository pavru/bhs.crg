import { createContext, useCallback, useContext, useRef, useEffect, useLayoutEffect, type MouseEvent } from 'react';
import { useNavigate } from 'react-router';

/** Обработчик ухода: показывает подтверждение и вызывает `proceed()`, если пользователь решил уйти. */
export type LeaveHandler = (proceed: () => void) => void;

export interface NavGuard {
  /** Активная страница регистрирует свой обработчик ухода (или снимает — null). */
  register: (h: LeaveHandler | null) => void;
  /** Навигация вызывает перед переходом: есть обработчик → вызывает его (диалог) и возвращает true
   *  (переход заблокирован, решение за обработчиком); иначе false (уходить сразу). */
  attempt: (proceed: () => void) => boolean;
}

export const Ctx = createContext<NavGuard | null>(null);

export function useNavigationGuard() { return useContext(Ctx); }

/**
 * onClick-обработчик для ссылок навигации (NavLink/Link): при активном гарде отменяет мгновенный
 * переход и отдаёт решение странице-владельцу; иначе — обычная навигация. Modifier-клики (Ctrl/⌘/Shift
 * — открыть в новой вкладке) и не-левый клик не перехватываются.
 */
export function useGuardedNavClick() {
  const guard = useNavigationGuard();
  const navigate = useNavigate();
  return (to: string) => (e: MouseEvent) => {
    if (e.defaultPrevented || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey || e.button !== 0) return;
    if (guard && guard.attempt(() => navigate(to))) e.preventDefault();
  };
}

/**
 * Уход со страницы не ссылкой (issue #1238): командой палитры, кнопкой уведомления, выходом из
 * учётной записи. Под охраной действие достаётся странице-владельцу — она спросит о несохранённом
 * и исполнит его сама; без охраны исполняется сразу.
 *
 * <p>Охрана живёт в ссылках, а не в маршрутизаторе (`useBlocker` без data-router недоступен), и
 * потому видит только то, что её позвало: прямой `navigate(…)` из обвязки приложения уводил со
 * счёта с правками молча. Обвязке — всему, что нарисовано вокруг любой страницы, — `useNavigate` и
 * голый `<Link>` запрещены; сверяет `chromeLeavesThroughGuard.test.ts`.</p>
 *
 * <p>Кнопку «Назад» браузера охрана не видит по построению — это отдельное решение.</p>
 */
export function useGuardedLeave() {
  const guard = useNavigationGuard();
  return useCallback((leave: () => void) => { if (!guard?.attempt(leave)) leave(); }, [guard]);
}

/** Переход по адресу через охрану — замена `useNavigate` для обвязки приложения. */
export function useGuardedNavigate() {
  const leave = useGuardedLeave();
  const navigate = useNavigate();
  return useCallback((to: string) => leave(() => navigate(to)), [leave, navigate]);
}

/**
 * Регистрирует гард ухода со страницы, пока `active` (есть несохранённые правки). `onLeave(proceed)`
 * должен показать подтверждение и вызвать `proceed()` при выборе «уйти»/«сохранить». Дополнительно —
 * `beforeunload` на перезагрузку/закрытие вкладки. Перехват in-app переходов — в ссылках AppShell.
 */
export function useLeaveGuard(active: boolean, onLeave: LeaveHandler) {
  const guard = useNavigationGuard();
  const cbRef = useRef(onLeave);
  /**
   * Свежий колбэк для отложенного вызова. Обновляется ЭФФЕКТОМ, а не присваиванием в рендере
   * (issue #858): рендер обязан быть чистым, а запись в ref — побочное действие, которое к тому же
   * случалось бы и на брошенном рендере (StrictMode, прерванный конкурентный проход).
   *
   * Эффект именно СЛОЙНЫЙ (useLayoutEffect). Обычный отложен до после отрисовки, и между коммитом
   * и его срабатыванием ref держит ПРЕДЫДУЩИЙ колбэк — клик по ссылке, попавший в эту щель, показал
   * бы диалог ухода с устаревшим замыканием. Слойный эффект выполняется в том же задании, что и
   * коммит: события в промежуток не попадают, щели нет.
   */
  useLayoutEffect(() => { cbRef.current = onLeave; });
  useEffect(() => {
    if (!guard) return;
    guard.register(active ? (proceed) => cbRef.current(proceed) : null);
    return () => guard.register(null);
  }, [guard, active]);
  useEffect(() => {
    if (!active) return;
    const h = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = ''; };
    window.addEventListener('beforeunload', h);
    return () => window.removeEventListener('beforeunload', h);
  }, [active]);
}
