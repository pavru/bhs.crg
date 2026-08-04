import { describe, it, expect } from 'vitest';
import { buildRecognitionFields, codesFromLabels } from './recognitionFields';
import type { SchemaField } from '@/shared/api/schema';
import type { DocumentType, EnumTypeDef, PrimitiveTypeDef } from '@/shared/api/types';

/**
 * Что распознаванию рассказывают про поля и как читают ответ (issue #654).
 *
 * Оба класса ошибки тихие: подпись перечисления вместо кода даёт пустой на вид `Select` при
 * заполненном реквизите, а «primitive» вместо базового типа лишает промпт единственной предметной
 * подсказки про формат. По безголовому пути импорта не видно ни того, ни другого.
 */

const field = (key: string, type: string, extra: Partial<SchemaField> = {}): SchemaField =>
  ({ key, type, title: key, required: false, ...extra } as unknown as SchemaField);

const docType = (id: string, fields: SchemaField[]): DocumentType =>
  ({ id, name: id, schema: { fields }, parentId: null } as unknown as DocumentType);

const enumTypes = [{
  id: 'e-kind', name: 'Вид документа', code: 'DocKind',
  values: [{ code: 'СС', label: 'Сертификат соответствия' }, { code: 'ДС', label: 'Декларация о соответствии' }],
}] as unknown as EnumTypeDef[];

const primitiveTypes = [
  { id: 'p-date', name: 'Дата', code: 'Date', baseType: 'date', constraints: {} },
  { id: 'p-num', name: 'Номер', code: 'Num', baseType: 'number', constraints: {} },
] as unknown as PrimitiveTypeDef[];

const defs = { enumTypes, primitiveTypes };

describe('buildRecognitionFields', () => {
  it('перечислению из реестра подставляет ПОДПИСИ — их модель и увидит в скане', () => {
    const t = docType('t', [field('Вид', 'enum', { typeId: 'e-kind' })]);
    const plan = buildRecognitionFields(t.schema.fields as SchemaField[], [t], defs);

    expect(plan.fields[0].options).toEqual(['Сертификат соответствия', 'Декларация о соответствии']);
  });

  it('без определений перечисление осталось бы без вариантов — ровно прежний дефект', () => {
    const t = docType('t', [field('Вид', 'enum', { typeId: 'e-kind' })]);
    expect(buildRecognitionFields(t.schema.fields as SchemaField[], [t], {}).fields[0].options)
      .toEqual([]);
  });

  it('legacy-список вариантов прямо в схеме продолжает работать', () => {
    const t = docType('t', [field('Вид', 'enum', { options: ['А', 'Б', ''] })]);
    const plan = buildRecognitionFields(t.schema.fields as SchemaField[], [t], defs);

    expect(plan.fields[0].options).toEqual(['А', 'Б']); // пустая строка вариантом не является
    expect(plan.enumCodes['Вид']).toMatchObject({ 'а': 'А', 'б': 'Б' });
  });

  it('примитив отдаёт БАЗОВЫЙ тип, а не бесполезное «primitive»', () => {
    const t = docType('t', [
      field('Выдан', 'primitive', { typeId: 'p-date' }),
      field('Номер', 'primitive', { typeId: 'p-num' }),
      field('Ничей', 'primitive', { typeId: 'нет-такого' }),
    ]);
    expect(buildRecognitionFields(t.schema.fields as SchemaField[], [t], defs).fields.map(f => f.type))
      .toEqual(['date', 'number', 'primitive']); // неизвестный тип — как было, врать нечем
  });

  it('спускается в составные поля и собирает пути через точку', () => {
    const inner = docType('inner', [field('Вид', 'enum', { typeId: 'e-kind' })]);
    const outer = docType('outer', [field('Период', 'complex', { typeId: 'inner' })]);
    const plan = buildRecognitionFields(outer.schema.fields as SchemaField[], [inner, outer], defs);

    expect(plan.fields.map(f => f.path)).toEqual(['Период.Вид']);
    expect(plan.enumCodes['Период.Вид']).toBeDefined(); // карта кодов вложенного поля не теряется
  });

  it('массивы, ссылки и вложения распознаванию не отдаются', () => {
    const t = docType('t', [
      field('Строки', 'array'), field('Ссылка', 'doc-ref'), field('Список', 'doc-array'),
      field('Картинка', 'image'), field('Файл', 'file'), field('Номер', 'string'),
    ]);
    expect(buildRecognitionFields(t.schema.fields as SchemaField[], [t], defs).fields.map(f => f.path))
      .toEqual(['Номер']);
  });
});

describe('codesFromLabels', () => {
  const t = docType('t', [field('Вид', 'enum', { typeId: 'e-kind' })]);
  const plan = buildRecognitionFields(t.schema.fields as SchemaField[], [t], defs);

  it('подпись, которой ответила модель, становится КОДОМ', () => {
    expect(codesFromLabels({ 'Вид': 'Сертификат соответствия' }, plan.enumCodes))
      .toEqual({ 'Вид': 'СС' });
  });

  it('регистр, лишние пробелы и «ё» совпадению не мешают', () => {
    expect(codesFromLabels({ 'Вид': '  декларация   О  СООТВЕТСТВИИ ' }, plan.enumCodes))
      .toEqual({ 'Вид': 'ДС' });
  });

  it('ответ кодом остаётся кодом — модель вправе ответить и так', () => {
    expect(codesFromLabels({ 'Вид': 'сс' }, plan.enumCodes)).toEqual({ 'Вид': 'СС' });
  });

  it('неопознанное значение сохраняется как есть, а не выбрасывается', () => {
    // Распознавание — единственное, что прочитало скан; терять его ответ хуже, чем показать
    // расхождение. Расхождение и покажут: форма подсветит, аудит значений (#644) найдёт.
    expect(codesFromLabels({ 'Вид': 'Свидетельство' }, plan.enumCodes))
      .toEqual({ 'Вид': 'Свидетельство' });
  });

  it('поля без перечисления проходят нетронутыми', () => {
    expect(codesFromLabels({ 'Номер': 'РОСС RU.0001', '__summary__': 'EKF' }, plan.enumCodes))
      .toEqual({ 'Номер': 'РОСС RU.0001', '__summary__': 'EKF' });
  });
});
