import type { ReactNode } from 'react';
import type { UseQueryResult } from '@tanstack/react-query';
import { Button } from '@/shared/ui/Button';
import { withArchiveWord } from '@/shared/ui/archive';
import { apiError } from '@/shared/utils/apiError';
import type { NomenclatureCandidates } from '@/shared/api/nomenclatureCandidates';
import { LOST } from './lostReferences';
import { Refusal } from './NomenclatureCardPanel';
import type { PickerRow } from './pickerCandidates';

/** Строка списка окна выбора: кандидат, находка поиска или запись справочника. */
export function PickerOption({ row, id, active, enter, narrow, kinds, onChoose, onHover }: {
  row: PickerRow;
  id: string;
  active: boolean;
  /** Enter сейчас выберет эту строку — сказано на ней. */
  enter: boolean;
  narrow: boolean;
  /** Показывать вид: в выдаче их больше одного. */
  kinds: boolean;
  onChoose: (row: PickerRow) => void;
  onHover: (id: string) => void;
}) {
  const tone = row.best
    ? `bg-primary-container ${active ? 'ring-1 ring-primary/40' : ''}`
    : active ? 'bg-surface-container' : 'hover:bg-surface-container';
  return (
    <div id={id} role="option" aria-selected={active}
      onClick={() => onChoose(row)} onMouseMove={() => { if (!active) onHover(row.id); }}
      className={`cursor-pointer px-2 py-1.5 rounded-md flex items-baseline gap-2 ${tone}`}>
      <span className="min-w-0 flex-1">
        {row.best && <span className="block text-caption font-medium text-primary">Лучшее совпадение</span>}
        <span className="block text-sm text-on-surface">{row.name ?? 'без названия'}</span>
        {row.why && <span className="block text-caption text-on-surface-variant">{row.why}</span>}
      </span>
      {kinds && row.type && <span className="text-caption text-on-surface-variant shrink-0">{row.type}</span>}
      {/* На узком окне без карточки выбирается только лучшее: остальные открывают карточку (issue #1518). */}
      <span className="text-xs text-primary shrink-0 whitespace-nowrap" aria-hidden>
        {narrow && !row.best ? '›' : 'Выбрать'}
        {enter && (
          <kbd className="ml-1 rounded border border-outline-variant bg-surface px-1 font-mono text-caption text-on-surface-variant">Enter</kbd>
        )}
      </span>
    </div>
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
 * Блок «Подходящие для этой строки» над списком при пустом запросе (issue #1518). Каждое состояние —
 * своими словами: «подбираем», «не нашлось», «не сработал» — разные вещи, и пустой блок на месте любого
 * из них читался бы как «подходящих нет», а за ним — вторая такая же позиция в справочнике.
 */
/**
 * Что блок читает у ответа подбора. Уже, чем весь результат запроса, чтобы вызывающий мог подменить данные, не
 * копируя результат целиком: копия читает все его поля и выключает отслеживание прочитанного у React Query.
 */
export type CandidatesAnswer = Pick<UseQueryResult<NomenclatureCandidates>, 'data' | 'isPending' | 'isError' | 'isSuccess' | 'error' | 'refetch'>;

export function CandidatesBlock({ asked, answer, rows, more, current, headingId, children }: {
  /** Спросить было о чём: у строки есть наименование или артикул. */
  asked: boolean;
  answer: CandidatesAnswer;
  /** Строки кандидатов уже стоят в списке окна — сюда приходит только их число. */
  rows: number;
  more: boolean;
  current: PickerCurrent | null;
  /** Заголовок блока подписывает список кандидатов. */
  headingId: string;
  /** Список кандидатов — строки окна, им ведёт одно выделение. */
  children: ReactNode;
}) {
  const data = answer.data;
  // Запомненная позиция и есть выбранная (окно соответствий, строка с архивной позицией): о ней уже
  // сказано «Сейчас: … (в архиве)», второй абзац повторил бы то же другими словами.
  const unusable = data?.recalledUnusable?.id === current?.id ? null : data?.recalledUnusable;
  return (
    <div className="space-y-1.5">
      {current && (
        <p className="text-xs text-on-surface-variant">
          Сейчас: <span className="text-on-surface">
            {current.lost ? current.lostText ?? LOST.position
              : withArchiveWord(current.name ?? 'позиция без названия', !!current.archived)}
          </span>
        </p>
      )}
      {unusable && (
        <p className="text-xs text-on-surface-variant">
          {unusable.issue === 'archived'
            ? `Для этой строки поставщика запомнена позиция «${unusable.name ?? 'без названия'}» — она в архиве. `
              + 'Вернуть её может тот, кто ведёт справочник.'
            : 'Для этой строки поставщика запомнена позиция, которой в справочнике больше нет. Выберите другую.'}
        </p>
      )}

      {!asked ? (
        <p className="text-xs text-on-surface-variant">В строке нет наименования — подобрать нечего. Найдите позицию поиском.</p>
      ) : (
        <>
          <h4 id={headingId} className="text-xs font-medium text-on-surface-variant">Подходящие для этой строки</h4>
          {answer.isPending && (
            <div aria-busy="true" className="space-y-1">
              <div className="h-9 rounded-md bg-surface-container animate-pulse" />
              <div className="h-9 rounded-md bg-surface-container animate-pulse" />
              <p className="text-xs text-on-surface-variant">Подбираем по словам строки…</p>
            </div>
          )}
          {answer.isError && (
            <div>
              <Refusal>
                Подбор не сработал: {apiError(answer.error, 'сервер отказал')}. Это не «подходящих нет» — найдите
                позицию поиском.
              </Refusal>
              <Button size="sm" variant="text" onClick={() => void answer.refetch()}>Повторить</Button>
            </div>
          )}
          {children}
          {answer.isSuccess && rows === 0 && (
            <>
              <p className="text-xs text-on-surface-variant">По словам строки подходящих не нашлось.</p>
              <p className="text-xs text-on-surface-variant">
                Это не значит, что позиции в справочнике нет: подбор сравнивает слова и артикул, а написанное
                иначе («3х2,5» и «3*2.5») не сводит. Найдите поиском — или заведите новую позицию.
              </p>
            </>
          )}
          {more && <p className="text-xs text-on-surface-variant">Есть ещё подходящие — уточните поиском.</p>}
          {!!data?.inArchive && (
            <p className="text-xs text-on-surface-variant">Похожих в архиве: {data.inArchive} — заводить заново не нужно.</p>
          )}
          {!!data?.unreadable && (
            <p className="text-xs text-warning">
              Не сверено позиций: {data.unreadable} — их записи прочитать не удалось. Это не «не подходят».
            </p>
          )}
        </>
      )}
    </div>
  );
}
