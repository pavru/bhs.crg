import type { ReactNode } from 'react';
import type { UseQueryResult } from '@tanstack/react-query';
import { Button } from '@/shared/ui/Button';
import { ListOption } from '@/shared/ui/ListOption';
import { withArchiveWord } from '@/shared/ui/archive';
import { apiError } from '@/shared/utils/apiError';
import type { NomenclatureCandidates } from '@/shared/api/nomenclatureCandidates';
import { LOST } from './lostReferences';
import { Refusal } from './NomenclatureCardPanel';
import type { PickerRow } from './pickerCandidates';
import type { CandidatesView } from './candidatesView';

/** Строка списка окна выбора: кандидат, находка поиска или запись справочника ([UI-87], `ListOption` формы `list`). */
export function PickerOption({ row, id, active, current = false, enter, narrow, kinds, onChoose, onHover }: {
  row: PickerRow;
  id: string;
  active: boolean;
  /** Позиция уже стоит в строке: галочка и `aria-selected` (П2, Р1). */
  current?: boolean;
  /** Enter сейчас выберет эту строку — сказано на ней. */
  enter: boolean;
  narrow: boolean;
  /** Показывать вид: в выдаче их больше одного. */
  kinds: boolean;
  onChoose: (row: PickerRow) => void;
  onHover: (id: string) => void;
}) {
  // Лучшее совпадение — надписью, без своей заливки: подсвечена только активная строка. «Выбрать» на каждой
  // строке был шумом — Enter сказан только на активной.
  // Пометки собираются списком: пустой — слота нет (фрагмент `present` считал бы содержимым).
  const marks = [
    kinds && row.type && <span key="type">{row.type}</span>,
    // На узком окне без карточки выбирается только лучшее: остальные открывают карточку (issue #1518).
    narrow && !row.best && <span key="card" className="text-body-small text-primary" aria-hidden>›</span>,
    enter && (
      <kbd key="enter" aria-hidden className="rounded-xs border border-outline-variant bg-surface px-1 font-mono text-label-small text-on-surface-variant">Enter</kbd>
    ),
  ].filter(Boolean);
  return (
    <ListOption id={id} active={active} current={current} overline={row.best ? 'Лучшее совпадение' : undefined}
      headline={row.name ?? 'без названия'} supporting={row.why ?? undefined}
      trailing={marks.length ? marks : undefined}
      onChoose={() => onChoose(row)} onHover={() => onHover(row.id)} />
  );
}

/** Позиция, уже стоящая в строке, — первой строкой и без кнопки: выбирать её заново незачем. */
export interface PickerCurrent {
  id?: string | null;
  name: string | null;
  archived?: boolean;
  lost?: boolean;
  lostText?: string;
}

/**
 * Что части блока читают у ответа подбора. Уже, чем весь результат запроса, чтобы вызывающий мог подменить данные,
 * не копируя результат целиком: копия читает все его поля и выключает отслеживание прочитанного у React Query.
 */
export type CandidatesAnswer = Pick<UseQueryResult<NomenclatureCandidates>, 'data' | 'isPending' | 'isError' | 'isSuccess' | 'error' | 'refetch'>;

/*
 * Блок «Подходящие для этой строки» при пустом запросе (issue #1518). Каждое состояние — своими словами:
 * «подбираем», «не нашлось», «не сработал» — разные вещи, и пустой блок на месте любого из них читался бы как
 * «подходящих нет», а за ним — вторая такая же позиция в справочнике.
 *
 * Блок разложен на три части по месту ([COST-7], D6 costs-picker-sheet-reflow): всё, что переносится по ширине,
 * стоит либо в полосе над колонками (CandidatesNotices), либо под списками (CandidatesTail). Между строками
 * кандидатов и справочника — только однострочные заголовки: иначе карточка, открывшись рядом и сузив колонку,
 * переносила бы оговорку и сдвигала строки справочника из-под мыши.
 */


