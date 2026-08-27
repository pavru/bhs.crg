import type { MaterialQualityLink } from '@/shared/api/qualityDocs';

// Отдельным файлом от компонента (issue #858): модуль, экспортирующий и компонент, и
// функцию, не может быть границей горячей подмены — правка поднимается вверх по импортам.

/** Имя материала: метка, если она есть, иначе машинный ключ (у связок до #554 метки нет). */
export function nameOf(link: MaterialQualityLink): string {
  return link.materialLabel?.trim() || link.materialKey;
}

/** Поиск идёт по ключу И по метке: артикул ищут одним, человеческое имя — другим. */
export function matchesLink(link: MaterialQualityLink, lowerQuery: string): boolean {
  return link.materialKey.toLowerCase().includes(lowerQuery)
    || (link.materialLabel ?? '').toLowerCase().includes(lowerQuery);
}
