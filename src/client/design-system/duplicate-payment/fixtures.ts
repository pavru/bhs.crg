export const supplier = 'ООО «Вектор Кабель»';
export const number = 'ЭМ-417';
export const date = '19.11.2026';

export const invoices = [
  { id: 'А', amount: '48 600,00 ₽', site: 'Корпус 1', file: 'ЭМ-417_корпус-1.pdf' },
  { id: 'Б', amount: '19 200,00 ₽', site: 'Корпус 2', file: 'ЭМ-417_корпус-2.pdf' },
  { id: 'В', amount: '48 600,00 ₽', site: 'Корпус 1', file: 'ЭМ-417_корпус-1.pdf' },
] as const;

export type Invoice = (typeof invoices)[number];
export type InvoiceId = Invoice['id'];

export const scenarios = [
  { id: 'one', label: 'Один двойник', multiple: false },
  { id: 'several', label: 'Несколько двойников', multiple: true },
  { id: 'paid', label: 'Двойник уже оплачен', multiple: false },
  { id: 'readonly', label: 'Просмотр без права оплаты', multiple: true },
  { id: 'confirmed', label: 'Пара А–Б подтверждена', multiple: true },
] as const;

export type Scenario = (typeof scenarios)[number];

export function pairKey(first: InvoiceId, second: InvoiceId) {
  return [first, second].sort().join('–');
}
