import type { Template } from '@/shared/api/types';

// Отдельным файлом от компонента (issue #858): модуль, экспортирующий и компонент, и
// функцию, не может быть границей горячей подмены — правка поднимается вверх по импортам.

export interface TemplateGroup {
  name: string;
  versions: Template[];
}

export function groupTemplates(templates: Template[]): TemplateGroup[] {
  const map = new Map<string, Template[]>();
  for (const t of templates) {
    const arr = map.get(t.name) ?? [];
    arr.push(t);
    map.set(t.name, arr);
  }
  return [...map.entries()].map(([name, versions]) => ({
    name,
    versions: [...versions].sort((a, b) => b.version - a.version),
  }));
}

/**
 * Номер следующей версии шаблона — наибольший среди его версий плюс один (issue #1920), как его
 * считает сервер. Версии шаблона — с тем же названием в списке одного типа документа.
 */
export function nextVersionOf(templates: Template[], template: Template): number {
  return Math.max(template.version,
    ...templates.filter(t => t.name === template.name).map(t => t.version)) + 1;
}
