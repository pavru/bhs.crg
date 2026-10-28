import * as Dialog from '@radix-ui/react-dialog';
import { X } from 'lucide-react';
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode, type RefObject } from 'react';
import { Button, type ButtonProps } from './Button';
import {
  ModalControlContext, WindowStepContext, useModalControl,
  type WindowStepHost, type WindowStepSlots, type WindowStepState,
} from './modalControl';
import { DISMISSAL, modalState, pointAtExit } from './modalDismissal';
import { useHistoryLayer } from './historyLayers';

interface ModalBaseProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  /** Тело диалога. Функция-форма получает requestClose (охраняемый выход) — содержимое само
   *  рисует свою шапку/крестик (для full-screen top app bar). */
  children: ReactNode | ((requestClose: () => void) => ReactNode);
  /**
   * Размер — по содержимому, а не по вкусу (issue #1382, docs/UI.md «Окна»): малая форма, пикер,
   * просмотр записи — обычное окно; таблица, вставка, сопоставление колонок, список записей с
   * действиями, большая форма, лист скана — `wide`; редактор на весь экран — `fullScreen`. Третьего
   * размера нет: `extraWide` убран, а ширину окна изнутри (`min-w-[…]`) не подпирают — это ловит
   * храповик интерфейса.
   */
  wide?: boolean;
  /** Полноэкранный диалог-оверлей (на весь вьюпорт) — для крупных форм. Рендерится НАД
   *  текущей страницей (она остаётся смонтированной): закрытие возвращает в прежний контекст. */
  fullScreen?: boolean;
  /** Не рисовать заголовок по умолчанию (содержимое рисует собственный top app bar). Свою кнопку
   *  выхода такое содержимое помечает `data-modal-exit` — иначе Esc и клик мимо её не покажут. */
  headerless?: boolean;
  /** Не добавлять собственный скролл и паддинг тела — содержимое само управляет
   *  раскладкой (фиксированный футер, прокрутка только области полей). */
  flushBody?: boolean;
  /**
   * Зафиксированная нижняя область (кнопки действий). Заголовок сверху и футер снизу
   * не скроллятся; прокручивается только тело между ними.
   *
   * <p>«Отмену» ставят `ModalExit` или зовут из функции-формы `requestClose`: выход, позванный
   * мимо, не спросит о правках и останется живым при идущем запросе.</p>
   */
  footer?: ReactNode | ((requestClose: () => void) => ReactNode);
  /**
   * Идёт запрос (issue #1235): крестик, `ModalExit`, Esc и клик мимо мертвы, пока он не завершится —
   * закрытое окно спрятало бы исход. Расставлять `disabled` по выходам вызывающему не нужно.
   */
  busy?: boolean;
}

/**
 * Чем диалог закрывается, решает его состояние (см. `modalDismissal`). Лёгкий диалог не бывает ни
 * «грязным», ни результатом — это запрещено типом, чтобы пометку «терять нечего» нельзя было
 * поставить на форму «на всякий случай».
 */
type ModalDismissalProps =
  | {
      /** Закрыть в любой момент ничего не стоит: нет набранного, идущего запроса и ответа, который
       *  показывают один раз. Утверждение о содержимом, а не удобство — ревью вправе спросить «правда?». */
      nothingToLose: true;
      isDirty?: never;
      acknowledge?: never;
    }
  | {
      nothingToLose?: false;
      /** Есть несохранённые правки. Не передан вовсе — диалог сам заметит ввод в содержимом. */
      isDirty?: boolean;
      /**
       * Показан результат, который надо прочитать. Значение — подпись единственного выхода
       * («Понятно», «Готово»): диалог рисует его сам, крестика нет. Именем, а не флажком —
       * результат без названного выхода оказался бы окном без выхода.
       */
      acknowledge?: string;
    };

export type ModalProps = ModalBaseProps & ModalDismissalProps;

/**
 * На узком экране (< 640 px) окно любого размера — во весь экран (issue #1355, docs/UI.md «Узкий
 * экран»): плавающая карточка там оставляла бы по краям полосы в полтора пальца и резала бы поля.
 * Классами `max-sm:`, а не отдельной веткой: окно одно и то же, меняется только раскладка.
 */
