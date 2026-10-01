import { describe, expect, it } from 'vitest';
import { NO_PLACE, choiceOf, fromChoice, placeName, samePlace } from './places';

const places = {
  sites: [{ id: 's1', name: 'Комарова 36', sections: [{ id: 'r1', name: '4 эт.' }] }],
  articles: [{ id: 'a1', name: 'Склад' }],
};

describe('цель разноски', () => {
  it('выбор помнит, что выбрано — стройка или статья', () => {
    expect(fromChoice(choiceOf({ construction: 's1', section: null, article: null })))
      .toEqual({ construction: 's1', section: null, article: null });
    expect(fromChoice(choiceOf({ construction: null, section: null, article: 'a1' })))
      .toEqual({ construction: null, section: null, article: 'a1' });
    expect(fromChoice('')).toEqual(NO_PLACE);
  });

  it('стройка и статья с одинаковым идентификатором — разные цели', () => {
    expect(samePlace({ construction: 'x', section: null, article: null }, { construction: null, section: null, article: 'x' }))
      .toBe(false);
  });

  it('название: стройка с разделом, статья, удалённая статья', () => {
    expect(placeName({ construction: 's1', section: 'r1', article: null }, places)).toBe('Комарова 36 / 4 эт.');
    expect(placeName({ construction: null, section: null, article: 'a1' }, places)).toBe('Склад');
    expect(placeName({ construction: null, section: null, article: 'нет' }, places)).toBe('статья удалена');
  });
});
