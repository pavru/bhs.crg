import type { ClosingLine, ClosingSection } from '@/shared/api/periods';
import { formatMoney } from '@/shared/utils/money';

/**
 * Правила перечня диалога закрытия, отделённые от разметки (E1b, issue #1099): что сказать в футере и
 * в сообщении об успехе и чем перечень отличается от того, который человек видел до отказа «данные
 * изменились».
 */

export type ClosingGroup = 'unfinished' | 'frozen';

/** Строка числом и суммой: «3 счёта на 412 500,00 ₽»; без суммы — «3 счёта». */
export function figure(line: Pick<ClosingLine, 'counted' | 'amount'>): string {
  return line.amount === null ? line.counted : `${line.counted} на ${formatMoney(line.amount)}`;
}

/**
 * Незавершённое одной фразой — для футера и сообщения об успехе: «11 счетов». null — незавершённого
 * нет. Числа разных строк не складываются: у модулей свои документы, и «14 документов» не значило бы
 * ничего.
 */
export function unfinishedSummary(sections: ClosingSection[]): string | null {
  const parts = sections.flatMap(s => s.unfinished.filter(l => l.count > 0).map(l => l.counted));
  return parts.length === 0 ? null : parts.join(', ');
}

const lineOf = (sections: ClosingSection[], module: string, group: ClosingGroup, key: string) =>
  sections.find(s => s.module === module)?.[group].find(l => l.key === key);

/**
 * Чем строка была в перечне, который человек видел до отказа: «было: 2 счёта на 300,00 ₽»; строки не
 * было — «не было». null — строка не изменилась либо сравнивать не с чем.
 */
export function wasText(
  before: ClosingSection[] | null, module: string, group: ClosingGroup, line: ClosingLine,
): string | null {
  if (!before) return null;
  const was = lineOf(before, module, group, line.key);
  if (!was) return 'не было';
  return figure(was) === figure(line) ? null : `было: ${figure(was)}`;
}

/** Строки, которые были в прежнем перечне, а в нынешнем их нет: исчезнувшее тоже изменение. */
export function goneLines(
  before: ClosingSection[] | null, section: ClosingSection, group: ClosingGroup,
): ClosingLine[] {
  const was = before?.find(s => s.module === section.module)?.[group] ?? [];
  return was.filter(l => !section[group].some(now => now.key === l.key));
}

/**
 * Ссылка строки — только путь внутри приложения. Перечень приходит с сервера и лежит в записи о
 * закрытии; адрес наружу под видом «посмотреть счета» экран не откроет, откуда бы он ни взялся.
 */
export function localLink(link: string | null | undefined): string | null {
  return link && link.startsWith('/') && !link.startsWith('//') && !link.includes('\\') ? link : null;
}
