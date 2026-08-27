import { describe, it, expect } from 'vitest';
import { nameOf, matchesLink } from './qualityLinkSearch';
import type { MaterialQualityLink } from '@/shared/api/qualityDocs';

function link(over: Partial<MaterialQualityLink>): MaterialQualityLink {
  return {
    id: 'l1', scope: 'System', scopeId: null,
    materialKey: 'КВВГнг-1х2.5', materialLabel: null,
    qualityDocumentId: 'q1', qualityDocumentName: 'Сертификат',
    ...over,
  } as MaterialQualityLink;
}

describe('имя связки материала (issue #554)', () => {
  it('берёт метку, когда она есть', () => {
    expect(nameOf(link({ materialLabel: 'Кабель КВВГ' }))).toBe('Кабель КВВГ');
  });

  it('падает на машинный ключ у старых связок без метки', () => {
    expect(nameOf(link({ materialLabel: null }))).toBe('КВВГнг-1х2.5');
    expect(nameOf(link({ materialLabel: '' }))).toBe('КВВГнг-1х2.5');
  });

  it('метку из одних пробелов именем не считает', () => {
    expect(nameOf(link({ materialLabel: '   ' }))).toBe('КВВГнг-1х2.5');
  });
});

describe('поиск по связке', () => {
  it('ищет и по ключу, и по метке — артикул одним, человеческое имя другим', () => {
    const l = link({ materialKey: 'КВВГнг-1х2.5', materialLabel: 'Кабель силовой' });
    expect(matchesLink(l, 'квввг')).toBe(false);
    expect(matchesLink(l, '1х2.5')).toBe(true);
    expect(matchesLink(l, 'силовой')).toBe(true);
  });

  it('регистр не важен (запрос приходит уже в нижнем)', () => {
    expect(matchesLink(link({ materialKey: 'ABC-1' }), 'abc')).toBe(true);
  });

  it('связка без метки ищется по ключу и не падает', () => {
    expect(matchesLink(link({ materialLabel: null }), 'квв')).toBe(true);
    expect(matchesLink(link({ materialLabel: null }), 'нет такого')).toBe(false);
  });
});
