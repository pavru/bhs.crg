import { createContext, useContext, useState } from 'react';
import type { MatrixState } from '@/shared/api/allocationMatrix';
import type { VatMode } from '@/shared/api/invoices';
import type { LineDraft } from './invoiceLines';

/**
 * Режим «Исправление» формы счёта (E2, issue #1100, PR 6b-1) и его ЕДИНЫЙ черновик.
 *
 * <p>Обычная форма пишет четырьмя путями: шапка — своей кнопкой, строки — своей, разноска строки и
 * матрица — сразу. У исправления запись одна — версия и книга, — и посчитать её можно только по счёту
 * целиком. Поэтому в режиме исправления каждая часть формы кладёт набранное сюда, а записывает всё
 * одним телом полоса режима (`CorrectionBar`).</p>
 *
 * <p>Правки шапки живут там же, где всегда, — в форме (`edits`): поля шапки в обоих режимах одни и те
 * же. Здесь — то, что в обычном режиме записывалось само: строки и разноска.</p>
 */
export function useCorrectionDraft() {
  const [active, setActive] = useState(false);
  const [reason, setReason] = useState('');
  /** Черновик строк; `null` — строки не трогали. Кладёт таблица строк. */
  const [lines, setLines] = useState<LineDraft[] | null>(null);
  /** Режим НДС черновика строк (issue #1498); `null` — строки не трогали. Кладёт таблица строк вместе с ними. */
  const [vatMode, setVatMode] = useState<VatMode | null>(null);
  /** Разноска из матрицы; `null` — не трогали. Кладёт матрица в черновом режиме. */
  const [allocation, setAllocation] = useState<MatrixState | null>(null);
  // Поколение черновика строк: таблица смонтирована с ним ключом, и сменить его — значит собрать строки
  // заново из вида. Сброс состояния ребёнка эффектом нарисовал бы кадр с прежними строками.
  const [generation, setGeneration] = useState(0);

  function forget() {
    setLines(null);
    setVatMode(null);
    setAllocation(null);
    setGeneration(g => g + 1);
  }

  return {
    active, reason, setReason, lines, setLines, vatMode, setVatMode, allocation, setAllocation, generation,
    /** Набрано то, что не видно форме: причина и разноска. Шапку и строки форма знает сама. */
    dirty: active && (reason.trim() !== '' || allocation !== null),
    start: () => setActive(true),
    /** Выйти, выбросив набранное: «Отменить исправление» и после записи. */
    stop: () => { forget(); setReason(''); setActive(false); },
    /**
     * Выйти, оставив набранное в шапке и строках: исправление больше не нужно, и это обычные несохранённые
     * правки. Разноска из матрицы черновиком обычной формы не бывает — её набирают заново.
     */
    release: () => { setAllocation(null); setReason(''); setActive(false); },
    /** «Перечитать счёт»: строки и разноска — из вида, причина остаётся — она про намерение, а не про счёт. */
    reread: forget,
  };
}

export type CorrectionDraft = ReturnType<typeof useCorrectionDraft>;

/**
 * Черновик исправления для частей формы — таблицы строк и матрицы. `null` вне формы счёта; `active`
 * ложно — форма в обычном режиме, и части пишут сами, как всегда.
 */
export const CorrectionContext = createContext<CorrectionDraft | null>(null);

/** Черновик исправления, если форма сейчас в этом режиме; иначе `null`. */
export function useCorrecting(): CorrectionDraft | null {
  const draft = useContext(CorrectionContext);
  return draft?.active ? draft : null;
}
