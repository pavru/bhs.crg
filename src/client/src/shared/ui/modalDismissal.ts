/**
 * Чем закрывается модальный диалог (issue #1235).
 *
 * Критичность — по СОСТОЯНИЮ, а не по пометке: диалог нельзя закрыть промахом, пока в нём есть
 * правки, идёт запрос или показан результат, который надо прочитать. Поимённого списка «критических
 * диалогов» нет — он устаревал бы с каждым новым окном.
 *
 * Правило вынесено из компонента, чтобы таблица из задачи была одной таблицей в коде: её читают
 * `Modal` и тест, а не шесть условий, разбросанных по обработчикам Radix.
 */

/** Состояния по убыванию силы: первое подошедшее и есть состояние диалога. */
export type ModalState = 'busy' | 'result' | 'dirty' | 'touched' | 'light' | 'plain';

export interface ModalFacts {
  /** Идёт запрос: исход ещё неизвестен, и закрытое окно его спрятало бы. */
  busy?: boolean;
  /** Показан результат — значение называет единственный выход («Понятно», «Готово»). */
  acknowledge?: string;
  /** Правки есть — по слову вызывающего. `undefined` значит «вызывающий за правками не следит». */
  isDirty?: boolean;
  /** В содержимом что-то вводили — заметил сам диалог. */
  touched?: boolean;
  /** Утверждение вызывающего: закрыть в любой момент ничего не стоит. */
  nothingToLose?: boolean;
}

export function modalState(f: ModalFacts): ModalState {
  if (f.busy) return 'busy';
  if (f.acknowledge) return 'result';
  if (f.isDirty) return 'dirty';
  // Лёгкий раньше «тронутого»: в пикере набирают запрос поиска, и это не правка.
  if (f.nothingToLose) return 'light';
  // Собственное наблюдение диалога — только там, где вызывающий о правках молчит. Тот, кто передал
  // `isDirty={false}`, знает точнее: поле поиска или уже сохранённая строка правкой не являются.
  if (f.touched && f.isDirty === undefined) return 'touched';
  return 'plain';
}

export interface Dismissal {
  /** Клик по затемнению: закрыть, показать выход кольцом или не отвечать вовсе. */
  outside: 'close' | 'hint' | 'dead';
  /** Esc: закрыть, перенести фокус на кнопку выхода или не отвечать. */
  escape: 'close' | 'focusExit' | 'dead';
  /** Крестик и «Отмена»: закрыть, спросить «Закрыть без сохранения?» или не отвечать. */
  exit: 'close' | 'ask' | 'dead';
}

export const DISMISSAL: Record<ModalState, Dismissal> = {
  // При запросе отклика нет нарочно: кнопка действия уже крутится, и кольцо вокруг мёртвой
  // «Отмены» звало бы нажать то, что не нажимается.
  busy: { outside: 'dead', escape: 'dead', exit: 'dead' },
  result: { outside: 'hint', escape: 'focusExit', exit: 'close' },
  dirty: { outside: 'hint', escape: 'focusExit', exit: 'ask' },
  // «Тронуто» слабее «правки есть»: диалог видел ввод, но не знает, сохранён ли он (список со
  // строчной правкой сохраняет сразу). Поэтому промах он не пропускает, а явный выход — пропускает
  // без вопроса: ложный вопрос «несохранённые изменения будут потеряны» учил бы его не читать.
  touched: { outside: 'hint', escape: 'focusExit', exit: 'close' },
  plain: { outside: 'hint', escape: 'close', exit: 'close' },
  light: { outside: 'close', escape: 'close', exit: 'close' },
};

/** Сколько держится кольцо вокруг кнопки выхода после клика мимо. */
export const HINT_MS = 600;

/**
 * Окно, которое сейчас нельзя закрыть клавишей: правки, запрос, результат (issue #1238). Такое окно
 * называет себя атрибутом `data-modal-held` со своим состоянием — по нему обвязка приложения не
 * открывает поверх него командную палитру: иначе правило закрытия обходилось бы двумя нажатиями
 * (Ctrl+K, Enter), и окно исчезало вместе со страницей.
 *
 * @returns верхнее из таких окон или `null`.
 */
export function heldDialog(): HTMLElement | null {
  const held = document.querySelectorAll<HTMLElement>('[data-modal-held]');
  return held.length ? held[held.length - 1] : null;
}

const rings = new WeakMap<HTMLElement, ReturnType<typeof setTimeout>>();

/**
 * Показать выход окна: кнопку, помеченную `data-modal-exit`, иначе крестик (`data-modal-cross`).
 * Кольцом, без движения: тишина неотличима от зависшего окна, а встряхивание читается как ошибка.
 *
 * <p>Одна на `Modal` и `ConfirmDialog` (ревью PR #1312): второй гасил Esc и промах в состоянии
 * отказа молча — то есть ровно той тишиной, от которой здесь уходят.</p>
 *
 * @param focus Esc ещё и переносит туда фокус — закрывает уже Enter. Клик мимо фокус не трогает:
 *   уведённый из поля щелчком, он превратил бы следующий пробел в «Отмену».
 */
export function pointAtExit(root: HTMLElement | null | undefined, focus: boolean) {
  const exit = root?.querySelector<HTMLElement>('[data-modal-exit]:not([disabled])')
    ?? root?.querySelector<HTMLElement>('[data-modal-cross]');
  if (!root || !exit) return;
  exit.scrollIntoView?.({ block: 'nearest' });
  if (focus) exit.focus();
  // Кольцо снимается со ВСЕХ кнопок окна, а не с этой: выход между двумя промахами мог смениться
  // («Отмена» стала недоступна — остался крестик), а таймер на окно один.
  clearTimeout(rings.get(root));
  root.querySelectorAll('[data-modal-hint]').forEach(old => old.removeAttribute('data-modal-hint'));
  exit.setAttribute('data-modal-hint', '');
  rings.set(root, setTimeout(() => exit.removeAttribute('data-modal-hint'), HINT_MS));
}
