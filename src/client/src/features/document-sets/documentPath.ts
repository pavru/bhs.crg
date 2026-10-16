/**
 * Адрес документа комплекта (issue #1384). Один на все входы: строка комплекта, поиск, замечания.
 * Отдельным файлом от страницы: модуль с компонентом и функцией не граница горячей подмены (#858).
 */
export const documentPath = (constructionId: string, setId: string, instanceId: string) =>
  documentUnder(`/document-sets/${constructionId}/sets/${setId}`, instanceId);

/** Тот же адрес от готового адреса комплекта — у ссылок замечаний есть только он. */
export const documentUnder = (setPath: string, instanceId: string) => `${setPath}/docs/${instanceId}`;

/**
 * Пометка перехода «из комплекта» (`state` навигации): выход из документа тогда — шаг истории
 * назад, к тому же месту комплекта, а не новая запись (ревью PR #1388).
 */
export const FROM_SET = { from: 'set' } as const;
