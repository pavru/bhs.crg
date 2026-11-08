import { extensionOf } from '@/shared/api/templateAssetRef';
import { sizeLimitWords, type FileKindsInfo } from '@/shared/api/fileKinds';

/**
 * Файл сметы ГРАНД-Сметы: OpenXML — `.gsfx` (архив) или голый `.xml`. Вид документа (ЛСР, ВОР, КА) сервер
 * узнаёт по содержимому, поэтому `ESTIMATE_ACCEPT` — только фильтр окна выбора.
 */
export const ESTIMATE_ACCEPT = '.gsfx,.xml';

/**
 * Расширение — хвост после точки из латиницы и цифр, с буквой, до пяти знаков. Иначе точка — часть имени:
 * «ЛСР ред.2», «смета v1.5», «Корпус.А» сохранены без расширения, а не с расширением «2» или «а».
 */
const EXTENSION = /^(?=.*[a-z])[a-z0-9]{1,5}$/;

/**
 * Проверка площадки окна загрузки ([UI-89], design D3 `sharedui-file-drop-zone`): причина без имени или
 * `null`. В духе `precheck` пакета сканов: отказ только тому, что точно не годится. Чужое расширение —
 * «не .gsfx или .xml»; имени без расширения отказа нет — файл идёт на сервер, вид он узнает по содержимому.
 * Предел размера — реестра видов файлов (`maxBytes`): он же у адреса загрузки сметы (`MaxFileBytes`). Без
 * реестра размер не судится — решит сервер.
 */
export function estimateFileRefusal(file: File, kinds?: FileKindsInfo): string | null {
  const ext = extensionOf(file.name).slice(1).toLowerCase();
  if (EXTENSION.test(ext) && ext !== 'gsfx' && ext !== 'xml') return 'не .gsfx или .xml';
  if (file.size === 0) return 'файл пуст';
  if (kinds && file.size > kinds.maxBytes) return `больше ${sizeLimitWords(kinds)}`;
  return null;
}

/** Подпись площадки — вид и, когда реестр прочитан, предел: «.gsfx или .xml · до 50 МБ». */
export const estimateFileHint = (kinds?: FileKindsInfo) =>
  kinds ? `.gsfx или .xml · до ${sizeLimitWords(kinds)}` : '.gsfx или .xml';
