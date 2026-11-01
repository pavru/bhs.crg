import * as Dialog from '@radix-ui/react-dialog';
import { useCallback, useContext, useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { NARROW, useMedia } from '@/shared/hooks/useMedia';
import { Button, type ButtonProps } from './Button';
import {
  ModalBarContext, ModalBodyExitContext, ModalControlContext, WindowStepContext, useModalControl,
  type BarAction, type ModalBar, type WindowStepHost,
} from './modalControl';
import { ModalAppBar } from './ModalAppBar';
import { DISMISSAL, modalState, pointAtExit } from './modalDismissal';
import { useHistoryLayer } from './historyLayers';
import { focusFromLayer } from './layerFocus';
import { DIALOG_SCRIM, DIALOG_SURFACE, DIALOG_TITLE } from './dialogSurface';
import { LeaveQuestion } from './LeaveQuestion';
import { useScrollEdges } from './useScrollEdges';
import { useWindowSteps } from './windowSteps';

export { ModalAction, ModalNote, ModalPrimary } from './ModalRoles';

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
  /** Full-screen dialog ([UI-75]) — для крупных форм: шапка с крестиком и главным действием, подвала
   *  нет. Таким же любое окно открывается на экране уже 768. Рендерится НАД текущей страницей (она
   *  остаётся смонтированной): закрытие возвращает в прежний контекст. */
  fullScreen?: boolean;
  /** Не рисовать заголовок по умолчанию (содержимое рисует собственный top app bar). Свою кнопку
   *  выхода такое содержимое помечает `data-modal-exit` — иначе Esc и клик мимо её не покажут. */
  headerless?: boolean;
  /** Не добавлять собственный скролл и паддинг тела — содержимое само управляет
   *  раскладкой (фиксированный футер, прокрутка только области полей). Свой выход такое содержимое
   *  ставит само — `ModalExit`: подвала «Закрыть» окно ему не добавляет. */
  flushBody?: boolean;
  /**
   * Действия окна — только кнопки с ролью: `ModalExit`, `ModalPrimary`, `ModalAction`, `ModalNote`
   * ([UI-75], сторож `modalFooterRoles.test.ts`). На столе они — подвал, который не прокручивается; во
   * весь экран переносят себя в шапку. Не передан — окно ставит подвал с «Закрыть» само: крестика у
   * basic dialog нет, и выход должен быть виден.
   *
   * <p>«Отмену» ставит `ModalExit`: выход, позванный мимо, не спросит о правках и останется живым при
   * идущем запросе.</p>
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
 * Подвал на столе: кнопки с ролью в объявленном порядке у замыкающего края, а помеченные `data-lead`
 * (пояснение, редкое действие) — у ведущего. Последний из ведущих отодвигает остальное вправо.
 */
const FOOTER_ROW = 'flex flex-wrap items-center justify-end gap-2 [&_[data-lead]:not(:has(~[data-lead]))]:mr-auto';

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
  const markBodyExit = useContext(ModalBodyExitContext);
  const bar = useContext(ModalBarContext);
  useLayoutEffect(() => markBodyExit?.(), [markBodyExit]);
  // Во весь экран выход подвала — крестик шапки ([UI-75]).
  if (bar) return null;
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
  const { start: edgeStart, end: edgeEnd, above: bodyAbove, below: bodyBelow, known } = useScrollEdges();
  // Тело `flushBody` прокручивает себя само, и окну о его прокрутке не узнать: черта у такого окна стоит
  // всегда, как до перехода, а не пропадает молча.
  const above = known ? bodyAbove : true;
  const below = known ? bodyBelow : true;
  // Кто открыл окно — запоминается при первой отрисовке, до того как поле с `autoFocus` заберёт фокус.
  // Radix запоминает его позже, эффектом, и видит уже это поле: оно уходит вместе с окном, и фокус
  // после закрытия падал в body (ревью PR #1696).
  const [opener] = useState(() => document.activeElement as HTMLElement | null);
  // Сколько `ModalExit` нарисовано в теле: форма со своим выходом второго, подвала «Закрыть», не получает.
  // Отмечается сама кнопка (`ModalBodyExitContext`), до отрисовки — лишний подвал не мелькает.
  const [bodyExits, setBodyExits] = useState(0);
  const markBodyExit = useCallback(() => {
    setBodyExits(n => n + 1);
    return () => setBodyExits(n => n - 1);
  }, []);
  // Во весь экран ([UI-75]): `fullScreen` всегда, любое окно — на экране уже 768. Решает JS, а не классы
  // `max-sm:`: от узости зависит, где стоят действия, а не только ширина.
  const narrow = useMedia(NARROW);
  const full = !!fullScreen || narrow;
  const [primarySlot, setPrimarySlot] = useState<HTMLElement | null>(null);
  const [noteSlot, setNoteSlot] = useState<HTMLElement | null>(null);
  const [barActions, setBarActions] = useState<Record<string, BarAction>>({});
  const setAction = useCallback((id: string, action: BarAction | null) => setBarActions(s => {
    if (!action) {
      if (!(id in s)) return s;
      const rest = { ...s };
      delete rest[id];
      return rest;
    }
    const was = s[id];
    return was && was.layer === action.layer && was.label === action.label && was.danger === action.danger
      && was.disabled === action.disabled ? s : { ...s, [id]: action };
  }), []);

  // На шаге окно закрывается по правилам шага, а выход с него — назад, к форме окна.
  const state = top
    ? modalState({ busy: busy || top.busy, isDirty: top.isDirty, touched: top.touched })
    : modalState({ busy, acknowledge, isDirty, touched, nothingToLose });
  const leave = top ? top.back : () => onOpenChange(false);
  // Места во весь экран — у видимого слоя: форма под шагом в шапку не пишет.
  const placeOf = useCallback((layer: string, live: boolean): ModalBar | null => full
    ? { layer, primary: live ? primarySlot : null, note: live ? noteSlot : null, setAction }
    : null, [full, primarySlot, noteSlot, setAction]);
  const topId = top?.id;
  const formBar = useMemo(() => placeOf('form', !topId), [placeOf, topId]);
  const stepHost = useMemo<WindowStepHost>(() => ({ ...host, bar: id => placeOf(id, id === topId) }),
    [host, placeOf, topId]);
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
  // одному. Содержимое `flushBody` и окно без шапки рисуют свой выход сами, а форма в теле — своим
  // `ModalExit` рядом с «Создать»: второй выход в подвале под ней был бы лишним.
  const actions = footer ?? (acknowledge || flushBody || headerless || bodyExits > 0 ? undefined : <ModalExit>Закрыть</ModalExit>);
  const shownTitle = top?.title ?? title;

  return (
    <Dialog.Portal>
        <Dialog.Overlay
          className={DIALOG_SCRIM}
          // Нажатие на затемнение фокус не забирает: иначе щелчок мимо выносил бы курсор из поля,
          // где человек набирал, — и окно, которое не закрылось, всё равно сбивало бы его с места.
          onMouseDown={e => e.preventDefault()}
        />
        <Dialog.Content
          ref={content}
          // Окно, которое Esc сейчас не закрывает, держит человека — см. `DialogHold`.
          data-modal-state={rule.escape === 'close' ? 'free' : state}
          className={full
            // Full-screen dialog: поверхность `surface`, без радиуса и тени ([UI-75]).
            ? 'fixed inset-0 z-50 flex flex-col overflow-hidden focus:outline-none bg-surface'
            : `fixed left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 z-50 max-h-[90vh] flex flex-col overflow-hidden focus:outline-none ${DIALOG_SURFACE} ${
              // У широкого — зазор по краям: между 768 и 896 px иначе окно ложилось бы в край экрана,
              // скруглёнными углами вплотную (ревью PR #1386).
              wide ? 'w-[calc(100%-2rem)] max-w-4xl' : 'w-full max-w-lg'
            }`}
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
          <ModalBodyExitContext.Provider value={null}>
          <ModalBarContext.Provider value={null}>
          <WindowStepContext.Provider value={stepHost}>
          <Dialog.Title className="sr-only">{shownTitle}</Dialog.Title>
          {/* Под вопросом окно недоступно одной обёрткой: фокус не вернётся в форму мимо вопроса. */}
          <div className="contents" inert={asking}>
          {full && (!headerless || top) ? (
            <ModalAppBar title={shownTitle} back={!!top} busy={busy || !!top?.busy} onExit={requestClose}
              primaryRef={setPrimarySlot} noteRef={setNoteSlot} edge={edge(above)}
              actions={Object.values(barActions).filter(a => a.layer === (topId ?? 'form'))} />
          ) : (!headerless || top) && (
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
              <div className={`pt-2 ${(actions || acknowledge || top) && !full ? 'pb-2' : 'pb-6'}`}>
                <div className="contents" hidden={!!top}>
                  <ModalBodyExitContext.Provider value={markBodyExit}>
                    {typeof children === 'function' ? children(requestClose) : children}
                  </ModalBodyExitContext.Provider>
                </div>
                {stepSlot('body')}
              </div>
              <div ref={edgeEnd} />
            </div>
          )}

          {/* Во весь экран подвала нет, но кнопки с ролью смонтированы и тут: из подвала они переносят себя
              в шапку. */}
          {(actions || acknowledge || top) && (
            <div className={`shrink-0 px-6 pt-4 pb-6 border-t ${edge(below)} ${FOOTER_ROW}`} hidden={full}>
              <ModalBarContext.Provider value={formBar}>
                <div className="contents" hidden={!!top}>
                  {typeof actions === 'function' ? actions(requestClose) : actions}
                  {acknowledge && <ModalExit>{acknowledge}</ModalExit>}
                </div>
              </ModalBarContext.Provider>
              {stepSlot('footer')}
            </div>
          )}
          </div>

          <LeaveQuestion open={asking} onStay={() => setConfirmClose(false)}
            onLeave={() => { setConfirmClose(false); leave(); }} />
          </WindowStepContext.Provider>
          </ModalBarContext.Provider>
          </ModalBodyExitContext.Provider>
          </ModalControlContext.Provider>
        </Dialog.Content>
    </Dialog.Portal>
  );
}
