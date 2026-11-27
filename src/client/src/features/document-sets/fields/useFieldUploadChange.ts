import { createContext, useLayoutEffect, useRef } from 'react';

/** Форма со сменой шага может дождаться вложений перед размонтированием полей. */
export const TrackFieldFiles = createContext(false);

/** При завершении загрузки форма уже может содержать более свежие соседние значения. */
export function useFieldUploadChange<T>(onChange: (value: T) => void) {
  const change = useRef<typeof onChange | null>(null);
  useLayoutEffect(() => {
    change.current = onChange;
    return () => { change.current = null; };
  }, [onChange]);
  return change;
}
