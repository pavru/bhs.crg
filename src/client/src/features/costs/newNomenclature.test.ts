import { describe, expect, it } from 'vitest';
import { existingPosition, type IntakeKind } from '@/shared/api/nomenclatureIntake';
import {
  createBody, defaultKind, missingFields, prefill, prefillHint, similarBody, typedVsCard,
} from './newNomenclature';
import type { CardLine } from '@/shared/api/nomenclatureCard';

const kind = (patch: Partial<IntakeKind> = {}): IntakeKind => ({
  typeId: 't-1', code: 'Номенклатура', name: 'Номенклатура', refusals: [],
  fields: [
    { key: 'Наименование', title: 'Наименование', required: true, identity: true, options: null },
    { key: 'Производитель', title: 'Производитель', required: false, identity: true, options: null },
    { key: 'Артикул', title: 'Артикул', required: false, identity: true, options: null },
    {
      key: 'ЕдиницаИзмерения', title: 'Единица измерения', required: true, identity: false,
      options: [{ id: 'u-1', name: 'шт' }, { id: 'u-2', name: 'м' }],
    },
  ],
  ...patch,
});

describe('новая позиция номенклатуры из строки счёта', () => {
  it('вид по умолчанию — единственный годный; из двух выбирает человек', () => {
    const bad = kind({ typeId: 't-2', refusals: ['обязательны поля…'] });
    expect(defaultKind([kind(), bad])?.typeId).toBe('t-1');
    expect(defaultKind([kind(), kind({ typeId: 't-3' })])).toBeNull();
    expect(defaultKind([bad])).toBeNull();
  });

  it('из строки подставляются наименование, артикул и единица, которая есть в справочнике', () => {
    expect(prefill(kind(), { name: ' Кабель 3х2,5 ', code: 'RZ-2W', unit: 'ШТ.' })).toEqual({
      values: { Наименование: 'Кабель 3х2,5', Артикул: 'RZ-2W' },
      refs: { ЕдиницаИзмерения: 'u-1' },
    });
    // Единицы «упак» в справочнике нет — поле остаётся пустым, а не берёт первую попавшуюся.
    expect(prefill(kind(), { name: 'Кабель', unit: 'упак' }).refs).toEqual({});
    expect(prefill(kind(), {})).toEqual({ values: {}, refs: {} });
  });

  it('поле, сопоставленное тэгом, берёт артикул и единицу; без тэга — по названию, как прежде', () => {
    // Артикул лежит в «Коде поставщика», а поле «Артикул» значит другое — тэг решает, не название.
    const tagged = kind({
      fields: [
        ...kind().fields.filter(f => f.key !== 'ЕдиницаИзмерения'),
        { key: 'Код', title: 'Код поставщика', required: false, identity: true, options: null, tag: 'nomenclature.article' },
        { key: 'Тара', title: 'Тара', required: false, identity: false, options: [{ id: 'p-1', name: 'шт' }] },
        {
          key: 'ЕдиницаИзмерения', title: 'Единица измерения', required: true, identity: false,
          options: [{ id: 'u-1', name: 'шт' }, { id: 'u-2', name: 'м' }], tag: 'nomenclature.unit',
        },
      ],
      mapped: ['nomenclature.article', 'nomenclature.unit'],
    });
    expect(prefill(tagged, { name: 'Кабель', code: 'RZ-2W', unit: 'шт' })).toEqual({
      values: { Наименование: 'Кабель', Код: 'RZ-2W' },
      refs: { ЕдиницаИзмерения: 'u-1' },
    });
    // Без тэга «шт» легла бы и в «Тару»: поле выбора, где нашлась такая запись.
    const untagged = kind({ fields: tagged.fields.map(f => ({ ...f, tag: null })), mapped: [] });
    expect(prefill(untagged, { unit: 'шт' }).refs).toEqual({ Тара: 'p-1', ЕдиницаИзмерения: 'u-1' });
  });

  it('тэг стоит на поле, которого окно не спрашивает, — по названию поле не угадывается', () => {
    // Размечены «Код изготовителя» и единица, но оба поля необязательные и не в ключе — окно их не
    // спрашивает. «Артикул поставщика» по названию похож, но это не то поле, что размечено (ревью #1525).
    const elsewhere = kind({
      fields: [
        { key: 'Наименование', title: 'Наименование', required: true, identity: true, options: null },
        { key: 'АртикулПоставщика', title: 'Артикул поставщика', required: false, identity: true, options: null },
        { key: 'Тара', title: 'Тара', required: true, identity: false, options: [{ id: 'p-1', name: 'шт' }, { id: 'p-2', name: 'уп' }] },
      ],
      mapped: ['nomenclature.article', 'nomenclature.unit'],
    });

    expect(prefill(elsewhere, { name: 'Кабель', code: 'RZ-2W', unit: 'шт' })).toEqual({
      values: { Наименование: 'Кабель' },
      refs: {},
    });
    // Тот же вид без разметки — по-прежнему по названию и в любое поле выбора.
    expect(prefill({ ...elsewhere, mapped: [] }, { name: 'Кабель', code: 'RZ-2W', unit: 'шт' })).toEqual({
      values: { Наименование: 'Кабель', АртикулПоставщика: 'RZ-2W' },
      refs: { Тара: 'p-1' },
    });
  });

  it('подставленное из счёта оговорено, набираемое с нуля — нет', () => {
    const k = kind();
    expect(prefillHint(k, k.fields[0], { name: 'Кабель' })).toContain('слова поставщика');
    expect(prefillHint(k, k.fields[2], { code: 'RZ-2W' })).toContain('внутренний код поставщика');
    expect(prefillHint(k, k.fields[1], { name: 'Кабель', code: 'RZ-2W' })).toBeNull();
    expect(prefillHint(k, k.fields[0], {})).toBeNull();
  });

  it('о похожих спрашивают полями ключа, и только когда есть название', () => {
    expect(similarBody(kind(), { Артикул: 'RZ-2W' })).toBeNull();
    expect(similarBody(kind(), { Наименование: ' Кабель ', Артикул: 'RZ-2W', Производитель: ' ' })).toEqual({
      typeId: 't-1', values: { Наименование: 'Кабель', Артикул: 'RZ-2W' },
    });
  });

  it('отказ «такая уже есть» отдаёт лежащую позицию, а любой другой отказ — нет', () => {
    const existing = { id: 'n-1', name: 'Кабель', type: 'Номенклатура', archived: false };
    expect(existingPosition({ response: { data: { code: 'exists', error: 'Такая уже есть', existing } } })).toEqual(existing);
    expect(existingPosition({ response: { data: { error: 'Не заполнено: «Наименование».' } } })).toBeNull();
    expect(existingPosition(new Error('сеть'))).toBeNull();
  });

  it('до создания названо, чего не хватает; пустые поля в запрос не едут', () => {
    expect(missingFields(kind(), { values: {}, refs: {} })).toEqual(['Наименование', 'Единица измерения']);
    const draft = { values: { Наименование: 'Кабель', Артикул: '  ' }, refs: { ЕдиницаИзмерения: 'u-2' } };
    expect(missingFields(kind(), draft)).toEqual([]);
    expect(createBody(kind(), draft)).toEqual({
      typeId: 't-1', values: { Наименование: 'Кабель' }, refs: { ЕдиницаИзмерения: 'u-2' },
    });
  });
});

