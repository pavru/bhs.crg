import { describe, it, expect } from 'vitest';
import { clientSources } from '@/shared/testing/clientSources';
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
// Охват — общий для всех переписей (ревью PR #1067): копии глоба разошлись, и два сторожа
// видели только `.tsx`, о чём по их коду догадаться было нельзя.
const sources = clientSources;

describe('вызывающие авто-маппинг', () => {
  // Зовёт, а не объявляет и не упоминает: с общим охватом (`.ts` тоже) в улов иначе попадают сам
  // модуль с объявлением хука и этот сторож — оба «вызывающими» не являются.
  const CALLS = /useAutoMapDataSetSource\(\)/;
  const DECLARES = /export function useAutoMapDataSetSource/;

  const callers = Object.entries(sources)
    .filter(([file]) => !file.endsWith('.test.ts'))
    .filter(([, code]) => CALLS.test(code) && !DECLARES.test(code))
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
