/**
 * Перечень по-русски: «a», «a и b», «a, b и c». Одно правило на все перечни интерфейса — виды файлов, номера
 * счетов (ревью PR #1823: склейка была написана дважды, и правка одной не дошла бы до другой).
 *
 * @param last союз перед последним: «и» или «или».
 */
export function joinWords(items: readonly string[], last = 'и'): string {
  if (items.length < 2) return items.join('');
  return `${items.slice(0, -1).join(', ')} ${last} ${items[items.length - 1]}`;
}
