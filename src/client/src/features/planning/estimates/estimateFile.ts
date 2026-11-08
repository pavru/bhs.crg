/**
 * Файл сметы ГРАНД-Сметы: OpenXML — `.gsfx` (архив) или голый `.xml`. Вид документа (ЛСР, ВОР, КА) сервер
 * узнаёт по содержимому, поэтому `ESTIMATE_ACCEPT` — только фильтр окна выбора.
 */
export const ESTIMATE_ACCEPT = '.gsfx,.xml';

/**
 * Проверка площадки окна загрузки ([UI-89], design D3 `sharedui-file-drop-zone`): причина без имени или
 * `null`. В духе `precheck` пакета сканов: отказ только тому, что точно не годится. Чужое расширение —
 * «не .gsfx или .xml»; имени без расширения отказа нет — файл идёт на сервер, вид он узнает по содержимому.
 */
export function estimateFileRefusal(file: File): string | null {
  const dot = file.name.lastIndexOf('.');
  const ext = dot < 0 ? '' : file.name.slice(dot + 1).toLowerCase();
  if (ext !== '' && ext !== 'gsfx' && ext !== 'xml') return 'не .gsfx или .xml';
  if (file.size === 0) return 'файл пуст';
  return null;
}
