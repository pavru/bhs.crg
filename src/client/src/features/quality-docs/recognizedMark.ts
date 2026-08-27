// Отдельным файлом от компонента (issue #858): модуль, экспортирующий и компонент, и
// функцию, не может быть границей горячей подмены — правка поднимается вверх по импортам.

/** Подсказка у помеченного поля — одна на все места, где пометка рисуется (issue #807). */
export const RECOGNIZED_HINT = 'Заполнено распознаванием — проверьте';

/**
 * Пути, которые распознавание РЕАЛЬНО подставит: пустые значения не применяются, значит и пометки
 * не заслуживают (issue #807). Правило одно на подстановку и на пометку — вычислив «что тронуто»
 * отдельно, мы завели бы вторую копию, и она разошлась бы с первой на первом же частном случае.
 */
export function recognizedPaths(flat: Record<string, string>): string[] {
  return Object.entries(flat)
    .filter(([, val]) => val != null && String(val).trim() !== '')
    .map(([path]) => path);
}

/**
 * Ключи полей ВЕРХНЕГО уровня, затронутые распознаванием, — по ним форма помечает поля. Верхнего,
 * потому что помечается поле целиком: у составного значения («Организация.ИНН») отдельной рамки нет,
 * да и проверять человек будет весь блок.
 */
export function recognizedFieldKeys(flat: Record<string, string>): Set<string> {
  return new Set(recognizedPaths(flat).map(path => path.split('.')[0]));
}

/** Раскладывает плоские значения (путь через точку) во вложенный объект и сливает с текущими. */
export function applyRecognized(values: Record<string, unknown>, flat: Record<string, string>): Record<string, unknown> {
  const next: Record<string, unknown> = JSON.parse(JSON.stringify(values ?? {}));
  for (const path of recognizedPaths(flat)) {
    const val = flat[path];
    const parts = path.split('.');
    let cur = next;
    for (let i = 0; i < parts.length - 1; i++) {
      const k = parts[i];
      if (typeof cur[k] !== 'object' || cur[k] == null) cur[k] = {};
      cur = cur[k] as Record<string, unknown>;
    }
    cur[parts[parts.length - 1]] = val;
  }
  return next;
}
