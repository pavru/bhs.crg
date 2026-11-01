import * as Dialog from '@radix-ui/react-dialog';
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { Button, type ButtonProps } from './Button';
import { ModalControlContext, WindowStepContext, useModalControl } from './modalControl';
import { DISMISSAL, modalState, pointAtExit } from './modalDismissal';
import { useHistoryLayer } from './historyLayers';
import { focusFromLayer } from './layerFocus';
import { DIALOG_SCRIM, DIALOG_SURFACE, DIALOG_TITLE } from './dialogSurface';
import { LeaveQuestion } from './LeaveQuestion';
import { useScrollEdges } from './useScrollEdges';
import { useWindowSteps } from './windowSteps';

interface ModalBaseProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  /** Тело диалога. Функция-форма получает requestClose (охраняемый выход) — содержимое само
   *  рисует свою шапку (для full-screen top app bar). */
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
   *  раскладкой (фиксированный футер, прокрутка только области полей). Свой выход такое содержимое
   *  ставит само — `ModalExit`: подвала «Закрыть» окно ему не добавляет. */
  flushBody?: boolean;
  /**
   * Зафиксированная нижняя область (кнопки действий). Заголовок сверху и футер снизу
   * не скроллятся; прокручивается только тело между ними. Не передан — окно ставит подвал с «Закрыть»
   * само: крестика у окна нет ([UI-75]), и выход должен быть виден.
   *
   * <p>«Отмену» ставят `ModalExit` или зовут из функции-формы `requestClose`: выход, позванный
   * мимо, не спросит о правках и останется живым при идущем запросе.</p>
   */
  footer?: ReactNode | ((requestClose: () => void) => ReactNode);
  /**
   * Идёт запрос (issue #1235): `ModalExit`, Esc и клик мимо мертвы, пока он не завершится —
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
       * («Понятно», «Готово»): диалог рисует его сам. Именем, а не флажком — результат без названного
       * выхода оказался бы окном без выхода.
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
  'max-sm:max-h-none max-sm:rounded-none';

/** Черта под шапкой и над подвалом — только когда содержимое ушло под них ([UI-75]). Цвет, а не сама
 *  черта: появляясь, она не сдвигает тело на пиксель. */
