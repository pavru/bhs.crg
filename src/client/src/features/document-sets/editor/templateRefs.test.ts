import { describe, expect, it } from 'vitest';
import type { Template } from '@/shared/api/types';
import { effectiveTemplateOf, paramTemplateIds, unknownTemplateRefs } from './templateRefs';

function tpl(id: string, extra: Partial<Template> = {}): Template {
  return {
    id, documentTypeId: 'dt', name: id, content: '', parameters: null, comment: null,
    version: 1, isActive: true, isDefault: false, createdAt: '', ...extra,
  } as Template;
}

describe('ссылки документа на шаблоны (issue #1960)', () => {
  const a = tpl('a');
  const b = tpl('b');
  const def = tpl('def', { isDefault: true });

  it('незнакомые id — из выбора и параметров, без повторов', () => {
    expect(unknownTemplateRefs(['new', 'a', 'new'], ['b', 'gone'], [a, b])).toEqual(['gone', 'new']);
    expect(unknownTemplateRefs(['a'], ['b'], [a, b])).toEqual([]);
  });

  it('ключи параметров — только у объекта', () => {
    expect(paramTemplateIds('{"a":{"x":1},"b":{}}')).toEqual(['a', 'b']);
    expect(paramTemplateIds('[1]')).toEqual([]);
    expect(paramTemplateIds('не json')).toEqual([]);
    expect(paramTemplateIds(null)).toEqual([]);
  });

  it('эффективный — первый ДЕЙСТВУЮЩИЙ из выбранных, а не первый id', () => {
    expect(effectiveTemplateOf(['gone', 'b'], [a, b, def], null)).toBe(b);
  });

  it('без выбора — пин, затем по умолчанию, затем первый действующий', () => {
    expect(effectiveTemplateOf([], [a, b, def], 'b')).toBe(b);
    expect(effectiveTemplateOf([], [a, b, def], null)).toBe(def);
    expect(effectiveTemplateOf([], [a, b], null)).toBe(a);
    expect(effectiveTemplateOf([], [], null)).toBeUndefined();
  });

  it('пин на историческую версию — она и есть эффективный (сервер выпускает ею)', () => {
    const old = tpl('old', { isActive: false, version: 1 });
    expect(effectiveTemplateOf([], [def, old], 'old')).toBe(old);
  });

  it('выбор, где ни один не действует, — не запасной шаблон: сервер откажет', () => {
    const retired = tpl('retired', { isActive: false });
    expect(effectiveTemplateOf(['retired'], [a, def, retired], null)).toBeUndefined();
    expect(effectiveTemplateOf(['gone'], [a, def], null)).toBeUndefined();
  });

  it('первый действующий — по порядку сервера, неактивные пропускаются', () => {
    const a1 = tpl('a1', { isActive: false });
    expect(effectiveTemplateOf([], [a1, b, a], null)).toBe(b);
  });
});
