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

/**
 * Что сделает «новая версия» от <paramref>template</paramref> (issue #1920): какой номер получит и
 * какие действующие версии того же шаблона уйдут в историю. У исторической версии это не она сама,
 * а действующая — о ней окно и обязано сказать: человек открыл v2 и думает только о ней.
 */
export interface NewVersionPlan {
  next: number;
  /** Действующие версии шаблона, кроме открытой, — сервер их погасит. */
  retires: Template[];
  /** Перейдёт ли к новой версии «по умолчанию» с другой версии шаблона. */
  takesDefault: boolean;
  /**
   * Сколько документов закреплено за гаснущими версиями — открытой, если она действующая, и
   * {@link retires}. Сервер пересадит их на новую версию и вернёт в черновик (#1923, вариант А).
   */
  pinnedToRetired: number;
  /**
   * Перейдут ли на новую версию документы без выбранного шаблона — правилом сервера: новая
   * версия стала «по умолчанию», или у типа нет действующего шаблона по умолчанию.
   */
  noPinDocsMove: boolean;
}

export function newVersionPlan(
  templates: Template[], template: Template, usage: Record<string, { count: number }> = {},
): NewVersionPlan {
  const siblings = templates.filter(t => t.name === template.name && t.id !== template.id);
  const retires = siblings.filter(t => t.isActive);
  const newIsDefault = template.isDefault || siblings.some(t => t.isDefault);
  const otherDefault = templates.some(t => t.name !== template.name && t.isDefault && t.isActive);
  return {
    next: nextVersionOf(templates, template),
    retires,
    takesDefault: !template.isDefault && siblings.some(t => t.isDefault),
    pinnedToRetired: [...(template.isActive ? [template] : []), ...retires]
      .reduce((n, t) => n + (usage[t.id]?.count ?? 0), 0),
    noPinDocsMove: newIsDefault || !otherDefault,
  };
}
