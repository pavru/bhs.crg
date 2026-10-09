import type { InvoiceLineMatch, InvoiceLineView, InvoiceMatchMemory } from '@/shared/api/invoices';
import type { MatchSuggestion } from '@/shared/api/supplierMatches';
import type { LineDraft } from './invoiceLines';

/**
 * Правила соответствий наименований поставщика, отделённые от разметки (задача C3, issue #1079,
 * ТЗ COST-7.1): что подставить, что запомнится сохранением и какими словами об этом сказать.
 */

/** Запомненное для строки — то, что форма знает о ней от сервера. */
export type MatchOffer = Omit<MatchSuggestion, 'index'>;

/** Что станет с выбором позиции при сохранении строк. */
export type MemoryFate =
  /** Запоминать нечего: позицию не меняли, она подставлена либо узнавать строку не по чему. */
  | 'none'
  /** Выбор запомнится новым соответствием. */
  | 'remember'
  /** Выбор заменит запомненное раньше — другой позицией. */
  | 'replace'
  /** Человек сказал «не запоминать». */
  | 'off';

/**
 * Ключ строки — так же, как его считает сервер: артикул, если есть, иначе наименование.
 *
 * ⚠️ Повторено нарочно и только ради ПОДСКАЗКИ «запомнится»: запоминает сервер, и решает тоже он.
 * Разойдись правила — подсказка соврёт, а данные нет; ответ сохранения называет число запомненного.
 */
export function lineKey(code: string | null, text: string | null): { by: 'code' | 'name'; value: string } | null {
  const byCode = normalize(code);
  if (byCode) return { by: 'code', value: byCode };
  const byName = normalize(text);
  return byName ? { by: 'name', value: byName } : null;
}

function normalize(text: string | null): string {
  return (text ?? '').trim().replace(/\s+/g, ' ').toLowerCase().replaceAll('ё', 'е');
}

/** Можно ли подставить запомненное: архивную и удалённую позицию не подставляем. */
export function usable(offer: MatchOffer | null | undefined): offer is MatchOffer {
  return !!offer && offer.issue === null;
}

/** Подстановка: позиция и пометка — вместе, иначе пометка говорила бы не о той позиции. */
export function applyOffer(offer: MatchOffer): Partial<LineDraft> {
  return {
    nomenclatureId: offer.nomenclatureId,
    nomenclatureName: offer.nomenclatureName,
    nomenclatureLost: false,
    nomenclatureMoved: false,
    nomenclatureArchived: false,
    matchedBy: offer.matchId,
    match: {
      id: offer.matchId, by: offer.by, state: 'current', source: offer.source,
      rememberedAt: offer.rememberedAt, rememberedBy: offer.rememberedBy,
    },
    offer,
    declined: false,
  };
}

/**
 * Отмена подстановки: снимаются позиция и пометка — и ТОЛЬКО они. Соответствие остаётся (решение
 * владельца продукта от 09.10.2026): неверное исправит следующий выбор, а случайный щелчок не должен
 * стирать запомненное для всех будущих счетов.
 *
 * <p>Отменённое запоминается в строке, чтобы его можно было вернуть — и чтобы «Подставить запомненное»
 * не вернуло его само.</p>
 */
export function cancelMatch(draft: LineDraft): Partial<LineDraft> {
  return {
    nomenclatureId: null,
    nomenclatureName: null,
    nomenclatureLost: false,
    nomenclatureMoved: false,
    nomenclatureArchived: false,
    matchedBy: null,
    match: null,
    offer: draft.offer ?? offerOf(draft),
    declined: true,
  };
}

/** Запомненное, восстановленное из сохранённой помеченной строки, — чтобы отмену можно было вернуть. */
function offerOf(draft: LineDraft): MatchOffer | null {
  const match = draft.match;
  if (!match || match.state !== 'current' || match.by === null || draft.nomenclatureId === null) return null;
  return {
    matchId: match.id, by: match.by, source: match.source ?? '', nomenclatureId: draft.nomenclatureId,
    nomenclatureName: draft.nomenclatureName, nomenclatureType: null, issue: null,
    rememberedAt: match.rememberedAt ?? '', rememberedBy: match.rememberedBy,
  };
}

