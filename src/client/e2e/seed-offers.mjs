const seedDate = '2026-11-02';

/** Посев одного черновика по первой синтетической редакции для чтения реестра. */
export async function seedOffer({ api, estimateId }) {
  if (!estimateId) return null;
  const estimate = await api('GET', `/plan/estimates/${estimateId}`);
  const revision = estimate.revisions.find(item => item.number === 1);
  if (!revision) throw new Error('В смете посева нет редакции 1 для КП.');

  let cursor;
  do {
    const params = new URLSearchParams({ estimateId, limit: '100' });
    if (cursor) params.set('cursor', cursor);
    const page = await api('GET', `/plan/offers?${params}`);
    const found = page.items.find(row => row.revision?.id === revision.id && row.date === seedDate);
    if (found) return found.id;
    cursor = page.next;
  } while (cursor);

  const offer = await api('POST', '/plan/offers', { revisionId: revision.id, date: seedDate, markup: 8 });
  console.log(`  + КП ${offer.number} по первой редакции сметы (посев)`);
  return offer.id;
}
