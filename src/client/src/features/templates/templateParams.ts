import type { TemplateParam } from '@/shared/api/types';

// Отдельным файлом от компонента (issue #858): модуль, экспортирующий и компонент, и
// функцию, не может быть границей горячей подмены — правка поднимается вверх по импортам.

export function parseTemplateParams(json: string | null): TemplateParam[] {
  if (!json) return [];
  try { const a = JSON.parse(json); return Array.isArray(a) ? (a as TemplateParam[]) : []; } catch { return []; }
}