/** Выбор руками: пометка снимается — позиция больше не «подставлена». Слово «не запоминать» тоже. */
export function pickByHand(id: string, name: string | null): Partial<LineDraft> {
  return {
    nomenclatureId: id, nomenclatureName: name,
    nomenclatureLost: false, nomenclatureMoved: false, nomenclatureArchived: false,
    matchedBy: null, match: null, remember: true,
  };
}

/**
 * Что станет с выбором при сохранении — тем же правилом, что на сервере: запоминается выбор человека,
 * и только когда он новость (новая строка либо сменились позиция или ключ).
 */
export function memoryFate(
  draft: LineDraft, saved: InvoiceLineView | undefined, hasSupplier: boolean, offer: MatchOffer | null | undefined,
): MemoryFate {
  if (!hasSupplier || draft.nomenclatureId === null || draft.matchedBy !== null) return 'none';

  const key = lineKey(draft.supplierCode, draft.supplierText);
  if (!key) return 'none';

  const before = saved ? lineKey(saved.supplierCode, saved.supplierText) : null;
  const news = !saved || saved.nomenclatureId !== draft.nomenclatureId
    || before?.by !== key.by || before.value !== key.value;
  if (!news) return 'none';

  if (draft.remember === false) return 'off';
  // Заменится только соответствие ТОГО ЖЕ ключа. Строка с артикулом, узнанная по наименованию, запомнится
  // новым соответствием по артикулу, а прежнее — по наименованию — останется: обещать здесь «заменится»
  // значило бы соврать, и человек считал бы исправленным то, что продолжит подставляться (ревью PR #1262).
  if (!offer || offer.by !== key.by) return 'remember';
  // Выбрали то же, что запомнено: сервер ничего не запишет, и обещать «запомнится» нечего.
  return offer.nomenclatureId === draft.nomenclatureId ? 'none' : 'replace';
}

/** Строки, которым запомненное можно подставить прямо сейчас. */
export function pending(drafts: readonly LineDraft[], offers: (draft: LineDraft) => MatchOffer | null | undefined): LineDraft[] {
  return drafts.filter(draft => draft.nomenclatureId === null && !draft.declined && usable(offers(draft)));
}

/** Чем узнана строка — словами, в именительном. */
export function byWord(by: 'code' | 'name' | null): string {
  return by === 'code' ? 'артикул' : 'наименование';
}

/** Что с соответствием стало с тех пор, как позицию подставили; `null` — ничего. */
export function matchTrouble(match: InvoiceLineMatch | null | undefined): string | null {
  switch (match?.state) {
    case 'changed': return 'соответствие с тех пор направили на другую позицию';
    case 'gone': return 'соответствие с тех пор забыли';
    case 'foreign': return 'соответствие другого поставщика — поставщика у счёта сменили';
    default: return null;
  }
}

/**
 * Запомнить не удалось, хотя строки сохранены, — словами; `null` — отказа не было. Молчать нельзя: ноль
 * запомненного читался бы как «запоминать было нечего».
 */
export function memoryFailure(memory: InvoiceMatchMemory | null | undefined): string | null {
  return memory?.failed
    ? 'Строки сохранены, но выбор позиций не запомнился — сбой на сервере. Позиции в строках на месте; '
      + 'следующий счёт этого поставщика их сам не получит.'
    : null;
}

/** Слова тоста после сохранения строк; `null` — ничего не запомнилось, и говорить не о чем. */
export function memoryToast(memory: InvoiceMatchMemory | null | undefined): string | null {
  if (!memory || memory.remembered === 0) return null;
  return memory.replaced > 0
    ? `Строки сохранены. Запомнено соответствий: ${memory.remembered}, из них заменено: ${memory.replaced}.`
    : `Строки сохранены. Запомнено соответствий: ${memory.remembered}.`;
}
