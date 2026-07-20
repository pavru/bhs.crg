import { createContext, useContext, useRef, useEffect, type ReactNode, type MouseEvent } from 'react';
import { useNavigate } from 'react-router-dom';

/** Обработчик ухода: показывает подтверждение и вызывает `proceed()`, если пользователь решил уйти. */
type LeaveHandler = (proceed: () => void) => void;

interface NavGuard {
  /** Активная страница регистрирует свой обработчик ухода (или снимает — null). */
  register: (h: LeaveHandler | null) => void;
  /** Навигация вызывает перед переходом: есть обработчик → вызывает его (диалог) и возвращает true
   *  (переход заблокирован, решение за обработчиком); иначе false (уходить сразу). */
  attempt: (proceed: () => void) => boolean;
}

const Ctx = createContext<NavGuard | null>(null);

/**
 * Router-agnostic гард навигации при несохранённых правках (issue #307). `<BrowserRouter>` (не
 * data-router) не поддерживает `useBlocker`, поэтому переходы перехватываются в самих ссылках
 * навигации (AppShell) через `attempt`, а страница-владелец показывает подтверждение. Один обработчик
 * за раз — под `<Outlet/>` смонтирована лишь одна страница.
 */
export function NavigationGuardProvider({ children }: { children: ReactNode }) {
  const handlerRef = useRef<LeaveHandler | null>(null);
  const value = useRef<NavGuard>({
    register: (h) => { handlerRef.current = h; },
    attempt: (proceed) => {
      if (handlerRef.current) { handlerRef.current(proceed); return true; }
      return false;
    },
  }).current;
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

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
 * Регистрирует гард ухода со страницы, пока `active` (есть несохранённые правки). `onLeave(proceed)`
 * должен показать подтверждение и вызвать `proceed()` при выборе «уйти»/«сохранить». Дополнительно —
 * `beforeunload` на перезагрузку/закрытие вкладки. Перехват in-app переходов — в ссылках AppShell.
 */
export function useLeaveGuard(active: boolean, onLeave: LeaveHandler) {
  const guard = useNavigationGuard();
  const cbRef = useRef(onLeave);
  cbRef.current = onLeave;
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
