import type { InvoiceImage } from '@/shared/api/invoiceImage';
import type { PrintProfile } from '@/shared/api/printProfiles';

/**
 * Раскладка вида для чтения у счёта (issue #1456): что сказать о профиле печати и когда давать его
 * выбрать. Тексты — по разбору Дизайнера (2026-10-18); вид ответа — `InvoiceImage.layout`.
 *
 * <p>Профиль касается только книги `.xlsx` без своей раскладки (`layout === 'profile'`). Книге со своей
 * раскладкой и всему остальному о профиле не говорится ничего, кроме подсказки «из самого файла» —
 * выбор там невыполним, а строка на каждом счёте из 1С была бы шумом.</p>
 */

/** Фраза в подсказку строки «Вид для чтения». Пусто — профиль к виду не относится или его имени нет. */
export function layoutHint(image: InvoiceImage): string {
  if (image.layout === 'file') return 'Раскладка — из самого файла: профиль печати к нему не применяется.';
  if (image.layout === 'profile' && image.profile) return `Раскладка — профиль печати «${image.profile.name}».`;
  return '';
}

/**
 * Строка под оговоркой — только тому, кто может перестроить: у читателя действия нет, а вид годен.
 * `null` — сказать нечего.
 */
export function layoutNotice(image: InvoiceImage): string | null {
  if (image.layout !== 'profile') return null;
  switch (image.profileState) {
    case 'changed':
      return `Профиль печати «${image.profile?.name ?? ''}» изменён после построения вида — «Перестроить» построит по новому.`;
    case 'deleted':
      return 'Профиль печати, по которому построен вид, удалён — «Перестроить» построит по профилю по умолчанию.';
    case 'none':
      return 'Вид построен до профилей печати — «Перестроить» применит профиль, если у книги нет своей раскладки.';
    default:
      return null;
  }
}

/**
 * Выбирать есть из чего и есть к чему: профилей больше одного, и книга своей раскладки не задала.
 * Вид «до профилей» — тоже: своя ли раскладка у книги, до перестроения не известно, а выбор у книги
 * со своей раскладкой честно кончится «раскладка из файла».
 */
export function canChooseProfile(image: InvoiceImage, profiles: readonly PrintProfile[]): boolean {
  return image.layout === 'profile' && profiles.length > 1;
}

/** Подпись чипа выбора: по чему вид разложен сейчас. */
export function layoutChipLabel(image: InvoiceImage): string {
  if (image.profile) return `Раскладка: ${image.profile.name}`;
  if (image.profileState === 'deleted') return 'Раскладка: профиль удалён';
  if (image.profileState === 'none') return 'Раскладка: до профилей';
  // Подставить профиль не удалось — почему, сказано пометкой построителя.
  return 'Раскладка: профиль не применён';
}

/** Какой профиль отмечен в выборе сначала: тот, по которому построен вид, иначе по умолчанию. */
export function initialChoice(image: InvoiceImage, profiles: readonly PrintProfile[]): string | undefined {
  return profiles.find(p => p.id === image.profile?.id)?.id ?? profiles.find(p => p.isSystem)?.id;
}

/** Пометка построителя о потерянном тексте — её начало, как пишет сервер (`RenditionPostconditions`). */
export const LOST_TEXT_NOTE = 'Часть текста не попала на страницу';
export const AUTHOR_LAYOUT_NOTE = 'Раскладку задал автор файла — профиль печати к нему не применяется.';

/**
 * Пометки построителя. У книги со своей раскладкой к пометке о потерянном тексте дописано, почему
 * профиль не помог: ровно тогда человек и спросит, а ответ не должен требовать администратора.
 */
export function layoutNotes(image: InvoiceImage): string[] {
  if (image.layout !== 'file') return image.notes;
  return image.notes.map(note => note.startsWith(LOST_TEXT_NOTE) ? `${note} ${AUTHOR_LAYOUT_NOTE}` : note);
}

/** Тост после перестроения: по какому профилю — иначе человек не видит, что изменилось. */
export function rebuiltMessage(image: InvoiceImage): string {
  return image.layout === 'profile' && image.profile
    ? `Вид для чтения перестроен по профилю «${image.profile.name}».`
    : 'Вид для чтения перестроен.';
}
