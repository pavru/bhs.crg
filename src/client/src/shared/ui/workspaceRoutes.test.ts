import { describe, it, expect } from 'vitest';
import { clientSources } from '@/shared/testing/clientSources';
import { pageOwners } from './navConfig';

/**
 * `pageOwners` повторяет маршруты `DocumentSetsPage` руками (ревью PR #1330) — тест не даёт им
 * разойтись. Новый маршрут комплекта, забытый в списке, в пространстве без ИД считался бы «Стройками»
 * и открывался без плашки «вне пространства».
 *
 * Маршрут читается из исходника страницы: `element={set}` — комплект (под `IdModuleOnly`), прочие —
 * стройка и раздел. Параметры заменяются образцами; `:panel` — каждым из известных значений.
 */
const PAGE = clientSources['/src/features/document-sets/DocumentSetsPage.tsx'];

function routes(): { path: string; set: boolean }[] {
  return [...PAGE.matchAll(/<Route path="([^"]+)" element=\{([^}]+)\}/g)]
    .map(m => ({ path: m[1], set: m[2].trim() === 'set' }));
}

function samples(path: string): string[] {
  const base = `/document-sets/${path.replace(/:constructionId/, 'c1').replace(/:sectionId/, 'r1').replace(/:setId/, 's1')}`;
  return base.includes(':panel') ? ['catalog', 'datasets', 'subscribers'].map(p => base.replace(':panel', p)) : [base];
}

const owner = (url: string) => pageOwners.find(p => p.match.test(url));

describe('владельцы страниц под адресом «Строек»', () => {
  it('маршруты прочитаны из страницы', () => {
    // Иначе всё ниже проходит на пустом списке.
    expect(routes().filter(r => r.set).length).toBeGreaterThanOrEqual(2);
    expect(routes().filter(r => !r.set).length).toBeGreaterThanOrEqual(4);
  });

  it('каждый маршрут комплекта принадлежит модулю ИД', () => {
    const lost = routes().filter(r => r.set).flatMap(r => samples(r.path)).filter(url => owner(url)?.module !== 'id');
    expect(lost).toEqual([]);
  });

  it('стройка и раздел — «Стройки», кроме панели наборов данных', () => {
    for (const url of routes().filter(r => !r.set && r.path !== 'index').flatMap(r => samples(r.path))) {
      const expected = url.endsWith('/datasets') ? 'datasets' : undefined;
      expect(owner(url)?.section, url).toBe(expected);
      expect(owner(url)?.module, url).toBeUndefined();
    }
  });
});
