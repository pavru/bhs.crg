import { describe, expect, it } from 'vitest';
import { clientSources } from '@/shared/testing/clientSources';

/**
 * Сторож-реестр «Избранных строек» (issue #1218): КАЖДЫЙ файл клиента, который берёт справочник
 * строек, назван здесь — «фильтрует» или «не фильтрует, потому что…».
 *
 * <p>Зачем. Фильтр ложится на экраны по одному, и новый экран со стройками по умолчанию не фильтрует
 * ничего — молча: шапка скажет «не действуют», но человек, выбравший три стройки, увидит
 * четырнадцать и решит, что фильтр сломан. Реестр делает выбор обязательным: взял справочник —
 * объяснись здесь. И наоборот: файл, который урезает список, обязан звать `useSiteFilter` — иначе
 * шапка над ним врёт.</p>
 *
 * <p>Справочник сам не фильтруется НИКОГДА (#1217): код читает «нет в справочнике» как «удалена».
 * «Фильтрует» значит «урезает свой список объектов по правилу», а не «берёт урезанный справочник».</p>
 *
 * <p>«Берёт справочник» — это имя хука в любом `import { … }` или `export { … } from`, в том числе
 * многострочном, и через обёртку: обёртка называет в `carries` свои хуки, которые несут справочник
 * дальше, и их потребители тоже обязаны быть названы (ревью PR #1331 — `usePlaces` прятал троих).</p>
 */
type Entry = ({ filters: true } | { filters: false; because: string }) & { carries?: string[] };

const REGISTRY: Record<string, Entry> = {
  '/src/features/document-sets/ConstructionsList.tsx': { filters: true },
  '/src/shared/siteFilter/SiteFilterProvider.tsx': {
    filters: false, because: 'это и есть справочник, с которым пересекается избранное',
  },
  '/src/features/document-sets/SetDetail.tsx': {
    filters: false, because: 'справочник уходит в выбор цели переноса и копирования — урезает его useSetTargets (#1219)',
  },
  // Урезана таблица границ (#1220); имена — из полного справочника, закрытие и история — по всем стройкам.
  '/src/features/periods/PeriodsPage.tsx': { filters: true },
  '/src/features/costs/places.ts': {
    filters: false, because: 'обёртка: справочник целей разноски; сама ничего не рисует', carries: ['usePlaces'],
  },
  '/src/features/costs/AllocationMatrix.tsx': {
    filters: false, because: 'варианты разноски урезает PlaceSelect (useSiteChoices, #1219)',
  },
  '/src/features/costs/LineAllocation.tsx': {
    filters: false, because: 'варианты разноски урезает PlaceSelect (useSiteChoices, #1219)',
  },
  '/src/features/costs/InvoiceObject.tsx': {
    filters: false, because: 'объект счёта урезает PlaceSelect (useSiteChoices, #1219)',
  },
  '/src/features/costs/WaybillsPage.tsx': {
    filters: false, because: 'список накладных страничный — отбор параметром sites, #1222',
  },
  // Выбор стройки урезан useSiteChoices (#1219); сам отчёт сворачивает неизбранные строкой, не меняя чисел, — #1223.
  '/src/features/costs/SiteCostsPage.tsx': { filters: true },
  '/src/features/reconciliations/ReconciliationsPage.tsx': {
    filters: false, because: 'сверка — вне первой версии (#1217): справочник только для имён',
  },
  '/src/features/reconciliations/ObservationsPanel.tsx': {
    filters: false, because: 'наблюдения сверки — вне первой версии (#1217): справочник только для имён',
  },
};

/** Где хуки справочника объявлены — эти файлы его не потребляют. */
const DEFINITIONS = ['/src/shared/api/constructions.ts', '/src/shared/api/invoices.ts'];

const DIRECTORY_HOOKS = ['useListConstructions', 'useCostsConstructions', 'useConstructionDirectory'];
/**
 * Хуки, которые урезают список и регистрируют экран: сам фильтр и варианты выбора стройки (#1219).
 * Обёртка, которую сторож не знает, сделала бы честное «фильтрует» недостижимым (ревью PR #1332).
 */
const FILTER_HOOK = /\b(?:useSiteFilter|useSiteChoices)\(/;

/** Имена из `import { … } from` и `export { … } from` — со всеми переносами строк и `as`. */
function namesTaken(code: string): string[] {
  return [...code.matchAll(/\b(?:import|export)\s+(?:type\s+)?\{([^}]*)\}\s*from\b/g)]
    .flatMap(m => m[1].split(','))
    .map(part => part.trim().replace(/^type\s+/, '').split(/\s+as\s+/)[0])
    .filter(Boolean);
}

/** Файлы, берущие справочник — сам хук или хук обёртки. Тесты не в счёт: они его не рисуют. */
function directoryUsers(sources: Record<string, string>, registry: Record<string, Entry>): string[] {
  const hooks = new Set([...DIRECTORY_HOOKS, ...Object.values(registry).flatMap(e => e.carries ?? [])]);
  return Object.entries(sources)
    .filter(([file]) => !/\.test\.tsx?$/.test(file) && !DEFINITIONS.includes(file))
    .filter(([, code]) => namesTaken(code).some(name => hooks.has(name)))
    .map(([file]) => file)
    .sort();
}

describe('сторож-реестр избранных строек', () => {
  const users = directoryUsers(clientSources, REGISTRY);

  it('видит потребителей справочника', () => {
    // Положительный якорь: сломайся поиск — проверки ниже прошли бы на пустом списке.
    expect(users).toContain('/src/features/document-sets/ConstructionsList.tsx');
    expect(users).toContain('/src/features/costs/LineAllocation.tsx');
  });

  it('видит многострочный импорт, реэкспорт и обёртку', () => {
    // Самопроверка на выдуманных файлах: сторож, зелёный всегда, хуже отсутствующего.
    const fake = {
      '/src/a.tsx': "import {\n  useGetConstruction,\n  useListConstructions,\n} from '@/shared/api/constructions';",
      '/src/b.ts': "export { useCostsConstructions as useSites } from '@/shared/api/invoices';",
      '/src/c.tsx': "import { usePlaces } from './places';",
      '/src/d.tsx': "import { useGetConstruction } from '@/shared/api/constructions';",
    };
    expect(directoryUsers(fake, { '/src/places.ts': { filters: false, because: '—', carries: ['usePlaces'] } }))
      .toEqual(['/src/a.tsx', '/src/b.ts', '/src/c.tsx']);
  });

  it('каждый файл со справочником строек назван в реестре', () => {
    const unnamed = users.filter(f => !(f in REGISTRY));
    expect(unnamed, 'Файл берёт справочник строек, но не сказал, фильтрует ли он список по избранному. '
      + 'Впишите его в REGISTRY: { filters: true } — и зовите useSiteFilter() или useSiteChoices(), — или { filters: false, because }.')
      .toEqual([]);
  });

  it('в реестре нет файлов, которые справочник больше не берут', () => {
    expect(Object.keys(REGISTRY).filter(f => !users.includes(f))).toEqual([]);
  });

  it('«фильтрует» значит зовёт useSiteFilter или useSiteChoices, «не фильтрует» — не зовёт', () => {
    const wrong = Object.entries(REGISTRY)
      .filter(([file, entry]) => FILTER_HOOK.test(clientSources[file] ?? '') !== entry.filters)
      .map(([file, entry]) => `${file}: в реестре ${entry.filters ? '«фильтрует»' : '«не фильтрует»'}`);
    expect(wrong).toEqual([]);
  });
});