const NARROW_FULL = 'max-sm:inset-0 max-sm:translate-x-0 max-sm:translate-y-0 max-sm:w-full max-sm:max-w-none ' +
  'max-sm:max-h-none max-sm:rounded-none max-sm:border-0';

/**
 * Содержимое живёт ровно пока диалог открыт (issue #858).
 *
 * <p>`Dialog.Root` остаётся смонтированным всегда — он и есть машина состояния Radix, и focus-scope
 * возвращает фокус на триггер при размонтировании контента. А вот подтверждение «закрыть без
 * сохранения?» держалось в состоянии САМОГО Modal, который переживает закрытие, — и стиралось
 * эффектом. Теперь оно заводится вместе с телом.</p>
 */
export function Modal(props: ModalProps) {
  return (
    <Dialog.Root open={props.open} onOpenChange={props.onOpenChange}>
      {props.open && <ModalBody {...props} />}
    </Dialog.Root>
  );
}

/**
 * Кнопка выхода из диалога — «Отмена» подвала (issue #1235). Идёт тем же путём, что крестик:
 * спросит о правках, при идущем запросе недоступна. Её же Esc и клик мимо показывают человеку.
 */
export function ModalExit({ children = 'Отмена', variant = 'text', disabled, ...rest }: Omit<ButtonProps, 'onClick' | 'type'>) {
  const { requestClose, busy } = useModalControl();
  return (
    <Button {...rest} type="button" variant={variant} data-modal-exit="" disabled={disabled}
      aria-disabled={busy || undefined} className={`${rest.className ?? ''} ${busy ? BUSY_EXIT : ''}`}
      onClick={requestClose}>
      {children}
    </Button>
  );
}

/**
 * Выход при идущем запросе: виден, но не нажимается. `aria-disabled`, а не `disabled` (ревью PR
 * #1310): настоящий `disabled` под фокусом — туда его ставит Esc — фокус теряет, браузер перестаёт
 * слать элементу клавиши, и ловушка Tab диалога выпускает курсор на страницу под окном.
 */
const BUSY_EXIT = 'opacity-40 pointer-events-none';

