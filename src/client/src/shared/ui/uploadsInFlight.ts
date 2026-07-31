import { useSyncExternalStore } from 'react';

/**
 * Счётчик незавершённых загрузок файлов (issue #522).
 *
 * Зачем не проп: поле-картинка живёт в четырёх местах, и в одном из них — внутри вложенных
 * составных полей и строк массива (`ComplexFields`). Протаскивать «я занято» через всю эту глубину
 * значило бы менять сигнатуры на каждом уровне ради одного логического бита.
 *
 * Зачем вообще: значение поля появляется только ПОСЛЕ загрузки. Сохрани пользователь форму раньше —
 * документ уедет без картинки, и человек об этом не узнает: он ведь её уже видит (показывается
 * локальное превью). Поэтому кнопка сохранения на время загрузки гаснет.
 */
let inFlight = 0;
const listeners = new Set<() => void>();

function emit() {
  for (const l of listeners) l();
}

export function beginUpload(): void {
  inFlight += 1;
  emit();
}

export function endUpload(): void {
  inFlight = Math.max(0, inFlight - 1);
  emit();
}

/** true, пока хоть одна загрузка не закончилась. */
export function useUploadsInFlight(): boolean {
  return useSyncExternalStore(
    listener => { listeners.add(listener); return () => listeners.delete(listener); },
    () => inFlight > 0,
    () => false,   // на сервере рендерить нечего — загрузок там не бывает
  );
}