/** Полоса над колонками: что стоит в строке сейчас, запомненная позиция, отказ подбора, «спросить не о чем». */
export function CandidatesNotices({ view, answer }: { view: CandidatesView; answer: CandidatesAnswer }) {
  const { current, unusable, noQuestion, failed } = view;
  if (!current && !unusable && !noQuestion && !failed) return null;
  return (
    <div className="space-y-1.5">
      {current && (
        <p className="text-body-small text-on-surface-variant">
          Сейчас: <span className="text-on-surface">
            {current.lost ? current.lostText ?? LOST.position
              : withArchiveWord(current.name ?? 'позиция без названия', !!current.archived)}
          </span>
        </p>
      )}
      {unusable && (
        <p className="text-body-small text-on-surface-variant">
          {unusable.issue === 'archived'
            ? `Для этой строки поставщика запомнена позиция «${unusable.name ?? 'без названия'}» — она в архиве. `
              + 'Вернуть её может тот, кто ведёт справочник.'
            : 'Для этой строки поставщика запомнена позиция, которой в справочнике больше нет. Выберите другую.'}
        </p>
      )}
      {noQuestion && (
        <p className="text-body-small text-on-surface-variant">В строке нет наименования — подобрать нечего. Найдите позицию поиском.</p>
      )}
      {failed && (
        <div>
          <Refusal>
            Подбор не сработал: {apiError(answer.error, 'сервер отказал')}. Это не «подходящих нет» — найдите
            позицию поиском.
          </Refusal>
          <Button variant="text" onClick={() => void answer.refetch()}>Повторить</Button>
        </div>
      )}
    </div>
  );
}

/**
 * Колонка списка: заголовок, ожидание, строки кандидатов и строка «не нашлось». Строка не обрезается: рядом с
 * листом она занимает около половины колонки, а обрезанное «не нашлось» не было бы сказано своими словами (#1518).
 */
export function CandidatesBlock({ view, headingId, children }: {
  view: CandidatesView;
  /** Заголовок блока подписывает список кандидатов. */
  headingId: string;
  /** Список кандидатов — строки окна, им ведёт одно выделение. */
  children: ReactNode;
}) {
  if (!view.heading) return null;
  return (
    <div className="space-y-1.5">
      <h4 id={headingId} className="text-body-small font-medium text-on-surface-variant">Подходящие для этой строки</h4>
      {view.pending && (
        <div aria-busy="true" className="space-y-1">
          <div className="h-9 rounded-sm bg-surface-container animate-pulse" />
          <div className="h-9 rounded-sm bg-surface-container animate-pulse" />
          <p className="text-body-small text-on-surface-variant">Подбираем по словам строки…</p>
        </div>
      )}
      {children}
      {view.notFound && (
        <p className="text-body-small text-on-surface-variant">По словам строки подходящих не нашлось.</p>
      )}
    </div>
  );
}

/** Под списками: оговорки к подбору — их обрезать нельзя, а между списками они сдвигали бы строки справочника. */
export function CandidatesTail({ view }: { view: CandidatesView }) {
  const { notFound, more, inArchive, unreadable } = view;
  if (!notFound && !more && !inArchive && !unreadable) return null;
  return (
    <div className="space-y-1.5">
      {notFound && (
        <p className="text-body-small text-on-surface-variant">
          Это не значит, что позиции в справочнике нет: подбор сравнивает слова и артикул, а написанное
          иначе («3х2,5» и «3*2.5») не сводит. Найдите поиском — или заведите новую позицию.
        </p>
      )}
      {more && <p className="text-body-small text-on-surface-variant">Есть ещё подходящие — уточните поиском.</p>}
      {!!inArchive && (
        <p className="text-body-small text-on-surface-variant">Похожих в архиве: {inArchive} — заводить заново не нужно.</p>
      )}
      {!!unreadable && (
        <p className="text-body-small text-warning">
          Не сверено позиций: {unreadable} — их записи прочитать не удалось. Это не «не подходят».
        </p>
      )}
    </div>
  );
}

/** Окно без листа (окно соответствий сметы): три части подряд, как один блок. */
export function CandidatesStack({ view, answer, headingId, children }: {
  view: CandidatesView;
  answer: CandidatesAnswer;
  headingId: string;
  children: ReactNode;
}) {
  return (
    <div className="space-y-1.5">
      <CandidatesNotices view={view} answer={answer} />
      <CandidatesBlock view={view} headingId={headingId}>{children}</CandidatesBlock>
      <CandidatesTail view={view} />
    </div>
  );
}
