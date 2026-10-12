import {
  purgeBatchChanged, type PurgeBatchExcluded, type PurgeBatchPreview, type PurgeBatchSource, type PurgedBatch,
} from '@/shared/api/recordPurge';
import { apiError } from '@/shared/utils/apiError';
import { lostLinks } from './purgeRecord';

/**
 * Чистая логика диалога пакетной очистки (issue #1245): слова заголовка и итога, группировка не
 * вошедших, разбор отказа. Ввод числа — тот же, что у одиночного удаления (`purgeRecord.ts`).
 */

export const isNamed = (source: PurgeBatchSource): source is { ids: string[] } => 'ids' in source;

export function batchTitle(preview: PurgeBatchPreview): string {
  return preview.records.length > 0 ? `Удалить записей: ${preview.records.length}, потеряв ссылки?` : 'Удалять нечего';
}

/**
 * Что проверено и что из этого выйдет — ДО ввода числа. У выбора галочками это главная защита от
 * «отметил десять, удалено шесть, а думаю, что десять».
 */
export function batchSubtitle(source: PurgeBatchSource, typeName: string | undefined, preview: PurgeBatchPreview): string {
  if (!isNamed(source)) return `Тип «${typeName ?? ''}», проверено записей: ${preview.checked}`;
  const out = preview.excluded.length + preview.missing;
  return out === 0
    ? `Выбрано записей: ${preview.checked}`
    : `Выбрано ${preview.checked}: будут удалены ${preview.records.length}, не войдут ${out}`;
}

/** Почему пакет пуст. Три разных «пусто» обязаны читаться по-разному. */
export function emptyText(source: PurgeBatchSource, typeName: string | undefined, preview: PurgeBatchPreview): string {
  if (!isNamed(source))
    // Найденные, но занятые ещё и ядром, — не «таких записей нет»: они есть и названы ниже.
    return preview.excluded.length > 0
      ? `В типе «${typeName ?? ''}» найдены записи, занятые данными выключенных модулей, но удалить нельзя ни одну — причины ниже.`
      : `В типе «${typeName ?? ''}» на этом уровне нет действующих записей, занятых только данными выключенных модулей.`;
  if (preview.missing >= preview.checked) return `Записей пакета в справочнике уже нет: ${preview.missing}.`;
  return 'Из выбранных не подходит ни одна.';
}

/** Не вошедшие — группами по причине: десять свободных записей — одна причина и десять названий. */
export function excludedGroups(excluded: PurgeBatchExcluded[]): { reason: string; names: string[] }[] {
  const groups = new Map<string, string[]>();
  for (const e of excluded) groups.set(e.reason, [...(groups.get(e.reason) ?? []), e.name]);
  return [...groups].map(([reason, names]) => ({ reason, names }));
}

/** Сколько записей не войдёт: названные с причиной и те, которых уже нет. */
export function leftOut(preview: PurgeBatchPreview): number {
  return preview.excluded.length + preview.missing;
}

/** Последствия — те же, что у одной записи, во множественном числе там, где речь о записях. */
export function batchConsequences(preview: PurgeBatchPreview): string[] {
  return [
    'Записи будут удалены совсем. Вернуть их можно только из резервной копии.',
    ...lostLinks(preview),
    'Действие записывается в журнал по каждой записи: кто, какую запись, сколько ссылок потеряно.',
  ];
}

export function moreNote(preview: PurgeBatchPreview): string | null {
  return preview.more > 0
    ? `За один раз — не больше ${preview.limit}. Занятых выключенными модулями осталось ещё: ${preview.more} — повторите действие после удаления.`
    : null;
}

export function batchToast(done: PurgedBatch, untouched: number): string {
  return `Удалено записей: ${done.records}. Потеряно ссылок: ${done.references}.` +
    (untouched > 0 ? ` Не тронуто: ${untouched}.` : '');
}

/**
 * Свежий список из отказа «пакет изменился» — вместе с тем, что не вошло при первом показе. Сервер
 * в отказе разбирает только записи пакета, и без этого не вошедшие пропали бы и из диалога, и из
 * итога «Не тронуто»: человек отмечал десять, а про четыре ему перестали бы говорить.
 */
export function carriedOver(before: PurgeBatchPreview, now: PurgeBatchPreview): PurgeBatchPreview {
  const known = new Set([...now.records, ...now.excluded].map(r => r.id));
  return {
    ...now,
    excluded: [...now.excluded, ...before.excluded.filter(e => !known.has(e.id))],
    missing: now.missing + before.missing,
    checked: before.checked,
    // Отказ разбирает только записи пакета и об остатке типа не знает — он прежний.
    more: before.more,
  };
}

/** Как изменилось число у записи между показанным и свежим: «было 12». `null` — не изменилось. */
export function wasNote(before: PurgeBatchPreview | null, id: string, now: number): string | null {
  const was = before?.records.find(r => r.id === id)?.references;
  return was !== undefined && was !== now ? `было ${was}` : null;
}

/**
 * Чем кончилась неудачная попытка удалить пакет:
 *  - `changed` — сервер прислал свежий список: показать заново, число ввести снова;
 *  - `final`   — отказ по существу без списка: показать его слова;
 *  - `unknown` — ответа нет (сеть, сбой). ⚠️ Удалён ли пакет, неизвестно: удаление — одна
 *    транзакция, и она могла пройти. Говорить «не удалено» здесь нельзя.
 */
export type BatchFailure =
  | { kind: 'changed'; preview: PurgeBatchPreview; note: string }
  | { kind: 'final'; text: string }
  | { kind: 'unknown' };

export function batchFailure(e: unknown): BatchFailure {
  const fresh = purgeBatchChanged(e);
  if (fresh) return { kind: 'changed', preview: fresh, note: apiError(e, 'Пакет изменился. Ничего не удалено.') };
  const status = (e as { response?: { status?: number } })?.response?.status;
  return status !== undefined && status >= 400 && status < 500
    ? { kind: 'final', text: apiError(e, 'Пакет не удалён.') }
    : { kind: 'unknown' };
}