describe('набрано ↔ в справочнике (#1517)', () => {
  const line = (key: string, text: string | null): CardLine => ({
    key, title: key, text, kind: 'text', identity: true, inheritedFrom: null, unreadable: false, archived: false,
  });
  const card = {
    name: 'Выключатель AV-10',
    lines: [line('Наименование', 'Выключатель AV-10'), line('Производитель', 'EKF'), line('Артикул', null)],
  };

  it('сравнивает поля ключа по ключу; регистр и пробелы не в счёт; ненабранное не сравнивается', () => {
    const draft = { values: { 'Наименование': 'Выключатель AV-10 6A', 'Производитель': ' ekf ', 'Артикул': 'mcb10' }, refs: {} };
    expect(typedVsCard(kind(), draft, card)).toEqual([
      { key: 'Наименование', label: 'Наименование', text: 'отличается — набрано «Выключатель AV-10 6A»', tone: 'differs' },
      { key: 'Производитель', label: 'Производитель', text: 'совпадает: EKF', tone: 'ok' },
      { key: 'Артикул', label: 'Артикул', text: 'в справочнике не заполнено', tone: 'plain' },
    ]);
    expect(typedVsCard(kind(), { values: { 'Наименование': 'выключатель  av-10' }, refs: {} }, card))
      .toEqual([{ key: 'Наименование', label: 'Наименование', text: 'совпадает: Выключатель AV-10', tone: 'ok' }]);
  });

  it('поле не из ключа не сравнивается', () => {
    const draft = { values: { 'Наименование': 'Выключатель AV-10' }, refs: { 'ЕдиницаИзмерения': 'u-1' } };
    expect(typedVsCard(kind(), draft, card).map(r => r.label)).toEqual(['Наименование']);
  });

  it('поле названия пусто в данных — сравнивается с именем записи: им запись и названа', () => {
    const named = { name: 'Кабель прогона 018746', lines: [line('Наименование', null), line('Производитель', null)] };
    const draft = { values: { 'Наименование': 'кабель прогона 018746', 'Производитель': 'EKF' }, refs: {} };
    expect(typedVsCard(kind(), draft, named)).toEqual([
      { key: 'Наименование', label: 'Наименование', text: 'совпадает: Кабель прогона 018746', tone: 'ok' },
      { key: 'Производитель', label: 'Производитель', text: 'в справочнике не заполнено', tone: 'plain' },
    ]);
  });

  it('не прочитанное сервером — не «не заполнено»', () => {
    const unread = { ...line('Производитель', null), unreadable: true };
    const draft = { values: { 'Наименование': 'Выключатель AV-10', 'Производитель': 'EKF' }, refs: {} };
    expect(typedVsCard(kind(), draft, { ...card, lines: [card.lines[0], unread] })[1]).toEqual({
      key: 'Производитель', label: 'Производитель', text: 'в справочнике не прочитано — сверить не удалось', tone: 'plain',
    });
  });
});
