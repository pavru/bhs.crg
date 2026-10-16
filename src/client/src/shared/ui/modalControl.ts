import { createContext, useContext } from 'react';

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

export function useModalControl(): ModalControl {
  const control = useContext(ModalControlContext);
  if (!control) throw new Error('ModalExit живёт только внутри Modal');
  return control;
}

/** Что шаг окна сообщает окну: заголовок вместо его собственного и как себя закрывать. */
export interface WindowStepState {
  title: string;
  busy: boolean;
  isDirty: boolean;
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
}

export const WindowStepContext = createContext<WindowStepHost | null>(null);

/** Открыть своё окно, если о нём просят, — но не внутри окна: там раскрываются на месте (issue #1384). */
export function useOwnWindow(wanted: boolean): boolean {
  return useContext(WindowStepContext) === null && wanted;
}
