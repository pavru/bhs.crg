import { LOST } from './lostReferences';
import { ARCHIVED_HINT, withArchiveWord } from '@/shared/ui/archive';
import * as Dialog from '@radix-ui/react-dialog';
import { useRef, useState } from 'react';
import { Search, TriangleAlert, X } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { apiError } from '@/shared/utils/apiError';
import { useNomenclature } from '@/shared/api/invoices';

/**
 * Выбор позиции номенклатуры для строки счёта (задача C2, issue #1078, ТЗ COST-7).
 *
 * <p><b>Поиск на сервере, а не отбор из загруженного списка</b> — в отличие от пикера типов. Причина не
 * в удобстве: номенклатура — самый большой справочник системы, и в данных записи лежат картинки (в
 * одной установке общие данные весили 5,43 МБ, 99,6 % — base64). Список целиком означал бы мегабайты
 * на каждое открытие формы.</p>
 *
 * <p>⚠️ <b>Неполнота ответа сказана словами.</b> Сервер отдаёт 25 позиций и признак «есть ещё»; если
 * промолчать, отсечение читается как «такой позиции нет» — и человек заведёт вторую такую же, а сводить
 * затраты после этого придётся вручную.</p>
 *
 * <p>⚠️ Создания позиции здесь НЕТ, хотя оно просится. Новая позиция номенклатуры заводится только явным
 * действием и с показом похожих (ТЗ COST-7.1, задача C3) — она требует права
 * <c>core.nomenclature.edit</c>, которого у снабженца может не быть. Кнопка «завести», отказывающая
 * правами, обещала бы то, чего нет.</p>
 */
export function NomenclaturePicker({ chosen, name, lost, lostText, archived, onPick, onClear }: {
  /**
   * Ссылка на позицию ЕСТЬ. Отдельно от названия: пустое название бывает и у выбранной позиции —
   * записи справочника без имени законны, и пикер их показывает.
   */
  chosen: boolean;
  /** Название выбранной позиции; `null` — имени у записи нет либо позиция не выбрана. */
  name: string | null;
  /** Ссылка есть, а записи справочника НЕТ — позицию удалили. Считает сервер, см. `InvoiceLineView`. */
  lost?: boolean;
  /** Чем назвать потерю, если это не удалённая запись: «позиция другого вида». */
  lostText?: string;
  /** Выбранная позиция в архиве: названа как была, а в поиске её уже нет (issue #1185). */
  archived?: boolean;
  onPick: (id: string, name: string | null) => void;
  onClear: () => void;
}) {
  const [open, setOpen] = useState(false);

  return (
    <>
      <div className="flex items-center gap-1">
        <button type="button" onClick={() => setOpen(true)} title={archived && !lost ? ARCHIVED_HINT : undefined}
          className={`min-w-0 flex-1 text-left text-xs px-2 py-1 rounded border truncate
            ${lost ? 'border-danger-border text-danger'
              : chosen ? 'border-stroke text-fg' : 'border-warning-border text-warning'}`}>
          {/* Три состояния, и путать их нельзя: потерю чинит справочник, пустое имя — тоже справочник,
              но позиция на месте, а «выбрать позицию» — работа человека за формой. */}
          {lost ? lostText ?? LOST.position
            : chosen ? withArchiveWord(name ?? 'позиция без названия', !!archived) : 'выбрать позицию'}
        </button>
        {/* ⚠️ Снять ссылку можно ВСЕГДА, пока она есть, — и особенно когда позиция потеряна: сервер
            отказывает сохранять строку с битой ссылкой, а спрятанная кнопка не оставляла человеку
            вообще никакого выхода (ревью PR #1117). */}
        {chosen && (
          <button type="button" onClick={onClear} title="Снять позицию"
            className="shrink-0 text-fg4 hover:text-fg p-0.5">
            <X size={12} />
          </button>
        )}
      </div>

      {open && (
        <PickerDialog
          onClose={() => setOpen(false)}
          onPick={(id, picked) => { onPick(id, picked); setOpen(false); }} />
      )}
    </>
  );
}