function ModalBody({
  onOpenChange, title, children, wide, fullScreen, headerless, isDirty, flushBody, footer,
  busy = false, nothingToLose, acknowledge,
}: ModalProps) {
  const [confirmClose, setConfirmClose] = useState(false);
  const [touched, setTouched] = useState(false);
  const content = useRef<HTMLDivElement>(null);
  const { host, top, touchTop, stepSlot } = useWindowSteps(content);

  // На шаге окно закрывается по правилам шага, а выход с него — назад, к форме окна.
  const state = top
    ? modalState({ busy: busy || top.busy, isDirty: top.isDirty, touched: top.touched })
    : modalState({ busy, acknowledge, isDirty, touched, nothingToLose });
  const leave = top ? top.back : () => onOpenChange(false);
  const rule = DISMISSAL[state];
  // Вопрос «Закрыть без сохранения?» живёт, только пока выход вообще возможен: начавшийся запрос
  // снимает его вместе с кнопкой «Закрыть» — иначе она закрыла бы окно мимо `busy`. А пока вопрос
  // показан, форма под ним недоступна (`inert`): фокус уходит в вопрос, и Enter не нажмёт
  // «Сохранить» под затемнением.
  const asking = confirmClose && rule.exit !== 'dead';
  // «Продолжить редактирование» возвращает человека туда, откуда он спросил: кнопка вопроса,
  // державшая фокус, исчезает, и без возврата он остался бы нигде.
  const askedFrom = useRef<HTMLElement | null>(null);
  useEffect(() => {
    if (asking) return;
    if (askedFrom.current?.isConnected) askedFrom.current.focus();
    askedFrom.current = null;
  }, [asking]);

  /** Охраняемый выход. `true` — окно закрывается (а не спросило, не вернулось с шага, не занято). */
  function requestClose() {
    if (rule.exit === 'dead') return false;
    if (rule.exit === 'ask') {
      askedFrom.current ??= document.activeElement as HTMLElement | null;
      setConfirmClose(true);
      return false;
    }
    leave();
    return !top;
  }

  // Системная «Назад» на телефоне (issue #1418) — тот же выход, что у крестика: с шага — к форме, с
  // вводом — вопрос. При показанном вопросе — «Продолжить редактирование», как Esc.
  useHistoryLayer(true, () => {
    if (!asking) return requestClose();
    setConfirmClose(false);
    return false;
  });

  /** Показать выход — см. `pointAtExit`. */
  const showExit = (focus: boolean) => pointAtExit(content.current, focus);

  /**
   * Ввод считается, только если поле лежит в ЭТОМ окне. События React всплывают по дереву
   * компонентов, а не по DOM: без проверки запрос поиска в пикере, открытом из формы, записал бы
   * «тронуто» самой форме.
   */
  function noteInput(e: { target: EventTarget }) {
    const field = e.target as Element;
    // Запрос поиска — не правка: его не сохраняют, и очищенное поле ничего не теряет. Поле поиска
    // называет себя само (`data-search`), иначе пикер с поиском переставал закрываться по Esc.
    if (!content.current?.contains(field) || field.closest?.('[data-search]')) return;
    // Набранное на шаге — его, а не формы окна: выход с шага теряет именно его.
    if (top) touchTop();
    else setTouched(true);
  }

  /**
   * Esc достаётся диалогу, только если им не распорядилось поле внутри.
   *
   * <p>Radix слушает клавишу на документе в фазе перехвата — РАНЬШЕ любого поля. Поэтому Esc,
   * которым человек закрывал подсказку редактора кода, очищал поиск или бросал строчную правку,
   * заодно доходил до диалога: закрывал окно или уводил фокус из поля на «Отмену». Договор обычный
   * для DOM: поле, которое клавишу обработало, останавливает её всплытие. Дошла до документа —
   * значит, она ничья, и отвечает диалог.</p>
   */
  function afterFields(act: () => void) {
    let reached = false;
    const seen = () => { reached = true; };
    document.addEventListener('keydown', seen, { once: true });
    setTimeout(() => {
      document.removeEventListener('keydown', seen);
      if (reached) act();
    }, 0);
  }

  return (
    <Dialog.Portal>
        <Dialog.Overlay
          className="fixed inset-0 z-40 bg-black/40"
          style={{ backdropFilter: 'blur(2px)' }}
          // Нажатие на затемнение фокус не забирает: иначе щелчок мимо выносил бы курсор из поля,
          // где человек набирал, — и окно, которое не закрылось, всё равно сбивало бы его с места.
          onMouseDown={e => e.preventDefault()}
        />
        <Dialog.Content
          ref={content}
          // Окно, которое Esc сейчас не закрывает, держит человека — см. `DialogHold`.
          data-modal-state={rule.escape === 'close' ? 'free' : state}
          className={fullScreen
            ? 'fixed inset-0 z-50 flex flex-col overflow-hidden focus:outline-none bg-surface-container-low'
            : `fixed left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 z-50 rounded-[28px] max-h-[90vh] flex flex-col overflow-hidden focus:outline-none bg-surface border border-outline-variant ${
              // У широкого — зазор по краям: между телефоном и 896 px (планшет, 768) иначе окно
              // ложилось бы в край экрана, скруглёнными углами вплотную (ревью PR #1386).
              wide ? 'w-[calc(100%-2rem)] max-w-4xl' : 'w-full max-w-lg'
            } ${NARROW_FULL}`}
          style={fullScreen ? undefined : { boxShadow: 'var(--md-sys-elevation-level3)' }}
          // «Тронуто»: первый же ввод внутри содержимого.
          // ⚠️ НЕ `onInputCapture`. React исполняет перехват `input` отдельным заходом, ДО того как
          // дойдёт до `onChange` поля, и сразу перерисовывает: управляемое поле получает прежнее
          // значение, а `onChange` уже не видит разницы и молчит — первый набранный знак пропадает
          // без единой ошибки (поймано живым прогоном на поле даты). `onChange` — событие
          // составное, его перехват идёт в одном заходе с обработчиком поля.
          onChangeCapture={noteInput}
          onInput={noteInput}
          // Начальный фокус — на само окно. Radix ставит его на первый фокусируемый элемент, а это
          // крестик: Enter сразу после открытия закрывал диалог. Поле с `autoFocus` сюда не доходит —
          // оно берёт фокус раньше, и Radix события не шлёт.
          onOpenAutoFocus={e => {
            e.preventDefault();
            content.current?.focus();
          }}
          onEscapeKeyDown={e => {
            // Решает диалог сам и ПОЗЖЕ — см. `afterFields`. Radix закрыл бы окно прямо сейчас.
            e.preventDefault();
            // Esc, которым отменяют набор в IME, — клавиша метода ввода, а не диалога.
            if (e.isComposing) return;
            // Esc при показанном подтверждении = «Продолжить редактирование» (закрыть подтверждение,
            // остаться в редакторе), а не закрыть/no-op.
            if (asking) {
              setConfirmClose(false);
              return;
            }
            if (rule.escape === 'close') afterFields(leave);
            else if (rule.escape === 'focusExit') afterFields(() => showExit(true));
          }}
          onPointerDownOutside={() => {
            if (!asking && rule.outside === 'hint') showExit(false);
          }}
          // Запрет — на `onInteractOutside`, а не на указателе: туда же приходит уход фокуса к
          // тостам и порталам, и закрывать диалог он не должен точно так же.
          // Промах на шаге не закрывает окно: Radix закрыл бы его целиком, а не вернул к форме.
          onInteractOutside={e => {
            if (rule.outside !== 'close' || top) e.preventDefault();
          }}
        >
          <ModalControlContext.Provider value={{ requestClose, busy: busy || !!top?.busy }}>
          <WindowStepContext.Provider value={host}>
          <Dialog.Title className="sr-only">{top?.title ?? title}</Dialog.Title>
          {(!headerless || top) && (
            <div className="flex items-center justify-between shrink-0 px-6 pt-6 pb-5" inert={asking}>
              {/* truncate: заголовком бывает имя объекта (материал в пикере документа качества
                  доходит до сотни знаков) — без обрезки оно выдавливает крестик за край. */}
              <span className="text-base font-semibold text-on-surface truncate" title={top?.title ?? title}>{top?.title ?? title}</span>
              {/* У результата выход один, и называет его диалог — крестика нет. При запросе крестик
                  виден, но недоступен: исчезнувший читался бы как поломка вёрстки. */}
              {!acknowledge && (
                <button
                  type="button"
                  onClick={requestClose}
                  aria-disabled={busy || undefined}
                  data-modal-cross=""
                  aria-label="Закрыть"
                  className={`flex items-center justify-center w-9 h-9 rounded-full transition-colors text-fg3 hover:text-on-surface hover:bg-black/5 dark:hover:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary ${busy ? BUSY_EXIT : ''}`}
                >
                  <X size={18} />
                </button>
              )}
            </div>
          )}
          {/* Форма окна под шагом остаётся смонтированной, только скрыта: выход с шага вернёт её
              такой, какой оставили. Тело и подвал шага ложатся в его места (`WindowStep`). */}
          {flushBody ? (
            <>
              <div className="flex-1 min-h-0 flex flex-col" inert={asking} hidden={!!top}>
                {typeof children === 'function' ? children(requestClose) : children}
              </div>
              {top && <div className="overflow-y-auto flex-1 px-6 pt-2 pb-4" inert={asking}>{stepSlot('body')}</div>}
            </>
          ) : (
            <div className={`overflow-y-auto flex-1 px-6 pt-2 ${footer || acknowledge || top ? 'pb-4' : 'pb-6'}`} inert={asking}>
              <div className="contents" hidden={!!top}>
                {typeof children === 'function' ? children(requestClose) : children}
              </div>
              {stepSlot('body')}
            </div>
          )}

          {(footer || acknowledge || top) && (
            <div className="shrink-0 px-6 py-3 border-t border-outline-variant bg-surface" inert={asking}>
              {acknowledge ? (
                <div className="flex items-center gap-2">
                  <div className="flex-1 min-w-0">{typeof footer === 'function' ? footer(requestClose) : footer}</div>
                  <ModalExit variant="filled">{acknowledge}</ModalExit>
                </div>
              ) : (
                <div className="contents" hidden={!!top}>{typeof footer === 'function' ? footer(requestClose) : footer}</div>
              )}
              {stepSlot('footer')}
            </div>
          )}

          {asking && (
            <div className={`absolute inset-0 z-10 flex items-center justify-center bg-black/40 ${fullScreen ? '' : 'rounded-[28px]'}`}>
              <div className="rounded-3xl p-5 w-80 bg-surface border border-outline-variant" style={{ boxShadow: 'var(--md-sys-elevation-level3)' }}>
                <p className="text-sm font-semibold mb-1 text-on-surface">
                  Закрыть без сохранения?
                </p>
                <p className="text-xs mb-4 text-fg3">
                  Несохранённые изменения будут потеряны.
                </p>
                <div className="flex gap-2 justify-end">
                  {/* Фокус — на безопасном ответе: Enter, которым человек пришёл сюда с «Отмены»,
                      повторным нажатием ничего не теряет. До «Закрыть» — один Tab. */}
                  <Button variant="text" size="sm" autoFocus onClick={() => setConfirmClose(false)}>
                    Продолжить редактирование
                  </Button>
                  <Button variant="filled" danger size="sm"
                    onClick={() => { setConfirmClose(false); leave(); }}>
                    Закрыть
                  </Button>
                </div>
              </div>
            </div>
          )}
          </WindowStepContext.Provider>
          </ModalControlContext.Provider>
        </Dialog.Content>
    </Dialog.Portal>
  );
}

