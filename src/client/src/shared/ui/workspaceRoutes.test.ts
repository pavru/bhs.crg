import { describe, it, expect } from 'vitest';
import { clientSources } from '@/shared/testing/clientSources';
import { allPageOwners, constructionPanels } from './constructionPanels';

/**
 * `pageOwners` повторяет маршруты `DocumentSetsPage` руками (ревью PR #1330) — тест не даёт им
 * разойтись. Новый маршрут комплекта, забытый в списке, в пространстве без ИД считался бы «Стройками»
 * и открывался без плашки «вне пространства». Записи панелей карточки стройки следуют из их реестра
 * (plan-02) — тест сверяет и их.
 *
 * Маршрут читается из исходника страницы: `element={set}` — комплект (под `IdModuleOnly`), прочие —
 * стройка и раздел. Параметры заменяются образцами; `:panel` — каждой панелью реестра, хвост `/*` —
 * вложенным адресом панели.
 */
const PAGE = clientSources['/src/features/document-sets/DocumentSetsPage.tsx'];

function routes(): { path: string; set: boolean }[] {
  return [...PAGE.matchAll(/<Route path="([^"]+)" element=\{([^}]+)\}/g)]
    .map(m => ({ path: m[1], set: m[2].trim() === 'set' }));
}

function samples(path: string, set: boolean): { url: string; panel?: string }[] {
  const base = `/document-sets/${path.replace(/:constructionId/, 'c1').replace(/:sectionId/, 'r1').replace(/:setId/, 's1')}`
    .replace(/\/\*$/, '/x/y');
  if (!base.includes(':panel')) return [{ url: base }];
  // Панели комплекта — свои (`SetDetail`), им образцом служат те же сегменты.
  const panels = set ? ['catalog', 'datasets', 'subscribers'] : constructionPanels.map(p => p.id);
  return panels.map(panel => ({ url: base.replace(':panel', panel), panel }));
}

const owner = (url: string) => allPageOwners.find(p => p.match.test(url));

describe('владельцы страниц под адресом «Строек»', () => {
  it('маршруты прочитаны из страницы', () => {
    // Иначе всё ниже проходит на пустом списке.
    expect(routes().filter(r => r.set).length).toBeGreaterThanOrEqual(2);
    expect(routes().filter(r => !r.set).length).toBeGreaterThanOrEqual(4);
    expect(routes().some(r => r.path.endsWith(':panel/*'))).toBe(true);
  });

  it('каждый маршрут комплекта принадлежит модулю ИД', () => {
    const lost = routes().filter(r => r.set).flatMap(r => samples(r.path, true)).filter(s => owner(s.url)?.module !== 'id');
    expect(lost).toEqual([]);
  });

  it('стройка и раздел — «Стройки», а панель — владельцу из её записи в реестре', () => {
    const checked = routes().filter(r => !r.set && r.path !== 'index').flatMap(r => samples(r.path, false));
    expect(checked.some(s => s.panel === 'datasets')).toBe(true);
    for (const { url, panel } of checked) {
      const entry = constructionPanels.find(p => p.id === panel);
      expect(owner(url)?.section, url).toBe(entry?.section);
      expect(owner(url)?.module, url).toBe(entry?.module);
    }
  });
});
