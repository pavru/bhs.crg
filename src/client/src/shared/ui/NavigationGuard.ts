import { createContext, useCallback, useContext, useRef, useEffect, useLayoutEffect, type MouseEvent } from 'react';
import { useLocation, useNavigate } from 'react-router';
import { refusedByDialog } from './modalDismissal';

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
 * Одна попытка ухода на всех — и на ссылку, и на команду (ревью PR #1313: правило жило двумя
 * копиями). Уход перехвачен, если открыто окно, которое держит человека, — оно покажет свой выход,
 * — либо если страница с правками взяла решение на себя.
 */
function intercepted(guard: NavGuard | null, leave: () => void): boolean {
  return refusedByDialog() || !!guard?.attempt(leave);
}

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
    if (intercepted(guard, () => navigate(to))) e.preventDefault();
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
 * <p>«Назад» и «Вперёд» браузера сторожит сама страница с правками — `useLeaveGuard`.</p>
 */
export function useGuardedLeave() {
  const guard = useNavigationGuard();
  return useCallback((leave: () => void) => { if (!intercepted(guard, leave)) leave(); }, [guard]);
}

/** Переход по адресу через охрану — замена `useNavigate` для обвязки приложения. */
export function useGuardedNavigate() {
  const leave = useGuardedLeave();
  const navigate = useNavigate();
  return useCallback((to: string) => leave(() => navigate(to)), [leave, navigate]);
}

/**
 * Переход к действию экрана (issue #1433, ревью PR #1478): на ДРУГОЙ экран — через охрану, как любой
 * уход; на ТОТ ЖЕ — без неё, и параметры действия ДОПИСЫВАЮТСЯ к нынешним. Со своего экрана никто не
 * уходит: открытая запись и форма с правками остаются на месте, и о правках спрашивает сама кнопка
 * экрана, как при обычном щелчке. Охрана здесь спросила бы первой, а кнопка — второй раз о том же; а
 * адрес без нынешних параметров закрыл бы открытую запись вместе с правками, не спросив вовсе.
 */
export function useGuardedIntent() {
  const navigate = useGuardedNavigate();
  const direct = useNavigate();
  const { pathname, search } = useLocation();
  return useCallback((to: string) => {
    const target = new URL(to, 'http://x');
    if (target.pathname !== pathname) { navigate(to); return; }
    const params = new URLSearchParams(search);
    target.searchParams.forEach((value, name) => params.set(name, value));
    direct(`${pathname}?${params}`);
  }, [navigate, direct, pathname, search]);
}

/**
 * Регистрирует гард ухода со страницы, пока `active` (есть несохранённые правки). `onLeave(proceed)`
 * должен показать подтверждение и вызвать `proceed()` при выборе «уйти»/«сохранить». Дополнительно —
 * `beforeunload` на перезагрузку/закрытие вкладки и шаг истории («Назад»/«Вперёд» браузера).
 * Перехват in-app переходов — в ссылках AppShell.
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
  // Неактивная охрана не регистрирует ничего — в том числе null (ревью PR #1388): эффекты потомка
  // идут раньше родительских, и «пустая» регистрация страницы при монтировании стирала бы охрану
  // вложенного редактора.
  useEffect(() => {
    if (!guard || !active) return;
    guard.register((proceed) => cbRef.current(proceed));
    return () => guard.register(null);
  }, [guard, active]);
  useEffect(() => {
    if (!active) return;
    const h = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = ''; };
    window.addEventListener('beforeunload', h);
    return () => window.removeEventListener('beforeunload', h);
  }, [active]);
  useHistoryStepGuard(active, cbRef);
}

/**
 * «Назад» и «Вперёд» браузера под охраной (ревью PR #1388). Окно редактора документа переживало
 * «Назад» с правками; страницей оно ушло бы молча — и так же уходили страницы счёта и накладной.
 *
 * <p>`useBlocker` без data-router нет, поэтому шаг истории перехватывается здесь, РАНЬШЕ роутера.
 * ⚠️ Раньше — значит РАНЬШЕ ЗАРЕГИСТРИРОВАН: у события на самом `window` слушатели идут в порядке
 * регистрации, и фаза перехвата не помогает (проверено живым прогоном — слушатель страницы, хоть и
 * в фазе перехвата, шёл после роутера). Поэтому слушатель один и вешается при загрузке модуля, до
 * того как смонтируется роутер, а страница с правками только подставляет ему обработчик.</p>
 *
 * <p>Роутер о шаге не узнаёт (`stopImmediatePropagation`), адрес возвращается тем же шагом назад
 * (`history.go(-step)`) — история не переписывается, и счётчик `idx` роутера остаётся верным. Уйти —
 * сделать тот же шаг ещё раз, уже мимо перехвата. Шаг меряется по `idx`, который роутер кладёт в
 * `history.state`; запись без него (чужая) не трогается.</p>
 */
let stepGuard: ((e: PopStateEvent) => void) | null = null;
let approvedStep = false;
if (typeof window !== 'undefined') {
  window.addEventListener('popstate', e => { if (!approvedStep) stepGuard?.(e); }, { capture: true });
}

/**
 * Шаг истории назад, о котором уже решено, — выход страницы «туда, откуда пришли» после сохранения
 * или ответа на вопрос. Охрана шага его пропускает: правки к этому моменту либо записаны, либо
 * брошены, а признак «есть правки» мог ещё не успеть погаснуть.
 */
export function stepBackApproved() {
  approvedStep = true;
  // Гаснет на этом же шаге, после охраны (её слушатель зарегистрирован первым): без охраны, то есть
  // без правок, флаг иначе дожил бы до чужого «Назад» и пропустил бы его.
  window.addEventListener('popstate', () => { approvedStep = false; }, { once: true });
  window.history.back();
}

function useHistoryStepGuard(active: boolean, onLeave: { current: LeaveHandler }) {
  // Запись истории, на которой стоит страница, — перечитывается каждой отрисовкой: переход роутера
  // по странице (параметры адреса) её перерисовывает. Не `useLocation`: охрана живёт и вне роутера
  // (тесты экранов), а читать надо всё равно `history.state` — его `idx` роутер наружу не отдаёт.
  const at = useRef<unknown>(null);
  useLayoutEffect(() => { at.current = (window.history.state as { idx?: unknown } | null)?.idx; });
  useEffect(() => {
    if (!active) return;
    let phase: 'watch' | 'restoring' | 'leaving' = 'watch';
    let step = 0;
    const onPop = (e: PopStateEvent) => {
      if (phase === 'leaving') { phase = 'watch'; return; }   // уход решён — шаг достаётся роутеру
      if (phase === 'restoring') {
        e.stopImmediatePropagation();
        phase = 'watch';
        onLeave.current(() => { phase = 'leaving'; window.history.go(step); });
        return;
      }
      const from = at.current;
      const to = (e.state as { idx?: unknown } | null)?.idx;
      if (typeof from !== 'number' || typeof to !== 'number' || to === from) return;
      e.stopImmediatePropagation();
      step = to - from;
      phase = 'restoring';
      window.history.go(-step);
    };
    stepGuard = onPop;
    return () => { if (stepGuard === onPop) stepGuard = null; };
  }, [active, onLeave]);
}
