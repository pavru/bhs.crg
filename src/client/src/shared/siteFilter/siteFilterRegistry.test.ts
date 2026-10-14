import { describe, expect, it } from 'vitest';
import { clientSources } from '@/shared/testing/clientSources';

/**
 * Сторож-реестр «Избранных строек» (issue #1218): КАЖДЫЙ файл клиента, который берёт справочник
 * строек, назван здесь — «фильтрует» или «не фильтрует, потому что…».
 *
 * <p>Зачем. Фильтр ложится на экраны по одному, и новый экран со стройками по умолчанию не фильтрует
 * ничего — молча: шапка скажет «не действуют», но человек, выбравший три стройки, увидит
 * четырнадцать и решит, что фильтр сломан. Реестр делает выбор обязательным: импортировал хук
 * справочника — объяснись здесь. И наоборот: файл, который урезает список, обязан звать
 * `useSiteFilter` — иначе шапка над ним врёт.</p>
 *
 * <p>Справочник сам не фильтруется НИКОГДА (#1217): код читает «нет в справочнике» как «удалена».
 * «Фильтрует» значит «урезает свой список объектов по правилу», а не «берёт урезанный справочник».</p>
 */
type Entry = { filters: true } | { filters: false; because: string };

const REGISTRY: Record<string, Entry> = {
  '/src/features/document-sets/ConstructionsList.tsx': { filters: true },
  '/src/shared/siteFilter/SiteFilterProvider.tsx': {
    filters: false, because: 'это и есть справочник, с которым пересекается избранное',
  },
  '/src/features/document-sets/SetDetail.tsx': {
    filters: false, because: 'справочник уходит в выбор цели переноса и копирования — общий компонент выбора стройки, #1219',
  },
  '/src/features/periods/PeriodsPage.tsx': {
    filters: false, because: 'имена строек из полного справочника; таблица периода урезается шагом #1220',
  },
  '/src/features/costs/places.ts': {
    filters: false, because: 'варианты разноски — общий компонент выбора стройки, #1219',
  },
  '/src/features/costs/WaybillsPage.tsx': {
    filters: false, because: 'список накладных страничный — отбор параметром sites, #1222',
  },
  '/src/features/costs/SiteCostsPage.tsx': {
    filters: false, because: 'отчёт сворачивает неизбранные строкой, не меняя чисел, — #1223',
  },
  '/src/features/reconciliations/ReconciliationsPage.tsx': {
    filters: false, because: 'сверка — вне первой версии (#1217): справочник только для имён',
  },
  '/src/features/reconciliations/ObservationsPanel.tsx': {
    filters: false, because: 'наблюдения сверки — вне первой версии (#1217): справочник только для имён',
  },
};

/** Где хуки справочника объявлены — эти файлы его не потребляют. */
const DEFINITIONS = ['/src/shared/api/constructions.ts', '/src/shared/api/invoices.ts'];

const DIRECTORY_HOOK = /\b(useListConstructions|useCostsConstructions)\b/;
const FILTER_HOOK = /\buseSiteFilter\(\)/;

/** Файлы, импортирующие хук справочника. Сами тесты не в счёт: они его не рисуют. */
function directoryUsers(sources: Record<string, string>): string[] {
  return Object.entries(sources)
    .filter(([file]) => !file.endsWith('.test.ts') && !file.endsWith('.test.tsx') && !DEFINITIONS.includes(file))
    .filter(([, code]) => code.split('\n').some(line => line.startsWith('import') && DIRECTORY_HOOK.test(line)))
    .map(([file]) => file)
    .sort();
}

describe('сторож-реестр избранных строек', () => {
  const users = directoryUsers(clientSources);

  it('видит потребителей справочника', () => {
    // Положительный якорь: сломайся поиск — проверки ниже прошли бы на пустом списке.
    expect(users).toContain('/src/features/document-sets/ConstructionsList.tsx');
  });

  it('каждый файл со справочником строек назван в реестре', () => {
    const unnamed = users.filter(f => !(f in REGISTRY));
    expect(unnamed, 'Файл берёт справочник строек, но не сказал, фильтрует ли он список по избранному. '
      + 'Впишите его в REGISTRY: { filters: true } — и зовите useSiteFilter(), — или { filters: false, because }.')
      .toEqual([]);
  });

  it('в реестре нет файлов, которые справочник больше не берут', () => {
    expect(Object.keys(REGISTRY).filter(f => !users.includes(f))).toEqual([]);
  });

  it('«фильтрует» значит зовёт useSiteFilter, «не фильтрует» — не зовёт', () => {
    const wrong = Object.entries(REGISTRY)
      .filter(([file, entry]) => FILTER_HOOK.test(clientSources[file] ?? '') !== entry.filters)
      .map(([file, entry]) => `${file}: в реестре ${entry.filters ? '«фильтрует»' : '«не фильтрует»'}`);
    expect(wrong).toEqual([]);
  });
});
