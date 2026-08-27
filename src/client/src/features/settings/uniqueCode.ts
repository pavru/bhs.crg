// Отдельным файлом от компонента (issue #858): модуль, экспортирующий и компонент, и
// функцию, не может быть границей горячей подмены — правка поднимается вверх по импортам.

/** Уникальный код на базе исходного: base2, base3 … (для дублирования типа, issue #210 Этап 2). */
export function uniqueCode(base: string, existing: Set<string>): string {
  if (!existing.has(base)) return base;
  let i = 2; while (existing.has(`${base}${i}`)) i++;
  return `${base}${i}`;
}
