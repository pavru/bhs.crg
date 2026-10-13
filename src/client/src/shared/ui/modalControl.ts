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
