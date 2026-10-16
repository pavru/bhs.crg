/**
 * Адрес документа комплекта (issue #1384). Один на все входы: строка комплекта, поиск, замечания.
 * Отдельным файлом от страницы: модуль с компонентом и функцией не граница горячей подмены (#858).
 */
export const documentPath = (constructionId: string, setId: string, instanceId: string) =>
  `/document-sets/${constructionId}/sets/${setId}/docs/${instanceId}`;
