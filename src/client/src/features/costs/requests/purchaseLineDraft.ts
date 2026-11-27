import type { PurchaseLine, PurchaseProposalLine } from '@/shared/api/purchaseRequests';

export interface LineDraft {
  row: PurchaseLine | PurchaseProposalLine;
  key: number;
  amount: string;
  unit: string;
  note: string;
}

export function lineDrafts(rows: (PurchaseLine | PurchaseProposalLine)[]): LineDraft[] {
  return rows.map((row, key) => ({
    row, key, amount: String(row.quantity), unit: row.unit ?? '', note: 'note' in row ? row.note ?? '' : '',
  }));
}

export function materialName(names: Record<string, string | null> | null | undefined, id: string) {
  return names?.[id] ?? `Позиция номенклатуры ${id}`;
}

export function confirmedLines(draft: LineDraft[]): PurchaseLine[] | null {
  if (!draft.length) return null;
  const result: PurchaseLine[] = [];
  for (const line of draft) {
    const raw = line.amount.trim().replace(',', '.');
    const quantity = Number(raw);
    const unit = line.unit.trim();
    if (!/^(?:\d{1,14}(\.\d{1,6})?|\.\d{1,6})$/.test(raw) || quantity >= 1e14
      || !unit || unit.length > 50 || line.note.trim().length > 2000) return null;
    result.push({ nomenclatureId: line.row.nomenclatureId, planItemId: line.row.planItemId,
      quantity, unit, note: line.note.trim() || null });
  }
  return result;
}
