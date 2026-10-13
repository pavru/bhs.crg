import * as Dialog from '@radix-ui/react-dialog';
import { X } from 'lucide-react';
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { Button, type ButtonProps } from './Button';
import { ModalControlContext, useModalControl } from './modalControl';
import { DISMISSAL, HINT_MS, modalState } from './modalDismissal';

interface ModalBaseProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  /** Тело диалога. Функция-форма получает requestClose (охраняемый выход) — содержимое само
   *  рисует свою шапку/крестик (для full-screen top app bar). */
  children: ReactNode | ((requestClose: () => void) => ReactNode);
  wide?: boolean;
  extraWide?: boolean;
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
  onOpenChange, title, children, wide, extraWide, fullScreen, headerless, isDirty, flushBody, footer,
  busy = false, nothingToLose, acknowledge,
}: ModalProps) {
  const [confirmClose, setConfirmClose] = useState(false);
  const [touched, setTouched] = useState(false);
  const content = useRef<HTMLDivElement>(null);
  const hintTimer = useRef<ReturnType<typeof setTimeout>>(undefined);
  const hinted = useRef<HTMLElement | null>(null);
  useEffect(() => () => clearTimeout(hintTimer.current), []);

  const rule = DISMISSAL[modalState({ busy, acknowledge, isDirty, touched, nothingToLose })];
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

  function requestClose() {
    if (rule.exit === 'dead') return;
    if (rule.exit === 'ask') {
      askedFrom.current ??= document.activeElement as HTMLElement | null;
      setConfirmClose(true);
    }
    else onOpenChange(false);
  }

  /**
   * Показать выход: «Отмена» (кнопка, помеченная `data-modal-exit`), иначе крестик. Кольцом, без
   * движения: тишина неотличима от зависшего окна, а встряхивание читается как ошибка.
   *
   * @param focus Esc ещё и переносит туда фокус — закрывает уже Enter. Клик мимо фокус не трогает:
   *   уведённый из поля щелчком, он превратил бы следующий пробел в «Отмену».
   */
  function showExit(focus: boolean) {
    const exit = content.current?.querySelector<HTMLElement>('[data-modal-exit]:not([disabled])')
      ?? content.current?.querySelector<HTMLElement>('[data-modal-cross]');
    if (!exit) return;
    exit.scrollIntoView?.({ block: 'nearest' });
    if (focus) exit.focus();
    // Кольцо снимается с ТОГО элемента, на который легло: выход между двумя промахами мог
    // смениться («Отмена» стала недоступна — остался крестик), а таймер один.
    clearTimeout(hintTimer.current);
    hinted.current?.removeAttribute('data-modal-hint');
    hinted.current = exit;
    exit.setAttribute('data-modal-hint', '');
    hintTimer.current = setTimeout(() => exit.removeAttribute('data-modal-hint'), HINT_MS);
  }

  /**
   * Ввод считается, только если поле лежит в ЭТОМ окне. События React всплывают по дереву
   * компонентов, а не по DOM: без проверки запрос поиска в пикере, открытом из формы, записал бы
   * «тронуто» самой форме.
   */
  function noteInput(e: { target: EventTarget }) {
    const field = e.target as Element;
    // Запрос поиска — не правка: его не сохраняют, и очищенное поле ничего не теряет. Поле поиска
    // называет себя само (`data-search`), иначе пикер с поиском переставал закрываться по Esc.
    if (content.current?.contains(field) && !field.closest?.('[data-search]')) setTouched(true);
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
          className={fullScreen
            ? 'fixed inset-0 z-50 flex flex-col overflow-hidden focus:outline-none bg-base'
            : `fixed left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 z-50 rounded-[28px] max-h-[90vh] flex flex-col overflow-hidden focus:outline-none bg-surface border border-stroke ${
              extraWide ? 'w-full max-w-5xl' : wide ? 'w-full max-w-2xl' : 'w-full max-w-lg'
            }`}
          style={fullScreen ? undefined : { boxShadow: 'var(--f-shadow28)' }}
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
            if (rule.escape === 'close') afterFields(() => onOpenChange(false));
            else if (rule.escape === 'focusExit') afterFields(() => showExit(true));
          }}
          onPointerDownOutside={() => {
            if (!asking && rule.outside === 'hint') showExit(false);
          }}
          // Запрет — на `onInteractOutside`, а не на указателе: туда же приходит уход фокуса к
          // тостам и порталам, и закрывать диалог он не должен точно так же.
          onInteractOutside={e => {
            if (rule.outside !== 'close') e.preventDefault();
          }}
        >
          <ModalControlContext.Provider value={{ requestClose, busy }}>
          <Dialog.Title className="sr-only">{title}</Dialog.Title>
          {!headerless && (
            <div className="flex items-center justify-between shrink-0 px-6 pt-6 pb-5" inert={asking}>
              {/* truncate: заголовком бывает имя объекта (материал в пикере документа качества
                  доходит до сотни знаков) — без обрезки оно выдавливает крестик за край. */}
              <span className="text-base font-semibold text-fg1 truncate" title={title}>{title}</span>
              {/* У результата выход один, и называет его диалог — крестика нет. При запросе крестик
                  виден, но недоступен: исчезнувший читался бы как поломка вёрстки. */}
              {!acknowledge && (
                <button
                  type="button"
                  onClick={requestClose}
                  aria-disabled={busy || undefined}
                  data-modal-cross=""
                  aria-label="Закрыть"
                  className={`flex items-center justify-center w-9 h-9 rounded-full transition-colors text-fg3 hover:text-fg1 hover:bg-black/5 dark:hover:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand ${busy ? BUSY_EXIT : ''}`}
                >
                  <X size={18} />
                </button>
              )}
            </div>
          )}
          {flushBody ? (
            <div className="flex-1 min-h-0 flex flex-col" inert={asking}>
              {typeof children === 'function' ? children(requestClose) : children}
            </div>
          ) : (
            <div className={`overflow-y-auto flex-1 px-6 pt-2 ${footer || acknowledge ? 'pb-4' : 'pb-6'}`} inert={asking}>
              {typeof children === 'function' ? children(requestClose) : children}
            </div>
          )}

          {(footer || acknowledge) && (
            <div className="shrink-0 px-6 py-3 border-t border-stroke bg-surface" inert={asking}>
              {acknowledge ? (
                <div className="flex items-center gap-2">
                  <div className="flex-1 min-w-0">{typeof footer === 'function' ? footer(requestClose) : footer}</div>
                  <ModalExit variant="filled">{acknowledge}</ModalExit>
                </div>
              ) : typeof footer === 'function' ? footer(requestClose) : footer}
            </div>
          )}

          {asking && (
            <div className={`absolute inset-0 z-10 flex items-center justify-center bg-black/40 ${fullScreen ? '' : 'rounded-[28px]'}`}>
              <div className="rounded-3xl p-5 w-80 bg-surface border border-stroke" style={{ boxShadow: 'var(--f-shadow28)' }}>
                <p className="text-sm font-semibold mb-1 text-fg1">
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
                    onClick={() => { setConfirmClose(false); onOpenChange(false); }}>
                    Закрыть
                  </Button>
                </div>
              </div>
            </div>
          )}
          </ModalControlContext.Provider>
        </Dialog.Content>
    </Dialog.Portal>
  );
}
