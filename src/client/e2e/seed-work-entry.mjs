import assert from 'node:assert/strict';
import { callApiOk } from './harness.mjs';

export const WORK_ENTRY_TYPE = 'Прокладка кабеля (веб-ввод)';
export const WORK_ENTRY_VARIANT = 'В перфорированном лотке (веб-ввод)';
export const WORK_ENTRY_MATERIAL = 'Кабель ВВГнг-LS 3х2,5 (посев)';

const ref = entry => ({ $ref: 'catalog', entryId: entry.id, displayName: entry.displayName });

// Классификатор из объявления ядра появляется при перезапуске после общего посева единиц.
export async function seedWorkEntryCatalog(page) {
  const get = path => callApiOk(page, 'GET', path);
  const types = await get('/document-types');
  const directory = types.find(type => type.code === 'ЕдиницаИзмерения');
  assert.ok(directory, 'Общий посев должен завести справочник единиц измерения.');
  const units = await get(`/common-data?purpose=choice&typeId=${directory.id}`);
  const metre = units.find(unit => unit.displayName === 'м');
  assert.ok(metre, 'Общий посев должен завести единицу «м».');
  const works = await get(`/work-types?purpose=choice&q=${encodeURIComponent(WORK_ENTRY_TYPE)}`);
  const type = works.find(entry => entry.displayName === WORK_ENTRY_TYPE) ?? await callApiOk(page, 'POST', '/work-types', {
    displayName: WORK_ENTRY_TYPE,
    data: JSON.stringify({ Код: 'E2E-WEB-ENTRY', Наименование: WORK_ENTRY_TYPE,
      ЕдиницаИзмерения: ref(metre), ТребуетМатериалы: true }),
  });
  const variants = await get(`/work-variants?purpose=choice&workTypeId=${type.id}`);
  const variant = variants.find(entry => entry.displayName === WORK_ENTRY_VARIANT) ?? await callApiOk(page, 'POST', '/work-variants', {
    displayName: WORK_ENTRY_VARIANT,
    data: JSON.stringify({ ВидРаботы: ref(type), Наименование: WORK_ENTRY_VARIANT }),
  });
  return { typeId: type.id, variantId: variant.id, unitId: metre.id };
}
