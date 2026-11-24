import { useLayoutEffect, useRef } from 'react';

/** При завершении загрузки форма уже может содержать более свежие соседние значения. */
export function useFieldUploadChange<T>(onChange: (value: T) => void) {
  const change = useRef<typeof onChange | null>(null);
  useLayoutEffect(() => {
    change.current = onChange;
    return () => { change.current = null; };
  }, [onChange]);
  return change;
}
