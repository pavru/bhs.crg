import type { EstimateVsCostsRow } from '@/shared/api/estimateVsCosts';

export function comparisonGroup(row: EstimateVsCostsRow) {
  if (row.difference === undefined || row.difference === null) return 'Сравнение недоступно';
  if (row.difference > 0) return 'Превышение сметы';
  if (row.difference < 0) return 'В пределах сметы';
  return 'Совпадает со сметой';
}

export function estimateReason(row: EstimateVsCostsRow, currencyMismatch = false) {
  if (row.estimate.state === 'hidden') return 'Скрыто правами — нужно право plan.estimate.cost';
  if (row.estimate.state === 'not-connected') return 'Модуль «Планирование» не подключён';
  if (row.estimate.state === 'not-in-estimate') return 'Нет в смете';
  if (currencyMismatch) return 'Разные валюты сметы и факта';
  if (row.estimate.basisUnknown) return 'Основа сметной суммы без НДС не подтверждена';
  if (row.allocatedIncomplete) return 'Стоимость части разноски неизвестна';
  if (row.allocatedVatUnknown) return 'НДС неизвестен — учтено полной суммой';
  return '';
}
