import { describe, it, expect } from 'vitest';
import { buildBlankTypst } from './templateBlank';
import type { DocumentType } from '@/shared/api/types';

function docType(overrides: Partial<DocumentType> & { schema: Record<string, unknown> }): DocumentType {
  return {
    id: overrides.id ?? 'dt-1',
    name: overrides.name ?? 'Тест',
    code: overrides.code ?? 'TEST',
    kind: 'Document' as DocumentType['kind'],
    isAbstract: false,
    parentId: overrides.parentId ?? null,
    schema: overrides.schema,
    pluginBindings: {},
    group: null,
    createdAt: '',
    updatedAt: '',
  };
}

describe('buildBlankTypst — справочный блок полей', () => {
  it('разделяет путь и название пробелом, даже когда путь длиннее целевой ширины столбца', () => {
    // Вложенный путь "ВыпустившаяОрганизация.Наименование" (35 символов) длиннее целевой
    // ширины колонки на depth=1 (32-2=30) — раньше padEnd молча ничего не добавлял здесь,
    // и title приклеивался к path без разделителя.
    const orgType = docType({
      id: 'org',
      schema: { fields: [{ key: 'Наименование', title: 'Наименование', type: 'string', required: true }] },
    });
    const main = docType({
      id: 'main',
      schema: {
        fields: [
          { key: 'ВыпустившаяОрганизация', title: 'Выпустившая организация', type: 'complex', typeId: 'org', required: true },
        ],
      },
    });

    const typst = buildBlankTypst('Шаблон', main, [main, orgType]);

    expect(typst).toContain('ВыпустившаяОрганизация.Наименование Наименование (string)');
    expect(typst).not.toContain('НаименованиеНаименование');
  });

  it('короткий путь по-прежнему выравнивается паддингом (не ломает существующее поведение)', () => {
    const main = docType({
      schema: { fields: [{ key: 'Дата', title: 'Дата документа', type: 'date', required: true }] },
    });

    const typst = buildBlankTypst('Шаблон', main, [main]);

    expect(typst).toMatch(/\/\/ {3}Дата {28}Дата документа \(date\)/);
  });
});
