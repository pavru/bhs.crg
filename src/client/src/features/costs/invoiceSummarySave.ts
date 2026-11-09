import type { SaveModel, SaveStatus } from './invoiceSave';

/**
 * Запись в сводке (D-12.3): `read` — разобранный читается, «Изменить» открывает правку; `carrier` — носитель. Что
 * сказать при несохранённом и куда вести, решает `summarySave` (`pending`): черновик пишется сам и его не называет; режим
 * «Изменить» — «Не сохранено» и «Отменить правку» (`onCancel`); оплаченный черновик ведёт к «Сохранить» в «Сроках».
 */
export type SummarySave =
  | { kind: 'read'; onEdit: () => void }
  | { kind: 'carrier'; status: SaveStatus; savedAt: string; onRetry: () => void; pending: SavePending | null;
    onCancel?: () => void };

/** «Не сохранено» явной записи — что сказать и куда вести. */
export type SavePending = { text: string; onGo?: () => void };

/** Запись в сводке по модели записи формы (D-12.3): что сказать при несохранённом — здесь и только здесь. */
export function summarySave(model: SaveModel, at: {
  status: SaveStatus; savedAt: string; unsaved: boolean; onRetry: () => void;
  onEdit: () => void; onCancel: () => void; onGo: () => void;
}): SummarySave | null {
  const carrier = { kind: 'carrier' as const, status: at.status, savedAt: at.savedAt, onRetry: at.onRetry };
  switch (model) {
    case 'auto': return { ...carrier, pending: null };
    case 'read': return { kind: 'read', onEdit: at.onEdit };
    case 'edit': return { ...carrier, pending: at.unsaved ? { text: 'Не сохранено' } : null, onCancel: at.onCancel };
    case 'terms': return { ...carrier,
      pending: at.unsaved ? { text: 'Не сохранено — «Сохранить» в «Сроках и оплате»', onGo: at.onGo } : null };
    default: return null;
  }
}