type StepEntry = WindowStepState & { id: string; back: () => void; touched: boolean };

/**
 * Стопка шагов окна (issue #1384): верхний шаг виден, нижние и форма окна смонтированы и скрыты.
 * Под каждый шаг окно заводит свои места — тело и подвал, — и шаг рисует туда порталом: так его
 * состояние живёт там, где он отрисован, и шаг на шаге не стирает нижний.
 */
function useWindowSteps(content: RefObject<HTMLDivElement | null>) {
  const [steps, setSteps] = useState<StepEntry[]>([]);
  const [slots, setSlots] = useState<Record<string, WindowStepSlots>>({});
  // Откуда ушли на шаг — туда фокус и вернётся: кнопка, открывшая шаг, снова видна.
  const openers = useRef(new Map<string, HTMLElement | null>());
  const restore = useRef<HTMLElement | null>(null);

  const push = useCallback((id: string, back: () => void) => {
    openers.current.set(id, document.activeElement as HTMLElement | null);
    setSteps(s => s.some(x => x.id === id) ? s : [...s, { id, back, title: '', busy: false, touched: false }]);
  }, []);
  const update = useCallback((id: string, next: WindowStepState) => {
    setSteps(s => s.map(x => x.id === id ? { ...x, ...next } : x));
  }, []);
  const pop = useCallback((id: string) => {
    restore.current = openers.current.get(id) ?? null;
    openers.current.delete(id);
    setSteps(s => s.filter(x => x.id !== id));
    setSlots(s => { const rest = { ...s }; delete rest[id]; return rest; });
  }, []);
  const host = useMemo<WindowStepHost>(() => ({ push, update, pop, slots }), [push, update, pop, slots]);

  // Стабильные ref-колбэки мест: новый колбэк на каждой отрисовке звал бы setSlots без конца.
  const refs = useRef(new Map<string, (el: HTMLElement | null) => void>());
  function slotRef(id: string, kind: keyof WindowStepSlots) {
    const key = `${id}:${kind}`;
    let ref = refs.current.get(key);
    if (!ref) {
      ref = el => setSlots(s => s[id]?.[kind] === el || (!el && !s[id]) ? s : { ...s, [id]: { ...s[id], [kind]: el } });
      refs.current.set(key, ref);
    }
    return ref;
  }

  const top = steps.at(-1) ?? null;
  const topId = top?.id;
  const touchTop = () => {
    if (topId && !top?.touched) setSteps(s => s.map(x => x.id === topId ? { ...x, touched: true } : x));
  };
  useEffect(() => {
    // Ушли с шага — к кнопке, которая его открыла: она снова видна, будь она в форме окна или в шаге
    // под снятым.
    const back = restore.current;
    restore.current = null;
    if (back?.isConnected && !back.closest('[hidden]')) { back.focus(); return; }
    if (!topId) return;
    // Кнопка, открывшая шаг, скрылась вместе с формой, но браузер снимет с неё фокус не сразу —
    // поэтому «вне окна» мало: фокус в скрытом тоже не годится. Окно держит его, пока шаг не взял свой.
    const active = document.activeElement;
    if (!content.current?.contains(active) || active?.closest('[hidden]')) content.current?.focus();
  }, [topId, content]);

  const stepSlot = (kind: keyof WindowStepSlots) => steps.map(s => (
    <div key={s.id} ref={slotRef(s.id, kind)} className="contents" hidden={s.id !== topId} />
  ));
  return { host, top, touchTop, stepSlot };
}
