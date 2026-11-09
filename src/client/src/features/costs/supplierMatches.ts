import type { InvoiceLineMatch, InvoiceLineView, InvoiceMatchMemory } from '@/shared/api/invoices';
import type { MatchSuggestion } from '@/shared/api/supplierMatches';
import type { LineDraft } from './invoiceLines';
import { K, refEntryId } from './invoiceFields';

/**
 * Правила соответствий наименований поставщика, отделённые от разметки (задача C3, issue #1079,
 * ТЗ COST-7.1): что подставить, что запомнится сохранением и какими словами об этом сказать.
 */

/**
 * Запомненное для строки — то, что форма знает о ней от сервера.
 *
 * <p>`supplierId` — чей это опыт: поставщик, по которому спросили (#1657). Предложение переживает смену
 * поставщика в черновике строки, и без него опыт прежнего читался бы опытом нового. Нет — восстановлено из
 * лежащей пометки строки: её сервер не сверяет.</p>
 */
export type MatchOffer = Omit<MatchSuggestion, 'index'> & { supplierId?: string | null };

/** Опыт ли это поставщика `supplier`: неизвестное (из лежащей пометки) — да. */
export function offerOfSupplier(offer: MatchOffer, supplier: string | null): boolean {
  return offer.supplierId === undefined || offer.supplierId === supplier;
}

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
  const byCode = matchKeyText(code);
  if (byCode) return { by: 'code', value: byCode };
  const byName = matchKeyText(text);
  return byName ? { by: 'name', value: byName } : null;
}

/** Тот же ли у двух строк ключ соответствия. */
export function sameKey(a: ReturnType<typeof lineKey>, b: ReturnType<typeof lineKey>): boolean {
  return a?.by === b?.by && a?.value === b?.value;
}

/**
 * Пробел — как `char.IsWhiteSpace` в .NET. У `\s` и `trim()` набор свой: BOM для них пробел, NEL — нет.
 */
const SPACE = /[\t-\r \u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]/;

/**
 * Приведение к ключу — как `SupplierMatchKey.Normalize` сервера: регистр, «ё» и пробелы, и только.
 * Совпадение правил сверяет общий набор `supplierKeyCases.json` — его считают тест сервера и этот.
 *
 * <p>Посимвольно, как сервер: `toLowerCase()` строки целиком знает конечную сигму и делает из «İ» две
 * буквы, а `char.ToLowerInvariant` — нет, и «İ» оставляет как есть.</p>
 */
