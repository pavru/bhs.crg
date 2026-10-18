import { createContext, useContext, useEffect } from 'react';

/**
 * Экран закрывает плавающую кнопку (issue #1434): на телефоне карточка записи занимает место списка,
 * и «Счёт из файла» поверх формы счёта закрывал бы её поля и кнопки. Сообщает об этом каркас
 * (`ListDetailShell`), а не каждый экран.
 */
export const PrimaryFabCoverContext = createContext<(covered: boolean) => void>(() => {});

/** Пока `covered` — плавающей кнопки нет. */
export function useCoversPrimaryFab(covered: boolean): void {
  const cover = useContext(PrimaryFabCoverContext);
  useEffect(() => {
    cover(covered);
    return () => cover(false);
  }, [cover, covered]);
}
