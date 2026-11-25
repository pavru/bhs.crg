import assert from 'node:assert/strict';
import { importFile } from './seed-estimates.mjs';
import { findDirectory } from './seed-invoices.mjs';

const ESTIMATE = 'Смета подсказки материалов (посев Work-26)';
const WAYBILL = 'Посев Work-26 · отпуск';
export const WORK_HINT_MATERIALS = {
  issued: 'Кабель ВВГнг-LS 3х2,5 (посев)',
  estimate: 'Труба гофрированная 20 мм (посев)',
  catalog: 'Лоток лестничный 100х50 (посев)',
};

export async function seedWorkMaterials({ api, apiBase, token, findType, site, line }) {
  const type = await findDirectory(api, findType, 'role.nomenclature', 'Номенклатура');
  assert.ok(type, 'Для Work-26 нужен справочник номенклатуры из общего посева.');
  const names = await api('GET', `/common-data?purpose=choice&typeId=${type.id}`);
  const position = tier => {
    const item = names.find(entry => entry.displayName === WORK_HINT_MATERIALS[tier]);
    assert.ok(item, `Не найдена позиция посева «${WORK_HINT_MATERIALS[tier]}».`);
    return item.id;
  };

  const bills = await api('GET', `/costs/waybills?constructionId=${site.id}`);
  let bill = bills.items.find(item => item.number === WAYBILL);
  if (!bill) {
    const assigned = await api('GET', `/work/assignments?constructionId=${site.id}`);
    bill = await api('POST', '/costs/waybills', { number: WAYBILL, construction: site.id, issuedOn: assigned.construction.today });
  }
  if (bill.state !== 'Posted') {
    const path = `/costs/waybills/${bill.id}`;
    if (!bill.version) bill = await api('GET', path);
    const filled = await api('PUT', `${path}/lines`, { lines: [{ sourceText: WORK_HINT_MATERIALS.issued,
      nomenclature: { $ref: 'catalog', entryId: position('issued') }, unit: 'м', quantity: 10 }] }, { 'If-Match': bill.version });
    await api('POST', `${path}/posted`, {}, { 'If-Match': filled.version });
  }

  const estimates = await api('GET', `/plan/estimates?constructionId=${site.id}`);
  const existing = estimates.items.find(item => item.name === ESTIMATE);
  const estimate = existing ? await api('GET', `/plan/estimates/${existing.id}`)
    : (await importFile({ api, apiBase, token, form: {
        constructionId: site.id, 'newEstimate.name': ESTIMATE, 'newEstimate.kind': 'main',
      } })).estimate;
  const revision = estimate.revisions.find(item => item.id === estimate.currentRevisionId);
  const path = `/plan/revisions/${revision.id}`;
  const rows = await api('GET', `${path}/rows`);
  const row = rows.items.find(item => item.kind === 'work' && item.rateCode === 'ГЭСНм08-02-409-01');
  assert.ok(row?.materials.length, 'В позиции посевной сметы нет ресурса.');
  let version = revision.version;
  if (row.workTypeId !== line.workTypeId || row.unitId !== line.unitId) {
    ({ version } = await api('PUT', `${path}/lines/${row.id}/match`, { workTypeId: line.workTypeId,
      unitId: line.unitId, factor: 100, remember: false }, { 'If-Match': version }));
  }
  const resource = row.materials[0];
  if (resource.nomenclatureId !== position('estimate')) {
    await api('PUT', `${path}/resources/${resource.id}/match`, { nomenclatureId: position('estimate'),
      remember: false }, { 'If-Match': version });
  }
}
