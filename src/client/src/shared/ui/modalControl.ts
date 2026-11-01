import { createContext, useContext, type ReactNode } from 'react';

/**
 * Что диалог даёт своему содержимому (issue #1235): охраняемый выход и признак «идёт запрос».
 * Отдельным файлом — файл с компонентом хуков не экспортирует (линт `react-refresh`).
 */
export interface ModalControl {
  /** Выход тем же путём, что у крестика: спросит о правках, при запросе не сделает ничего. */
  requestClose: () => void;
  busy: boolean;
}

export const ModalControlContext = createContext<ModalControl | null>(null);

/**
 * Отметка «у тела окна свой выход» (sharedui-md3-13, [UI-75]): `ModalExit` формы, нарисованной в теле, зовёт
 * её, пока смонтирован, — и окно не ставит под формой второй выход, подвал «Закрыть». Возвращает снятие
 * отметки. Подвал окна получает `null`: его `ModalExit` — выход самого подвала, а не тела, в том числе у окна,
 * открытого из тела другого окна.
 */
export const ModalBodyExitContext = createContext<(() => () => void) | null>(null);

export function useModalControl(): ModalControl {
  const control = useContext(ModalControlContext);
  if (!control) throw new Error('ModalExit живёт только внутри Modal');
  return control;
}

/** Что шаг окна сообщает окну: заголовок вместо его собственного и как себя закрывать. */
export interface WindowStepState {
  title: string;
  busy: boolean;
  /** Не сказано — окно судит по вводу на шаге само, как о своей форме (`touched`). */
  isDirty?: boolean;
}

/** Куда шаг кладёт своё тело и подвал — места окно заводит под каждый шаг в стопке. */
export interface WindowStepSlots {
  body?: HTMLElement | null;
  footer?: HTMLElement | null;
}

/**
 * Окно, внутри которого отрисован компонент (issue #1384): им шаг окна (`WindowStep`) встаёт в стопку
 * шагов, а поле узнаёт, что стоит в окне, и второе окно над ним не открывает. Нет окна — `null`.
 */
export interface WindowStepHost {
  push: (id: string, back: () => void) => void;
  update: (id: string, state: WindowStepState) => void;
  pop: (id: string) => void;
  slots: Record<string, WindowStepSlots>;
  /** Места шага во весь экран — см. `ModalBar`; на столе `null`. */
  bar?: (id: string) => ModalBar | null;
}

export const WindowStepContext = createContext<WindowStepHost | null>(null);

/** Открыть своё окно, если о нём просят, — но не внутри окна: там раскрываются на месте (issue #1384). */
export function useOwnWindow(wanted: boolean): boolean {
  return useContext(WindowStepContext) === null && wanted;
}

/** Действие окна, перенесённое во весь экран в меню «⋮» шапки ([UI-75]). */
export interface BarAction {
  /** Чьё: `form` — формы окна, иначе id шага. В «⋮» — только действия видимого. */
  layer: string;
  label: string;
  danger: boolean;
  disabled: boolean;
  /** Идёт запрос действия: пункт погашен и вместо знака — кольцо хода. */
  loading: boolean;
  icon?: ReactNode;
  /** Почему пункт погашен — строкой под подписью: подсказки при наведении на телефоне нет. */
  hint?: string;
  run: () => void;
}

/**
 * Окно во весь экран (sharedui-md3-13, [UI-75]): куда кнопки подвала с ролью переносят себя. Подвала у
 * такого окна нет — `ModalPrimary` рисует себя в шапке, `ModalNote` — строкой под ней, `ModalAction`
 * встаёт пунктом «⋮», `ModalExit` уступает место крестику. `null` — окно на столе: роли рисуют подвал на
 * месте. У слоя, который сейчас не виден (форма под шагом), мест нет — его роли молчат.
 */
export interface ModalBar {
  layer: string;
  primary: HTMLElement | null;
  note: HTMLElement | null;
  /** Поставить, обновить (`action`) или снять (`null`) пункт «⋮». Ссылка стабильная. */
  setAction: (id: string, action: BarAction | null) => void;
}

export const ModalBarContext = createContext<ModalBar | null>(null);
