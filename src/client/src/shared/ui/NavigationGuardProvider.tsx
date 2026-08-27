import { useRef, useState, type ReactNode } from 'react';
import { Ctx, type NavGuard, type LeaveHandler } from './NavigationGuard';

/**
 * Router-agnostic гард навигации при несохранённых правках (issue #307). `<BrowserRouter>` (не
 * data-router) не поддерживает `useBlocker`, поэтому переходы перехватываются в самих ссылках
 * навигации (AppShell) через `attempt`, а страница-владелец показывает подтверждение. Один обработчик
 * за раз — под `<Outlet/>` смонтирована лишь одна страница.
 *
 * Отдельным файлом от хуков (issue #858): модуль, экспортирующий и компонент, и функции, теряет
 * горячую перезагрузку — правка компонента перезагружает страницу целиком вместо подмены на месте.
 * Хуки оставлены на прежнем месте, чтобы не перекраивать импорты по всему интерфейсу.
 */
export function NavigationGuardProvider({ children }: { children: ReactNode }) {
  const handlerRef = useRef<LeaveHandler | null>(null);
  /**
   * Значение контекста заводится РАЗ и живёт весь срок провайдера. Через `useState` с ленивым
   * инициализатором, а не `useRef(...).current`: чтение ref в рендере — обращение к изменяемому
   * ящику там, где рендер обязан быть чистым (issue #858). `useMemo` тут не годится вовсе — React
   * вправе забыть закэшированное, а подписчики держатся именно за эту ссылку.
   */
  const [value] = useState<NavGuard>(() => ({
    register: (h) => { handlerRef.current = h; },
    attempt: (proceed) => {
      if (handlerRef.current) { handlerRef.current(proceed); return true; }
      return false;
    },
  }));
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}
