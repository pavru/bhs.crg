import { describe, it, expect } from 'vitest';
import { discriminatorProblem } from './unionDiscriminator';
import type { SchemaField } from '@/shared/api/schema';
import type { MaterializeDiscriminator } from '@/shared/api/types';

/**
 * Почему настройку варианта-по-типу нельзя сохранять (issue #716).
 *
 * Клиентская проверка живёт рядом с серверной намеренно: сервер остаётся авторитетом, а эта нужна,
 * чтобы отказ пришёл ДО нажатия «Сохранить». Проверяем именно те случаи, из-за которых настройка
 * молча пропускала бы строки: правило без маппинга и один тип у двух вариантов.
 */
describe('discriminatorProblem', () => {
  const AOSR = '11111111-1111-1111-1111-111111111111';
  const REGISTRY = '22222222-2222-2222-2222-222222222222';

  const variants: SchemaField[] = [
    { key: 'АОСР', title: 'АОСР', type: 'doc-ref' } as SchemaField,
    { key: 'РеестрРабот', title: 'Реестр работ', type: 'doc-ref' } as SchemaField,
  ];

  const typeName = (id: string) => (id === AOSR ? 'АОСР' : 'Реестр работ');

  const d = (rules: Record<string, string[]>, column = 'ТипКод'): MaterializeDiscriminator =>
    ({ column, kind: 'docTypeCode', rules });

  it('пропускает полную настройку', () => {
    expect(discriminatorProblem(
      variants,
      { 'АОСР': 'Ид', 'РеестрРабот': 'Ид' },
      d({ 'АОСР': [AOSR], 'РеестрРабот': [REGISTRY] }),
      typeName,
    )).toBeNull();
  });

  it('вариант без типов — выключен, а не ошибка', () => {
    expect(discriminatorProblem(
      variants, { 'АОСР': 'Ид' }, d({ 'АОСР': [AOSR] }), typeName,
    )).toBeNull();
  });

  it('требует колонку-признак', () => {
    expect(discriminatorProblem(variants, { 'АОСР': 'Ид' }, d({ 'АОСР': [AOSR] }, ''), typeName))
      .toContain('колонку');
  });

  it('типы назначены, а маппинга нет — строки дали бы пустой объект', () => {
    expect(discriminatorProblem(variants, {}, d({ 'АОСР': [AOSR] }), typeName))
      .toContain('не задан маппинг');
  });

  it('один тип у двух вариантов — противоречие, и сообщение называет оба заголовками', () => {
    const problem = discriminatorProblem(
      variants,
      { 'АОСР': 'Ид', 'РеестрРабот': 'Ид' },
      d({ 'АОСР': [AOSR], 'РеестрРабот': [AOSR] }),
      typeName,
    );
    expect(problem).toContain('АОСР');
    expect(problem).toContain('Реестр работ');
  });

  it('ни одного назначенного типа — правило ничего не разложит', () => {
    expect(discriminatorProblem(variants, { 'АОСР': 'Ид' }, d({}), typeName))
      .toContain('ни одного типа');
  });
});