/** Тело монтируется по открытию: запрос заводится заново, и эффекта «закрылось — очисти» не нужно. */
function PickerDialog({ onClose, onPick }: {
  onClose: () => void;
  onPick: (id: string, name: string | null) => void;
}) {
  const [typed, setTyped] = useState('');
  const [query, setQuery] = useState('');
  const timer = useRef<number | null>(null);
  const found = useNomenclature(query);

  // Задержка в обработчике, а не в эффекте: запрос на каждую букву — это двадцать обращений на одно
  // слово, а состояние, поставленное из эффекта, вызывает лишний кадр (правило react-hooks).
  function type(next: string) {
    setTyped(next);
    if (timer.current !== null) window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => setQuery(next.trim()), 250);
  }

  const items = found.data?.items ?? [];

  return (
    <Dialog.Root open onOpenChange={o => { if (!o) onClose(); }}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 bg-black/40 z-50" />
        <Dialog.Content className="fixed z-50 left-1/2 top-24 -translate-x-1/2 w-[min(40rem,92vw)]
          rounded-xl border border-stroke bg-surface shadow-xl p-4 space-y-3">
          <Dialog.Title className="text-sm font-medium text-fg2">Позиция номенклатуры</Dialog.Title>

          <div className="flex items-center gap-2 border-b border-stroke pb-2">
            <Search size={14} className="text-fg4 shrink-0" />
            {/* Курсор ставим ссылкой на узел, а не атрибутом `autoFocus`: пикер открывают, чтобы
                искать, но атрибут ругает линтер (он бьёт по фокусу на загрузке страницы), а эффект
                рисовал бы лишний кадр. */}
            <input ref={node => node?.focus()} value={typed} onChange={e => type(e.target.value)}
              placeholder="часть наименования"
              className="flex-1 bg-transparent text-sm outline-none text-fg placeholder:text-fg4" />
          </div>

          {found.isError && (
            <p className="flex items-start gap-2 text-xs text-danger">
              <TriangleAlert size={13} className="shrink-0 mt-0.5" />
              <span>Справочник не прочитан: {apiError(found.error, 'сервер отказал')}. ⚠️ Это НЕ
                «позиций нет»: их список сюда не доехал.</span>
            </p>
          )}

          <div className="max-h-72 overflow-y-auto -mx-1">
            {items.map(item => (
              <button key={item.id} type="button" onClick={() => onPick(item.id, item.name)}
                className="w-full text-left px-3 py-1.5 rounded hover:bg-surface2 flex items-baseline gap-2">
                <span className="text-sm text-fg truncate">{item.name ?? 'без названия'}</span>
                {/* Найдено по альтернативному имени — сказано словами: набранного в названии нет, и
                    без пояснения строка читается как промах поиска. */}
                {item.matchedAlias && (
                  <span className="text-[11px] text-fg3 truncate">найдено по «{item.matchedAlias}»</span>
                )}
                <span className="text-[11px] text-fg4 shrink-0">{item.type}</span>
              </button>
            ))}

            {!found.isError && items.length === 0 && !found.isFetching && (
              <p className="px-3 py-2 text-xs text-fg4">
                {query
                  ? 'По этому запросу позиций нет. Заводит их ответственный за справочник — строку можно '
                    + 'оставить без позиции, счёт будет ждать в отборе «Разобрать».'
                  : 'Справочник номенклатуры пуст.'}
              </p>
            )}
          </div>

          {/* Архивные позиции в выбор не попадают — и молчать о них нельзя: не найдя позицию,
              человек заведёт её заново, а она есть. Действия здесь нет нарочно: вернуть из архива
              отсюда пока нечем. */}
          {!!found.data?.inArchive && (
            <p className="text-xs text-fg3">
              В архиве под этот запрос: {found.data.inArchive}. В выборе архивных позиций нет — заводить
              такую же заново не нужно.
            </p>
          )}

          {/* ⚠️ Оговорка о неполноте — обязательна: см. описание пикера. */}
          {found.data?.more && (
            <p className="text-xs text-warning">
              Показаны первые {items.length} — подходящих больше. Уточните запрос: по неполному списку
              нельзя решить, что позиции в справочнике нет.
            </p>
          )}

          <div className="flex justify-end">
            <Button size="sm" variant="text" onClick={onClose}>Закрыть</Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
