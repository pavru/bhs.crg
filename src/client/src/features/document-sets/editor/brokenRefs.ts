import { isFieldRef } from '@/shared/api/types';

// ── Обнаружение неразрешённых ссылок в значениях ────────────────────────────
// Выделено из editor/index.tsx (#490). Отдельно от бейджа: чистая функция и компонент в одном
// модуле — это ровно то, на что справедливо ругается react-refresh.

export function containsRef(v: unknown): boolean {
  if (isFieldRef(v)) return true;
  if (Array.isArray(v)) return v.some(containsRef);
  if (v != null && typeof v === 'object') return Object.values(v).some(containsRef);
  return false;
}
