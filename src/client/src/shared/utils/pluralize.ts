/** Русское склонение числительных: 1 раздел, 2 раздела, 5 разделов, 21 раздел, 11 разделов. */
export function ruPlural(n: number, one: string, few: string, many: string): string {
  const mod10 = n % 10;
  const mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return one;
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return few;
  return many;
}

/** "N раздел(-а/-ов)" — числительное + существительное в нужном падеже. */
export function ruCount(n: number, one: string, few: string, many: string): string {
  return `${n} ${ruPlural(n, one, few, many)}`;
}
