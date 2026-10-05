/** Рубли так, как их пишут на всех экранах: «412 500,00 ₽». */
export function formatMoney(value: number): string {
  return `${value.toLocaleString('ru-RU', { minimumFractionDigits: 2, maximumFractionDigits: 2 })} ₽`;
}
