// Отдельным файлом от компонента (issue #858): модуль, экспортирующий и компонент, и
// функцию, не может быть границей горячей подмены — правка поднимается вверх по импортам.

/**
 * Прочитанное — картинка или причина отказа (issue #519).
 *
 * Судим по самой data-URI, а не по `file.type`: это ровно то, что потом проверит `normalize()`, и
 * два разных мерила разошлись бы. Пустой `file.type` (Windows отдаёт его, например, для `.tif`)
 * даёт `data:application/octet-stream` — такой файл поле всё равно не покажет, поэтому честнее
 * сказать об этом вслух и подсказать выход, чем молча оставить поле пустым.
 */
export function checkImageResult(result: unknown, fileName: string): { src: string } | { error: string } {
  if (typeof result !== 'string')
    return { error: `Не удалось прочитать файл «${fileName}».` };
  if (!result.startsWith('data:image'))
    return { error: `«${fileName}» — не изображение или его тип не распознан. Сохраните файл как PNG или JPG.` };
  return { src: result };
}