export function matchKeyText(text: string | null | undefined): string {
  let result = '';
  let gap = false;
  for (const symbol of text ?? '') {
    if (SPACE.test(symbol)) {
      gap = true;
      continue;
    }
    if (gap && result !== '') result += ' ';
    gap = false;
    const lower = symbol === 'İ' ? symbol : symbol.toLowerCase();
    result += lower === 'ё' ? 'е' : lower;
  }
  return result;
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
 * Новая пометка «запомнено» взята из памяти НЕ этого поставщика (#1657): сервер её отвергнет («это
 * соответствие другого поставщика»). Пометки, что у строки уже лежали, сервер не сверяет — они не чужие.
 *
 * <p>Одно правило на снятие (`dropForeignMarks`) и на предупреждение у пометки: разойдись они — пометка
 * обещала бы снятие, которого не будет, или молчала бы о том, что снимется.</p>
 *
 * @param supplier поставщик, за которым строки будут записаны.
 */
export function foreignMark(draft: LineDraft, saved: InvoiceLineView | undefined, supplier: string | null): boolean {
  if (draft.matchedBy === null || (saved?.match?.id ?? null) === draft.matchedBy) return false;
  return !!draft.offer && !offerOfSupplier(draft.offer, supplier);
}

/**
 * Поставщика сменили (D-10.7, ревью PR #1786, #1657): НОВЫЕ пометки «запомнено» из памяти прежнего сервер
 * отвергнет. Позиция у таких строк остаётся выбранной, пометка снимается, и за новым поставщиком выбор не
 * запоминается: его делала память прежнего, а не человек.
 *
 * <p>Судит поставщик, за которым строки пишутся, а не то, сменила ли его шапка этим же нажатием: шапка могла
 * записаться прошлым нажатием, а строки — нет (ревью PR #1830).</p>
 */
export function dropForeignMarks(drafts: readonly LineDraft[], saved: readonly InvoiceLineView[], supplier: string | null): {
  drafts: LineDraft[]; dropped: number;
} {
  let dropped = 0;
  const next = drafts.map(draft => {
    if (!foreignMark(draft, draft.id ? saved.find(line => line.id === draft.id) : undefined, supplier)) return draft;
    dropped += 1;
    return { ...draft, matchedBy: null, match: null, offer: null, remember: false };
  });
  return { drafts: next, dropped };
}

/** Поставщик в шапке изменён и не сохранён (#1657, D3): на кого. `null` — смены нет. */
export interface SupplierChange {
  /** Поставщик из правки шапки; `null` — поставщика сняли. */
  to: string | null;
}

/**
 * Смена поставщика, которой нет в сохранённом счёте (#1657, D3), — сравнением правки шапки с сохранённым
 * видом по ссылке. Выбрали прежнего обратно — смены нет, хотя ключ в правке остался.
 */
export function supplierChangeOf(edits: Readonly<Record<string, unknown>>, saved: Readonly<Record<string, unknown>>): SupplierChange | null {
  if (!(K.supplier in edits)) return null;
  const to = refEntryId(edits[K.supplier]);
  return to === refEntryId(saved[K.supplier]) ? null : { to };
}

/**
 * Строки пишутся БЕЗ шапки, а смена поставщика в ней не сохранена (#1657, решение (б) от 08.11.2026): каждая
 * строка уходит с «не запоминать». Сервер запомнил бы выбор за поставщиком, который лежит в счёте, — за
 * прежним, хотя человек выбирал позиции уже для нового.
 *
 * <p>Сегодня строки так не пишутся: «Сохранить» записывает шапку раньше строк (D-10.7), и выбор уходит за
 * новым поставщиком. Правило — для записи строк без шапки, которую принесёт автосохранение (12).</p>
 *
 * @param saved строки, лежащие в счёте: запоминается только новость, и молчать о незапомненном можно, когда
 *   запоминать было нечего.
 * @returns строки к отправке и было ли что запоминать: о незапомненном выборе молчать нельзя.
 */
export function holdMemory(drafts: readonly LineDraft[], saved: readonly InvoiceLineView[]): { drafts: LineDraft[]; held: boolean } {
  const held = drafts.some(draft => memoryFate(draft, saved.find(line => line.id === draft.id), true, null) === 'remember');
  return { drafts: drafts.map(draft => ({ ...draft, remember: false })), held };
}

/** Уведомление о выборе, не запомненном по {@link holdMemory}. */
export const HELD_MEMORY_NOTICE = 'Строки сохранены, но выбор позиций не запомнен: поставщик в шапке изменён и не '
  + 'сохранён. Позиции в строках на месте.';

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
 * Уведомление после сохранения строк — одно на сохранение ([UI-71]); `null` — ничего не запомнилось, и
 * говорить не о чем. Отказ запоминания назван, хотя строки сохранены: ноль запомненного без него читался
 * бы как «запоминать было нечего». Отказ значит, что не записалось ничего, — поэтому он и вытесняет
 * число запомненного, а не встаёт вторым уведомлением за ним.
 */
export function memoryNotice(memory: InvoiceMatchMemory | null | undefined): { variant: 'success' | 'info'; text: string } | null {
  if (memory?.failed) {
    return {
      variant: 'info',
      text: 'Строки сохранены, но выбор позиций не запомнился — сбой на сервере. Позиции в строках на месте; '
        + 'следующий счёт этого поставщика их сам не получит.',
    };
  }
  if (!memory || memory.remembered === 0) return null;
  return {
    variant: 'success',
    text: memory.replaced > 0
      ? `Строки сохранены. Запомнено соответствий: ${memory.remembered}, из них заменено: ${memory.replaced}.`
      : `Строки сохранены. Запомнено соответствий: ${memory.remembered}.`,
  };
}
