import type { CandidatesAnswer, PickerCurrent } from './PickerRows';

/**
 * Что из блока подбора показывать — одним решением на все три части: условия обязаны совпадать (окно ведёт
 * выделение по строкам, которые колонка показывает). Отказ подбора строк не прячет: в списке могут стоять
 * кандидаты соседних строк или прежнего ответа при отказавшем перезапросе, и выделение с Enter ведут к ним.
 */
export function candidatesView({ asked, answer, rows, more, current }: {
  asked: boolean;
  answer: CandidatesAnswer;
  rows: number;
  more: boolean;
  current: PickerCurrent | null;
}) {
  const data = answer.data;
  // Запомненная позиция и есть выбранная (окно соответствий, строка с архивной позицией): о ней уже
  // сказано «Сейчас: … (в архиве)», второй абзац повторил бы то же другими словами.
  const unusable = data?.recalledUnusable?.id === current?.id ? null : data?.recalledUnusable ?? null;
  const failed = asked && answer.isError;
  const notFound = asked && answer.isSuccess && rows === 0;
  // Оговорки — по данным, а не по успеху: отказавший перезапрос прежних чисел не отменяет.
  const inArchive = asked ? data?.inArchive ?? 0 : 0;
  const unreadable = asked ? data?.unreadable ?? 0 : 0;
  return {
    current, unusable, failed,
    noQuestion: !asked,
    // Заголовок без строк под ним при отказе читался бы «подходящих нет»: отказ сказан в полосе.
    heading: asked && (!answer.isError || rows > 0),
    pending: asked && answer.isPending,
    notFound,
    more: asked && more,
    inArchive,
    unreadable,
  };
}
export type CandidatesView = ReturnType<typeof candidatesView>;