const edge = (shown: boolean) => shown ? 'border-outline-variant' : 'border-transparent';

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
 * Кнопка выхода из диалога — «Отмена» подвала (issue #1235). Крестика у окна нет ([UI-75]): это и есть
 * выход окна. Спросит о правках, при идущем запросе недоступна. Её же Esc и клик мимо показывают человеку.
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
 * слать элементу клавиши, и ловушка Tab диалога выпускает курсор на страницу под окном. Вид — у самой
 * кнопки: отключённое MD3 ловит и `aria-disabled` ([UI-64]).
 */
const BUSY_EXIT = 'pointer-events-none';

function ModalBody({
  onOpenChange, title, children, wide, fullScreen, headerless, isDirty, flushBody, footer,
  busy = false, nothingToLose, acknowledge,
}: ModalProps) {
  const [confirmClose, setConfirmClose] = useState(false);
  const [touched, setTouched] = useState(false);
  const content = useRef<HTMLDivElement>(null);
  const { host, top, touchTop, stepSlot } = useWindowSteps(content);
  const { start: edgeStart, end: edgeEnd, above, below } = useScrollEdges();
  // Кто открыл окно — запоминается при первой отрисовке, до того как поле с `autoFocus` заберёт фокус.
  // Radix запоминает его позже, эффектом, и видит уже это поле: оно уходит вместе с окном, и фокус
  // после закрытия падал в body (ревью PR #1696).
  const [opener] = useState(() => document.activeElement as HTMLElement | null);

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
  // «Продолжить редактирование» возвращает человека туда, откуда он спросил: вопрос ушёл, а с ним и
  // фокус, и без возврата человек остался бы нигде.
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

  // Системная «Назад» на телефоне (issue #1418) — тот же выход, что у «Отмены»: с шага — к форме, с
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

  // Подвал «Закрыть» — окну, которое своих действий не объявило: пикеры и справки не правятся по
  // одному. Содержимое `flushBody` и окно без шапки рисуют свой выход сами.
  const actions = footer ?? (acknowledge || flushBody || headerless ? undefined : <ModalExit>Закрыть</ModalExit>);
  const shownTitle = top?.title ?? title;

  return (
    <Dialog.Portal>
        <Dialog.Overlay
          className={`${DIALOG_SCRIM} z-40`}
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
            : `fixed left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 z-50 max-h-[90vh] flex flex-col overflow-hidden focus:outline-none ${DIALOG_SURFACE} ${
              // У широкого — зазор по краям: между телефоном и 896 px (планшет, 768) иначе окно
              // ложилось бы в край экрана, скруглёнными углами вплотную (ревью PR #1386).
              wide ? 'w-[calc(100%-2rem)] max-w-4xl' : 'w-full max-w-lg'
            } ${NARROW_FULL}`}
          // «Тронуто»: первый же ввод внутри содержимого.
          // ⚠️ НЕ `onInputCapture`. React исполняет перехват `input` отдельным заходом, ДО того как
          // дойдёт до `onChange` поля, и сразу перерисовывает: управляемое поле получает прежнее
          // значение, а `onChange` уже не видит разницы и молчит — первый набранный знак пропадает
          // без единой ошибки (поймано живым прогоном на поле даты). `onChange` — событие
          // составное, его перехват идёт в одном заходе с обработчиком поля.
          onChangeCapture={noteInput}
          onInput={noteInput}
          // Начальный фокус — на само окно. Radix ставит его на первый фокусируемый элемент, а это
          // кнопка: Enter сразу после открытия нажимал бы её. Поле с `autoFocus` сюда не доходит —
          // оно берёт фокус раньше, и Radix события не шлёт.
          onOpenAutoFocus={e => {
            e.preventDefault();
            content.current?.focus();
          }}
          onCloseAutoFocus={e => {
            if (!opener?.isConnected || opener === document.body) return;
            e.preventDefault();
            focusFromLayer(opener);
          }}
          // Пока показан вопрос, Esc и щелчок мимо достаются ему — верхнему окну Radix, а не этому.
          onEscapeKeyDown={e => {
            // Решает диалог сам и ПОЗЖЕ — см. `afterFields`. Radix закрыл бы окно прямо сейчас.
            e.preventDefault();
            // Esc, которым отменяют набор в IME, — клавиша метода ввода, а не диалога.
            if (e.isComposing) return;
            if (rule.escape === 'close') afterFields(leave);
            else if (rule.escape === 'focusExit') afterFields(() => showExit(true));
          }}
          onPointerDownOutside={() => {
            if (rule.outside === 'hint') showExit(false);
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
          <Dialog.Title className="sr-only">{shownTitle}</Dialog.Title>
          {/* Под вопросом окно недоступно одной обёрткой: фокус не вернётся в форму мимо вопроса. */}
          <div className="contents" inert={asking}>
          {(!headerless || top) && (
            <div className={`shrink-0 px-6 pt-6 pb-2 border-b ${edge(above)}`}>
              {/* Заголовком бывает имя объекта (материал в пикере документа качества доходит до сотни
                  знаков) — две строки, дальше обрезка. */}
              <span className={`${DIALOG_TITLE} line-clamp-2 break-words`} title={shownTitle}>{shownTitle}</span>
            </div>
          )}
          {/* Форма окна под шагом остаётся смонтированной, только скрыта: выход с шага вернёт её
              такой, какой оставили. Тело и подвал шага ложатся в его места (`WindowStep`). Часовые
              прокрутки — у краёв видимого тела: черта считается по нему, а не по скрытой форме. */}
          {flushBody ? (
            <>
              <div className="flex-1 min-h-0 flex flex-col" hidden={!!top}>
                {typeof children === 'function' ? children(requestClose) : children}
              </div>
              {top && (
                <div className="overflow-y-auto flex-1 min-h-0 px-6">
                  <div ref={edgeStart} />
                  <div className="py-2">{stepSlot('body')}</div>
                  <div ref={edgeEnd} />
                </div>
              )}
            </>
          ) : (
            <div className="overflow-y-auto flex-1 min-h-0 px-6">
              <div ref={edgeStart} />
              <div className={`pt-2 ${actions || acknowledge || top ? 'pb-2' : 'pb-6'}`}>
                <div className="contents" hidden={!!top}>
                  {typeof children === 'function' ? children(requestClose) : children}
                </div>
                {stepSlot('body')}
              </div>
              <div ref={edgeEnd} />
            </div>
          )}

          {(actions || acknowledge || top) && (
            <div className={`shrink-0 px-6 pt-4 pb-6 border-t ${edge(below)}`}>
              {acknowledge ? (
                <div className="flex items-center gap-2">
                  <div className="flex-1 min-w-0">{typeof footer === 'function' ? footer(requestClose) : footer}</div>
                  <ModalExit>{acknowledge}</ModalExit>
                </div>
              ) : (
                <div className="contents" hidden={!!top}>
                  {typeof actions === 'function' ? actions(requestClose)
                    : footer ? actions : <div className="flex justify-end">{actions}</div>}
                </div>
              )}
              {stepSlot('footer')}
            </div>
          )}
          </div>

          <LeaveQuestion open={asking} onStay={() => setConfirmClose(false)}
            onLeave={() => { setConfirmClose(false); leave(); }} />
          </WindowStepContext.Provider>
          </ModalControlContext.Provider>
        </Dialog.Content>
    </Dialog.Portal>
  );
}
