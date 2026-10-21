import { LOST } from './lostReferences';
import { ARCHIVED_HINT, withArchiveWord } from '@/shared/ui/archive';
import { useCallback, useId, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { Search, TriangleAlert, X } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { Modal, ModalExit } from '@/shared/ui/Modal';
import { WindowStep } from '@/shared/ui/WindowStep';
import { apiError } from '@/shared/utils/apiError';
import { isNotFound, useNomenclature } from '@/shared/api/invoices';
import { useNomenclatureCard, type NomenclatureCard } from '@/shared/api/nomenclatureCard';
import { useNomenclatureCandidates } from '@/shared/api/nomenclatureCandidates';
import { WIDE, useMedia } from '@/shared/hooks/useMedia';
import { useCan } from '@/shared/api/access';
import { NewNomenclatureStep } from './NewNomenclatureStep';
import { NomenclatureCardPanel } from './NomenclatureCardPanel';
import { trimText, type LineWords } from './newNomenclature';
import {
  candidateRows, foundRows, lineVsCard, looksLikeArticle, neighbourCandidates, type NeighbourLine, type PickerRow,
} from './pickerCandidates';
import { CandidatesBlock, PickerOption, type PickerCurrent } from './PickerRows';
import { plateMeta, type PickerLine } from './pickerLine';
import { usePickerSelection } from './pickerSelection';
import { Tooltip } from '@/shared/ui/Tooltip';

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
 * <p>⚠️ Новая позиция заводится только явным действием и с показом похожих (ТЗ COST-7.1, задача C3,
 * issue #1079) — вторым шагом этого же окна, см. `NewNomenclatureStep`. Кнопка видна при праве
 * <c>core.nomenclature.edit</c>: у того, кто вводит счета, его может не быть, и кнопка, отказывающая
 * правами, обещала бы то, чего нет. Без права на её месте сказано, кто заводит позиции.</p>
 */
export function NomenclaturePicker({
  chosen, chosenId = null, name, lost, lostText, archived, mark, clearable = true, from, line, supplierId = null,
  neighbours = NO_NEIGHBOURS, onPick, onClear,
}: {
  /**
   * Ссылка на позицию ЕСТЬ. Отдельно от названия: пустое название бывает и у выбранной позиции —
   * записи справочника без имени законны, и пикер их показывает.
   */
  chosen: boolean;
  /** Выбранная позиция: в кандидатах её нет — она стоит первой строкой «Сейчас: …» (issue #1518). */
  chosenId?: string | null;
  /** Название выбранной позиции; `null` — имени у записи нет либо позиция не выбрана. */
  name: string | null;
  /** Ссылка есть, а записи справочника НЕТ — позицию удалили. Считает сервер, см. `InvoiceLineView`. */
  lost?: boolean;
  /** Чем назвать потерю, если это не удалённая запись: «позиция другого вида». */
  lostText?: string;
  /** Выбранная позиция в архиве: названа как была, а в поиске её уже нет (issue #1185). */
  archived?: boolean;
  /** Пометка рядом с позицией — «запомнено», «запомнится» (C3, issue #1079). */
  mark?: ReactNode;
  /**
   * Показывать ли крестик «Снять позицию». У подставленной позиции его нет: её снимает крестик
   * пометки («Отменить подстановку»), и два крестика рядом с разным смыслом были бы ловушкой.
   */
  clearable?: boolean;
  /** Слова строки, для которой выбирают позицию: ими заполняется окно «Новая позиция». */
  from?: LineWords;
  /** Для какой строки открыто окно — плашка под заголовком (issue #1517). */
  line?: PickerLine;
  /** Поставщик документа: его соответствия кандидатами не идут, запомненное в архиве — названо. */
  supplierId?: string | null;
  /** Соседние строки этого же документа с выбранной позицией — кандидаты по тому же артикулу или имени. */
  neighbours?: readonly NeighbourLine[];
  onPick: (id: string, name: string | null) => void;
  onClear: () => void;
}) {
  const [open, setOpen] = useState(false);

  return (
    <>
      <div className="flex items-center gap-1">
        <Tooltip content={archived && !lost ? ARCHIVED_HINT : undefined}>
          <button type="button" onClick={() => setOpen(true)}
            className={`min-w-0 flex-1 text-left text-xs px-2 py-1 rounded border truncate
              ${lost ? 'border-danger/30 text-danger'
                : chosen ? 'border-stroke text-fg1' : 'border-warning-border text-warning'}`}>
            {/* Три состояния, и путать их нельзя: потерю чинит справочник, пустое имя — тоже справочник,
                но позиция на месте, а «выбрать позицию» — работа человека за формой. */}
            {lost ? lostText ?? LOST.position
              : chosen ? withArchiveWord(name ?? 'позиция без названия', !!archived) : 'выбрать позицию'}
          </button>
        </Tooltip>
        {/* ⚠️ Снять ссылку можно ВСЕГДА, пока она есть, — и особенно когда позиция потеряна: сервер
            отказывает сохранять строку с битой ссылкой, а спрятанная кнопка не оставляла человеку
            вообще никакого выхода (ревью PR #1117). */}
        {mark}
        {chosen && clearable && (
          <Tooltip content="Снять позицию" name>
            <button type="button" onClick={onClear}
              className="shrink-0 text-fg4 hover:text-fg1 p-0.5">
              <X size={12} />
            </button>
          </Tooltip>
        )}
      </div>

      {open && (
        <PickerDialog from={from ?? NO_WORDS} line={line} supplierId={supplierId} neighbours={neighbours}
          current={chosen ? { id: chosenId, name, archived, lost, lostText } : null}
          onClose={() => setOpen(false)}
          onPick={(id, picked) => { onPick(id, picked); setOpen(false); }} />
      )}
    </>
  );
}

const NO_WORDS: LineWords = {};
const NO_NEIGHBOURS: readonly NeighbourLine[] = [];

/**
 * Окно уже этой ширины — карточке рядом со списком места нет (issue #1517): она открывается шагом окна
 * по нажатию на строку. Меряется окно, а не экран: на планшете окно уже экрана.
 */
const NARROW_BODY = 720;

function useNarrow() {
  // Узел — состоянием, а не `useRef`: тело окна появляется позже первого эффекта, и наблюдатель,
  // подключаемый один раз при монтировании, не подключался вовсе — окно не узнавало, что сузилось.
  const [node, setNode] = useState<HTMLDivElement | null>(null);
  const [narrow, setNarrow] = useState(() => window.innerWidth < NARROW_BODY);
  // Первая мерка — в момент появления узла, до первого кадра: догадка по ширине экрана расходится с
  // окном у экранов чуть шире порога, и окно мигнуло бы двумя колонками.
  const ref = useCallback((el: HTMLDivElement | null) => {
    setNode(el);
    const dialog = el?.closest<HTMLElement>('[role="dialog"]');
    if (dialog) setNarrow(dialog.offsetWidth < NARROW_BODY);
  }, []);
  useLayoutEffect(() => {
    // Само окно, а не тело списка: тело прячется под шагом окна («Новая позиция», карточка), и его нулевая
    // ширина читалась бы как «узко» — а пока шаг открыт, поворот планшета не доходил бы до него вовсе.
    // `offsetWidth`, а не прямоугольник: окно появляется с масштабом, и мерка шла бы по анимации.
    const dialog = node?.closest<HTMLElement>('[role="dialog"]');
    if (!dialog) return;
    const observer = new ResizeObserver(() => setNarrow(dialog.offsetWidth < NARROW_BODY));
    observer.observe(dialog);
    return () => observer.disconnect();
  }, [node]);
  return { ref, narrow };
}

/**
 * Тело монтируется по открытию: запрос заводится заново, и эффекта «закрылось — очисти» не нужно.
 *
 * <p>Окно — `Modal wide` (issue #1517): справа от списка карточка выделенной позиции. Одно выделение на
 * список (`aria-activedescendant`): стрелки меняют карточку сразу, мышь — после покоя над строкой.
 * Нажатие на строку выбирает её; на узком теле — открывает карточку шагом окна.</p>
 *
 * <p><b>Пустой запрос — кандидаты, а не голова алфавита</b> (issue #1518): первые 25 позиций по алфавиту
 * этой строке не подходят ничем. Набор — обычный поиск; стёр — кандидаты снова, из кэша.</p>
 */
function PickerDialog({ from, line, current, supplierId, neighbours, onClose, onPick }: {
  from: LineWords;
  line?: PickerLine;
  current: (PickerCurrent & { id: string | null }) | null;
  supplierId: string | null;
  neighbours: readonly NeighbourLine[];
  onClose: () => void;
  onPick: (id: string, name: string | null) => void;
}) {
  const [typed, setTyped] = useState('');
  const [query, setQuery] = useState('');
  const timer = useRef<number | null>(null);
  const found = useNomenclature(query);
  // Шаг «Новая позиция» живёт в этом же окне: набранный запрос цел, пока окно открыто.
  const [creating, setCreating] = useState(false);
  // Карточка шагом окна — на узком теле.
  const [opened, setOpened] = useState<PickerRow | null>(null);
  const canCreate = useCan().permission('core.nomenclature.edit');
  const { ref, narrow } = useNarrow();
  const phone = !useMedia(WIDE);
  const listId = useId();
  // Удалённую или убранную в архив за время показа в строку не выбрать — кнопки в подвале шага нет.
  const openedCard = useNomenclatureCard(opened?.id ?? null);
  const openedPickable = !(openedCard.isError && isNotFound(openedCard.error)) && !openedCard.data?.archived;
  // В поле зрения выделение ведёт только стрелка: строка, выделенная мышью, под ней и так видна, а
  // прокрутка из-под курсора сдвигала бы список, и он полз бы сам.
  const byKeys = useRef(false);

  // Наименование — как отправлено на сервер: по нему же подсвечиваются слова в карточке.
  const source = trimText(from.name);
  const code = trimText(from.code);
  const question = source || code ? { supplierId, name: source, code } : null;
  const candidates = useNomenclatureCandidates(question);
  const near = neighbourCandidates(from, neighbours);
  const picked = candidateRows(candidates.isPending ? undefined : candidates.data, candidates.isPending ? [] : near,
    current?.id ?? null);

  // Задержка в обработчике, а не в эффекте: запрос на каждую букву — это двадцать обращений на одно
  // слово, а состояние, поставленное из эффекта, вызывает лишний кадр (правило react-hooks).
  function type(next: string) {
    setTyped(next);
    if (timer.current !== null) window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => setQuery(next.trim()), 250);
  }

  const searching = typed.trim() !== '';
  // Справочник целиком — только когда он умещается: голова алфавита большого справочника не подходит
  // строке ничем, а жёлтая оговорка о неполноте — для запроса человека, а не для пустого поля.
  const head = !searching && query === '' && !found.isPlaceholderData ? found.data : undefined;
  const whole = !!head && !head.more;
  const shown = new Set([...picked.rows.map(r => r.id), current?.id]);
  const directory = whole ? foundRows(head!.items, []).filter(r => !shown.has(r.id)) : [];
  // Пока задержка не отпустила набранное, на экране ответ на прежний запрос — пустой, если прежний был «».
  const results = searching && query !== '' ? foundRows(found.data?.items ?? [], picked.rows) : [];
  const rows = searching ? results : [...picked.rows, ...directory];
  const best = searching ? null : picked.rows.find(r => r.best)?.id ?? null;
  const selection = usePickerSelection(rows.map(r => r.id), best);
  const optionId = (id: string) => `${listId}-${id}`;
  // Вид у строки — только когда в выдаче их больше одного: одинаковая метка у всех ничего не различает.
  const kinds = new Set(rows.map(r => r.type).filter(Boolean)).size > 1;
  const active = rows.find(r => r.id === selection.active) ?? null;
  // Enter выбирает выделенную, только пока она среди ЭТОГО ответа: прошлый ответ стоит на экране, пока
  // едет новый, и Enter по нему выбрал бы строку, которой под набранным уже нет.
  const fresh = !searching || (query === typed.trim() && !found.isPlaceholderData);
  const enterable = fresh && selection.index >= 0;

  function choose(row: PickerRow) {
    if (narrow && !row.best) { selection.select(row.id); setOpened(row); }
    else onPick(row.id, row.name);
  }

  function keys(e: React.KeyboardEvent) {
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      byKeys.current = true;
      selection.step(e.key === 'ArrowDown' ? 1 : -1);
      return;
    }
    if (e.key === 'Enter' && enterable) {
      e.preventDefault();
      choose(rows[selection.index]);
    }
  }

  // Выделенная стрелкой строка — в поле зрения: без этого стрелка уводила бы выделение за край списка.
  const activeOption = selection.index >= 0 && selection.active ? optionId(selection.active) : undefined;
  useLayoutEffect(() => {
    if (activeOption && byKeys.current) document.getElementById(activeOption)?.scrollIntoView({ block: 'nearest' });
    byKeys.current = false;
  }, [activeOption]);

  const plate = <LinePlate from={from} line={line} />;
  // Сравнение со строкой — у любой позиции: артикул и единицу сверяет карточка по тэгам полей, слова — сервер.
  const compare = (row: PickerRow | null) => (row && (row.matched || code || trimText(from.unit))
    ? { title: 'Строка ↔ позиция', rows: (card: NomenclatureCard) => lineVsCard(source, from, row.matched, card) }
    : undefined);
  const option = (row: PickerRow) => (
    <PickerOption key={row.id} row={row} id={optionId(row.id)} active={row.id === selection.active}
      enter={!narrow && enterable && row.id === selection.active} narrow={narrow} kinds={kinds}
      onChoose={choose} onHover={selection.hover} />
  );
  // Подбор ничего не нашёл — заводить новую становится вероятным следующим шагом, и кнопка это показывает.
  const nothing = !searching && candidates.isSuccess && picked.rows.length === 0;

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Позиция номенклатуры" wide flushBody nothingToLose
      footer={(
        <div className="flex items-center justify-between gap-3">
          {/* Кнопка — всегда, а не только при пустом поиске: двадцать пять неподходящих находок —
              такой же повод завести позицию, как ни одной. */}
          {canCreate ? (
            <Button size="sm" variant={nothing ? 'outlined' : 'text'} onClick={() => setCreating(true)}>
              Завести новую позицию…
            </Button>
          ) : (
            <p className="text-caption text-fg4">
              Новую позицию заводит тот, кто ведёт справочник номенклатуры. Строку можно сохранить без позиции.
            </p>
          )}
          <ModalExit>Закрыть</ModalExit>
        </div>
      )}>
      <div ref={ref} className={narrow ? 'flex-1 min-h-0 overflow-y-auto'
        : 'flex-1 min-h-0 grid grid-cols-[minmax(0,55fr)_minmax(0,45fr)] border-t border-stroke'}>
        <div className={`space-y-3 px-6 pt-3 pb-4 ${narrow ? '' : 'overflow-y-auto'}`}>
          {plate}
          <div className="flex items-center gap-2 rounded-md border border-stroke-strong px-2.5 h-10">
            <Search size={15} className="text-fg4 shrink-0" />
            {/* Курсор ставим ссылкой на узел, а не атрибутом `autoFocus`: пикер открывают, чтобы
                искать, но атрибут ругает линтер (он бьёт по фокусу на загрузке страницы), а эффект
                рисовал бы лишний кадр. На телефоне при кандидатах — нет: клавиатура закрыла бы их. */}
            <input ref={node => { if (!(phone && question)) node?.focus(); }} value={typed}
              onChange={e => type(e.target.value)} onKeyDown={keys}
              placeholder="наименование или часть наименования" aria-label="Поиск позиции" data-search=""
              role="combobox" aria-expanded aria-controls={listId} aria-autocomplete="list"
              aria-activedescendant={activeOption}
              className="flex-1 bg-transparent text-sm outline-none text-fg1 placeholder:text-fg4" />
          </div>

          {found.isError && (
            <p className="flex items-start gap-2 text-xs text-danger">
              <TriangleAlert size={13} className="shrink-0 mt-0.5" />
              <span>Справочник не прочитан: {apiError(found.error, 'сервер отказал')}. ⚠️ Это НЕ
                «позиций нет»: их список сюда не доехал.</span>
            </p>
          )}

          <div id={listId} role="listbox" aria-label={searching ? 'Найденные позиции' : 'Подходящие позиции'}
            onMouseLeave={selection.leave} className="space-y-3">
            {searching ? (
              <div className="-mx-2 space-y-0.5">{results.map(option)}</div>
            ) : (
              <>
                <CandidatesBlock asked={question !== null} answer={candidates} rows={picked.rows.length}
                  more={picked.more} current={current}>
                  {picked.rows.length > 0 && <div className="-mx-2 space-y-0.5">{picked.rows.map(option)}</div>}
                </CandidatesBlock>
                {directory.length > 0 && (
                  <div className="space-y-1.5">
                    <h4 className="text-xs font-medium text-fg2">Весь справочник ({head!.items.length})</h4>
                    <div className="-mx-2 space-y-0.5">{directory.map(option)}</div>
                  </div>
                )}
              </>
            )}
          </div>

          {head?.more && (
            <p className="text-xs text-fg4">Ищите по наименованию — справочник большой, весь список здесь не показывается.</p>
          )}
          {whole && head!.items.length === 0 && (
            <p className="text-xs text-fg4">Справочник номенклатуры пуст.</p>
          )}

          {searching && fresh && found.isSuccess && !found.isFetching && results.length === 0 && (
            <p className="text-xs text-fg4">
              {looksLikeArticle(query) && question
                ? 'Поиск идёт по наименованию; по артикулу подбирает блок «Подходящие» — очистите поле.'
                : canCreate
                  ? 'По этому запросу позиций нет. Проверьте другое написание — или заведите новую позицию.'
                  : 'По этому запросу позиций нет. Заводит их ответственный за справочник — строку можно '
                    + 'оставить без позиции, счёт будет ждать в отборе «Разобрать».'}
            </p>
          )}

          {/* Архивные позиции в выбор не попадают — и молчать о них нельзя: не найдя позицию,
              человек заведёт её заново, а она есть. Действия здесь нет нарочно: вернуть из архива
              отсюда пока нечем. */}
          {searching && !!found.data?.inArchive && (
            <p className="text-xs text-fg3">
              В архиве под этот запрос: {found.data.inArchive}. В выборе архивных позиций нет — заводить
              такую же заново не нужно.
            </p>
          )}

          {/* ⚠️ Оговорка о неполноте — обязательна: см. описание пикера. Только на запросе человека. */}
          {searching && found.data?.more && (
            <p className="text-xs text-warning">
              Показаны первые {results.length} — подходящих больше. Уточните запрос: по неполному списку
              нельзя решить, что позиции в справочнике нет.
            </p>
          )}
          {narrow && rows.length > 0 && (
            <p className="text-caption text-fg4">Нажмите на позицию, чтобы открыть её карточку.</p>
          )}
        </div>

        {/* Карточка остаётся, когда курсор ушёл со списка: её уводят читать, а не терять. */}
        {!narrow && (
          <aside aria-label="Карточка позиции" className="overflow-y-auto border-l border-stroke px-5 py-4">
            <NomenclatureCardPanel id={selection.active} compare={compare(active)}
              head={active ? { name: active.name, type: active.type, why: active.why } : null} onPick={onPick} />
          </aside>
        )}
      </div>

      {opened && (
        <WindowStep title="Карточка позиции" onBack={() => setOpened(null)}
          footer={(
            <div className="flex items-center gap-2">
              <ModalExit>К списку</ModalExit>
              {openedPickable && (
                <Button variant="filled" className="flex-1" onClick={() => onPick(opened.id, opened.name)}>
                  Выбрать эту позицию
                </Button>
              )}
            </div>
          )}>
          <NomenclatureCardPanel id={opened.id} head={{ name: opened.name, type: opened.type, why: opened.why }}
            compare={compare(opened)} pick={null} onPick={onPick} />
        </WindowStep>
      )}

      {creating && (
        <NewNomenclatureStep from={from} plate={plate} narrow={narrow}
          onBack={() => setCreating(false)} onPick={onPick} />
      )}
    </Modal>
  );
}

/** Для какой строки открыто окно: где, слова поставщика целиком (до двух строк), артикул и количество. */
function LinePlate({ from, line }: { from: LineWords; line?: PickerLine }) {
  const meta = plateMeta(from, line?.quantity);
  const name = (from.name ?? '').trim();
  if (!line && !name && !meta) return null;
  return (
    <div className="rounded-lg bg-muted px-3 py-2 space-y-0.5">
      {line && <p className="text-caption text-fg3">{line.where}</p>}
      {name && <p className="text-sm leading-snug text-fg1 line-clamp-2" title={name}>{name}</p>}
      {meta && <p className="text-xs text-fg3 tabular-nums">{meta}</p>}
    </div>
  );
}
