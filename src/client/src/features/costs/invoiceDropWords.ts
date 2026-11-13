import { recognizedKinds, wordsOf, type FileKind, type FileKindsInfo } from '@/shared/api/fileKinds';

/**
 * Слова о файлах счёта (shell-07-scan-drop-hint, [COST-8] [COST-31], #1435). Целью броска служит всё окно
 * (costs-invoice-screen-14), поэтому отдельного элемента под это нет: о броске до броска говорят пустые состояния —
 * там, куда человек смотрит, пока счёт не открыт. Здесь же подсказка кнопки «Счёт из файла»: перечень форматов
 * называется одним правилом, и подсказка с описанием не разойдутся.
 */

/** Распознаваемые виды словами; `null` — назвать нечего (реестр не пришёл или пуст). */
const kindsWords = (kinds: FileKind[]): string | null => (kinds.length ? wordsOf(kinds) : null);

/**
 * Звать ли к броску. Нужны все три условия:
 * - право заводить счета — без него приглашение кончилось бы отказом;
 * - точный указатель (`!coarse`) — на касании, хоть в 1024 px, перетащить файл в окно нечем;
 * - реестр не сказал «распознаётся ничего»: тогда выбор файла пуст и брошенное отсеется. Реестр ещё не пришёл —
 *   звать можно: бросок примут, а перечень допишется, когда он придёт.
 */
export function dropInvited({ canEdit, coarse, info }: {
  canEdit: boolean; coarse: boolean; info: FileKindsInfo | undefined;
}): boolean {
  return canEdit && !coarse && (!info || recognizedKinds(info).length > 0);
}

/** Подсказка кнопки «Счёт из файла»: какие файлы распознаются, видно до выбора. */
export function fromFileTitle(kinds: FileKind[]): string {
  const words = kindsWords(kinds);
  return `Один или несколько файлов${words ? `: ${words}` : ''}. Файлы можно перетащить в окно.`;
}

/**
 * Описание «Выберите счёт» в правой части. Она сама входит в цель броска — «сюда» здесь правда. Перечня нет, пока
 * реестр не пришёл, — без скобок, а не выдуманный перечень. Не зовём — описания нет: заголовка достаточно, а
 * «заведите новый» звало бы в отказ того, у кого нет права.
 */
export function selectHint(invited: boolean, kinds: FileKind[]): string | undefined {
  if (!invited) return undefined;
  const words = kindsWords(kinds);
  return `Или перетащите сюда файлы счетов${words ? ` (${words})` : ''} — из каждого заведём черновик и распознаем `
    + 'его в фоне. Новый счёт можно завести и пустым.';
}
