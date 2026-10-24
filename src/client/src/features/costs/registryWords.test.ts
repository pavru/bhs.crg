import { describe, expect, it } from 'vitest';
import { REGISTRY_WORDS, sectionLabel } from './registryWords';
import cases from './placeLabelCases.json';

/** Слова и подписи реестра — против общего набора с сервером (`PlaceLabelCasesTests`, COST-20.1). */
describe('registryWords — как сервер называет места разноски', () => {
  it('слова потерянных ссылок и доли на стройку целиком', () => {
    expect(REGISTRY_WORDS).toEqual(cases.words);
  });

  it.each(cases.sections.map(c => [c.label, c] as const))('подпись раздела: %s', (_, c) => {
    expect(sectionLabel(c.site, c.section)).toBe(c.label);
  });
});
