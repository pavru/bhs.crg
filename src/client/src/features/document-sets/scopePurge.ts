import type { PurgeLevel, ScopeLeaving, ScopePurgeOffer } from '@/shared/api/scopePurge';
import { ruCount } from '@/shared/utils/pluralize';
import { lostLinks, type PurgeWords } from './catalog/purgeRecord';

/**
 * Слова принудительного удаления уровня (issue #1243). Чистая логика — без React: тексты здесь
 * различаются родом («удалена», «удалён») и тем, куда идти проверять.
 */

interface LevelWords {
  /** Винительный: «удалить стройку». */
  whom: string;
  /** «Стройка «X» удалена» */
  gone: (name: string) => string;
  /** «Вместе со стройкой будут удалены:» */
  together: string;
  intro: string;
  whole: string;
  journal: string;
  outcome: PurgeWords;
}

const WORDS: Record<PurgeLevel, LevelWords> = {
  construction: {
    whom: 'стройку',
    gone: name => `Стройка «${name}» удалена`,
    together: 'Вместе со стройкой будут удалены:',
    intro: 'На стройку и её содержимое ссылаются данные выключенных модулей:',
    whole: 'Стройка со всем содержимым будет удалена совсем. Вернуть её можно только из резервной копии.',
    journal: 'Действие записывается в журнал: кто, какую стройку, сколько ссылок потеряно.',
    outcome: {
      refused: 'Удалить стройку нельзя.',
      gone: 'Стройки уже нет: её удалили, пока диалог был открыт.',
      unknown: 'Удалена ли стройка — неизвестно: закройте диалог и проверьте список строек. Если она на месте, удаление можно повторить.',
    },
  },
  section: {
    whom: 'раздел',
    gone: name => `Раздел «${name}» удалён`,
    together: 'Вместе с разделом будут удалены:',
    intro: 'На раздел и его содержимое ссылаются данные выключенных модулей:',
    whole: 'Раздел со всем содержимым будет удалён совсем. Вернуть его можно только из резервной копии.',
    journal: 'Действие записывается в журнал: кто, какой раздел, сколько ссылок потеряно.',
    outcome: {
      refused: 'Удалить раздел нельзя.',
      gone: 'Раздела уже нет: его удалили, пока диалог был открыт.',
      unknown: 'Удалён ли раздел — неизвестно: закройте диалог и проверьте стройку. Если он на месте, удаление можно повторить.',
    },
  },
  set: {
    whom: 'комплект',
    gone: name => `Комплект «${name}» удалён`,
    together: 'Вместе с комплектом будут удалены:',
    intro: 'На комплект и его содержимое ссылаются данные выключенных модулей:',
    whole: 'Комплект со всем содержимым будет удалён совсем. Вернуть его можно только из резервной копии.',
    journal: 'Действие записывается в журнал: кто, какой комплект, сколько ссылок потеряно.',
    outcome: {
      refused: 'Удалить комплект нельзя.',
      gone: 'Комплекта уже нет: его удалили, пока диалог был открыт.',
      unknown: 'Удалён ли комплект — неизвестно: закройте диалог и проверьте раздел. Если он на месте, удаление можно повторить.',
    },
  },
};

export const levelWords = (level: PurgeLevel): LevelWords => WORDS[level];

/** Строка-выход в отказе обычного удаления. Без права — без кнопки: выход есть, но не у вас. */
export function exitNote(level: PurgeLevel, allowed: boolean): string {
  const { whom } = WORDS[level];
  return allowed
    ? `${whom[0].toUpperCase()}${whom.slice(1)} можно удалить совсем — ссылки будут потеряны.`
    : `Удалить ${whom}, потеряв ссылки, может администратор.`;
}

/**
 * Что уходит вместе с уровнем — строками. Нулевые не выводятся; пусто — блока нет вовсе: уровень
 * без содержимого держат ссылки на него самого.
 */
export function leavingLines(leaving: ScopeLeaving | null): string[] {
  if (!leaving) return [];
  return [
    leaving.sections > 0 ? ruCount(leaving.sections, 'раздел', 'раздела', 'разделов') : null,
    leaving.sets > 0 ? ruCount(leaving.sets, 'комплект', 'комплекта', 'комплектов') : null,
    leaving.objects > 0
      ? `документы (с их PDF) и записи общих данных — ${leaving.objects}` : null,
    leaving.qualityDocuments > 0
      ? ruCount(leaving.qualityDocuments, 'документ качества', 'документа качества', 'документов качества') : null,
    leaving.workPlanItems > 0
      ? ruCount(leaving.workPlanItems, 'позиция перечня работ', 'позиции перечня работ', 'позиций перечня работ') : null,
  ].filter((line): line is string => line !== null);
}

export function scopeConsequences(level: PurgeLevel, offer: ScopePurgeOffer): string[] {
  return [WORDS[level].whole, ...lostLinks(offer), WORDS[level].journal];
}

export function scopePurgedToast(level: PurgeLevel, name: string, references: number): string {
  return `${WORDS[level].gone(name)}. Потеряно ссылок: ${references}.`;
}
