import { useState } from 'react';

// Отдельным файлом от компонента (issue #858): модуль, экспортирующий и компонент, и
// функцию, не может быть границей горячей подмены — правка поднимается вверх по импортам.

export const MAX_VERSIONS_KEY = 'crg.maxTemplateVersions';
export const DEFAULT_MAX_VERSIONS = 5;

export function useMaxTemplateVersions(): [number, (v: number) => void] {
  const [value, setValue] = useState(() => {
    const stored = localStorage.getItem(MAX_VERSIONS_KEY);
    const parsed = stored ? Number(stored) : NaN;
    return Number.isFinite(parsed) && parsed >= 2 ? parsed : DEFAULT_MAX_VERSIONS;
  });
  function set(v: number) {
    localStorage.setItem(MAX_VERSIONS_KEY, String(v));
    setValue(v);
  }
  return [value, set];
}
