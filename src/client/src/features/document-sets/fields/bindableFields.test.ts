import { describe, it, expect } from 'vitest';
import { bindableFields } from './bindableFields';
import type { SchemaField } from '@/shared/api/schema';

const field = (key: string, over: Partial<SchemaField> = {}): SchemaField =>
  ({ key, title: key, type: 'string', ...over }) as SchemaField;

describe('поля, открытые источнику данных', () => {
  it('не отдаёт ни расчётные, ни запертые', () => {
    const list = [
      field('Обычное'),
      field('Расчётное', { computed: true, expression: '1' }),
      field('Запертое', { locked: true }),
      // Происхождение из модуля само по себе ничего не запрещает: поле «Табельный номер» модуль
      // ЗАВОДИТ, а заполняет его источник или человек. Запрещает только замок.
      field('ПолеМодуля', { origin: 'module' }),
    ];
    expect(bindableFields(list).map(f => f.key)).toEqual(['Обычное', 'ПолеМодуля']);
  });

  it('пустой список не ломает', () => {
    expect(bindableFields([])).toEqual([]);
  });
});

/**
 * Сторож против возврата дефекта, найденного ревью PR #1011: дверь ручного ввода закрыта, а
 * соседний вход к тому же полю остался открыт.
 *
 * Авто-маппинг (`useAutoMapDataSetSource`) — именно такой вход: он предлагает `{ключ: колонка}`
 * по СПИСКУ полей, который даёт вызывающий, и результат уезжает в `mapping` НАПРЯМУЮ, мимо
 * редактора маппинга с его отсевом. Поэтому всякий, кто его зовёт, обязан отсеять поля сам — тем
 * же правилом. Так запертое поле и оказывалось привязанным: «линзу» у него спрятали, а галка
 * «этот источник заполнит также» у СОСЕДНЕГО поля включена по умолчанию.
 *
 * Текстом, а не разбором синтаксиса: так же ловится импорт через общий индекс модуля.
 */
const sources = import.meta.glob('/src/**/*.tsx', { query: '?raw', import: 'default', eager: true }) as Record<string, string>;

describe('вызывающие авто-маппинг', () => {
  const callers = Object.entries(sources)
    .filter(([, code]) => code.includes('useAutoMapDataSetSource'))
    .map(([file]) => file.replace(/^\/src\//, ''))
    .sort();

  it('все до одного отсеивают поля общим правилом', () => {
    const offenders = callers.filter(f => !sources['/src/' + f].includes('bindableFields'));
    expect(offenders, 'авто-маппинг предлагает поля мимо редактора маппинга — отсейте их сами').toEqual([]);
  });

  it('видит самих вызывающих — иначе проверять было бы нечего', () => {
    // Поимённо: «нашлось хотя бы столько-то» прошло бы и на пустом стекле.
    expect(callers).toEqual([
      'features/document-sets/catalog/EntryDataSetBindings.tsx',
      'features/document-sets/editor/DataSetsTab.tsx',
      'features/document-sets/editor/FieldSourceBinding.tsx',
    ]);
  });
});
