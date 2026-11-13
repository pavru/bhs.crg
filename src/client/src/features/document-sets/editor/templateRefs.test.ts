import { describe, expect, it } from 'vitest';
import type { Template } from '@/shared/api/types';
import { effectiveTemplateOf, knownTemplateIds, knownTemplateParams } from './templateRefs';

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

  it('выбор — только известные шаблоны, порядок сохраняется', () => {
    expect(knownTemplateIds(['gone', 'b', 'a'], [a, b])).toEqual(['b', 'a']);
  });

  it('параметры — только ключи известных шаблонов', () => {
    expect(knownTemplateParams({ a: { x: 1 }, gone: { y: 2 } }, [a, b])).toEqual({ a: { x: 1 } });
  });

  it('эффективный — первый ДЕЙСТВУЮЩИЙ из выбранных, а не первый id', () => {
    expect(effectiveTemplateOf(['gone', 'b'], [a, b, def], null)).toBe(b);
  });

  it('без выбора — пин, затем по умолчанию, затем первый', () => {
    expect(effectiveTemplateOf([], [a, b, def], 'b')).toBe(b);
    expect(effectiveTemplateOf(['gone'], [a, b, def], null)).toBe(def);
    expect(effectiveTemplateOf([], [a, b], null)).toBe(a);
    expect(effectiveTemplateOf([], [], null)).toBeUndefined();
  });
});
