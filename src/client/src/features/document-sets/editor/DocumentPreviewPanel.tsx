import { useCallback, useEffect, useId, useRef, useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { CircularProgress } from '@/shared/ui/CircularProgress';
import { ErrorIcon, KeyboardArrowDown, KeyboardArrowUp, Refresh, ScanDelete } from '@/shared/ui/icons';
import { previewDocument, type PreviewResult } from '@/shared/api/documentSets';
import { Button, IconButton } from '@/shared/ui/Button';
import { SideSheet } from '@/shared/ui/SideSheet';
import { formatTime } from '@/shared/format/format';
import { setDocumentPreviewOpen, useDocumentPreview } from './documentPreviewOpen';
import { PdfViewer } from '@/shared/ui/viewer/PdfViewer';
import { preloadPdfViewer } from '@/shared/ui/viewer/viewerGate';

/** Отказ сервера: построить PDF не вышло (`error`) или строить не по чему (`no-template`). */
type Failure = { kind: 'error'; message: string } | { kind: 'no-template' };

type State =
  | { kind: 'idle' }
  | { kind: 'loading' }
  // `failure` — последний запрос отказал, а на экране прежний PDF: он устарел, но лист, масштаб и поворот целы.
  | { kind: 'pdf'; blob: Blob; failure?: Failure }
  | Failure;

const NO_TEMPLATE_HINT = 'Добавьте шаблон типа документа и отметьте его по умолчанию.';

/**
 * Живой предпросмотр документа (issue #193): справа от формы, реальный PDF по дефолтному шаблону.
 * Перегенерация с дебаунсом при правках + «Обновить»; single-flight (отмена устаревшего ответа).
 *
 * Вид — standard side sheet 400 (Р9, sharedui-md3-17): открывает и закрывает его переключатель в шапке
 * документа, полосы-ручки у края формы больше нет. Фокус лист не берёт — правят форму, а смотрят в лист.
 *
 * PDF показывает общий просмотрщик (issue #1493): ключ документа — сам документ, поэтому новый вариант
 * встаёт на том же листе и месте, с тем же масштабом и поворотом, без пустого кадра.
 *
 * Отказ ОБНОВЛЕНИЯ прежний PDF не стирает (макет «Отказ обновления предпросмотра», вариант В,
 * https://claude.ai/artifact/WGUgN3ALJt6qGs8pMDXaFc): пока правят реквизит, промежуточное значение ломает
 * компиляцию шаблона, и плашка вместо PDF размонтировала бы просмотрщик — исправленный вариант открывался бы
 * с 1-го листа. Поэтому PDF остаётся (приглушённым), а строка времени постоянной высоты становится строкой
 * отказа: лист под ней не прыгает, сколько бы отказов ни пришло при наборе. Текст сервера — по «Подробнее».
 * Плашка по центру панели — только у отказа первого показа, когда PDF ещё не было.
 *
 * Монтируется заново на каждый документ (`key={instance.id}` во вкладке реквизитов): вкладка при переходе
 * к другому документу не пересоздаётся, и панель показала бы PDF прежнего под ключом нового, а его
 * запоздавший ответ — как «новый вариант» нового.
 */
export function DocumentPreviewPanel({ instanceId, requisites }: {
  instanceId: string;
  requisites: unknown;
}) {
  const { open } = useDocumentPreview();
  const [state, setState] = useState<State>({ kind: 'idle' });
  const [updatedAt, setUpdatedAt] = useState<number | null>(null);
  // Идёт перегенерация. На обновлении прежний PDF остаётся на экране — крутится только «Обновить».
  const [busy, setBusy] = useState(false);
  // Раскрыт ли текст отказа. Следующий отказ его не схлопывает (текст сменится на месте), удачный вариант — да.
  const [details, setDetails] = useState(false);
  const reqId = useRef(0);

  const run = useCallback(async () => {
    const id = ++reqId.current;
    setBusy(true);
    setState(s => s.kind === 'pdf' ? s : { kind: 'loading' }); // при первом — спиннер; при рефреше держим старый PDF
    let res: PreviewResult;
    try { res = await previewDocument(instanceId, requisites); }
    catch (e) { res = { kind: 'error', message: e instanceof Error ? e.message : 'Ошибка' }; }
    if (id !== reqId.current) return; // устарел — до просмотрщика не доходит
    setBusy(false);
    if (res.kind === 'pdf') {
      setState({ kind: 'pdf', blob: res.blob });
      setUpdatedAt(Date.now());
      setDetails(false);
    } else {
      const failure = res;
      // Отказ обновления: тот же объект Blob и тот же ключ — просмотрщик не перемонтируется и не перечитывает файл.
      setState(s => s.kind === 'pdf' ? { kind: 'pdf', blob: s.blob, failure } : failure);
    }
  }, [instanceId, requisites]);

  // Дебаунс перегенерации при изменении реквизитов (пока панель открыта).
  useEffect(() => {
    if (!open) return;
    const t = setTimeout(() => void run(), 1500);
    return () => clearTimeout(t);
  }, [open, run]);

  // Открытая панель покажет PDF — ядро просмотрщика грузится, пока строится первый вариант.
  useEffect(() => { if (open) preloadPdfViewer(); }, [open]);

  const stale = state.kind === 'pdf' ? state.failure : undefined;
  return (
    <SideSheet variant="standard" open={open} onClose={() => setDocumentPreviewOpen(false)} autoFocus={false}
      title="Предпросмотр" label="Предпросмотр документа" closeLabel="Скрыть предпросмотр"
      actions={<IconButton label="Обновить" onClick={() => void run()} disabled={state.kind === 'loading'} loading={busy} icon={Refresh} />}>
      <div data-testid="document-preview" className="flex flex-col h-full gap-2">
      {state.kind === 'pdf' && (
        stale
          ? <StaleNotice failure={stale} shownAt={updatedAt} open={details} onToggle={() => setDetails(d => !d)}
              onRetry={() => void run()} />
          // Высота строки — та же, что у строки отказа: смена одной на другую лист не сдвигает.
          : <p className="shrink-0 h-10 flex items-center text-body-small text-on-surface-variant">
              {updatedAt ? `обновлено ${timeAgo(updatedAt)}` : ''}
            </p>
      )}
      {/* Приглушается только область листа, не строка инструментов: та работает и у устаревшего PDF. Класс
          меняется на том же узле — обёртка вокруг просмотрщика перемонтировала бы его. */}
      <div className={`flex-1 min-h-0 relative ${stale ? '[&_[role=region]]:opacity-60' : ''}`}>
        {state.kind === 'pdf' && (
          <PdfViewer source={state.blob} documentKey={instanceId} title="Предпросмотр документа"
            rememberZoomAs="document-preview" />
        )}
        {state.kind === 'loading' && (
          <div className="absolute inset-0 flex items-center justify-center text-on-surface-variant">
            <CircularProgress size={24} />
          </div>
        )}
        {/* Отказ панели — один узел на все его виды, как `viewer-failure` у просмотрщика: метка, вид в
            data-state и объявление читалке. Новый вид отказа добавляется внутрь и метку получает сам. */}
        {(state.kind === 'no-template' || state.kind === 'error') && (
          <div data-testid="preview-failure" data-state={state.kind} role="alert"
            className="absolute inset-0 flex flex-col items-center justify-center gap-2 text-center px-6 text-on-surface-variant">
            {state.kind === 'no-template' ? (
              <>
                <Icon icon={ScanDelete} size={40} />
                <p className="text-body-medium">Нет шаблона для предпросмотра</p>
                <p className="text-body-small">{NO_TEMPLATE_HINT}</p>
              </>
            ) : (
              <>
                <Icon icon={ScanDelete} size={40} className="text-error" />
                <p className="text-body-medium text-error">Не удалось построить предпросмотр</p>
                {/* whitespace-pre-line — отказ компилятора шаблона приходит несколькими строками (issue #1047). */}
                <p className="text-body-small break-words max-w-full whitespace-pre-line">{state.message}</p>
                <Button variant="text" onClick={() => void run()} className="mt-1">Повторить</Button>
              </>
            )}
          </div>
        )}
        {state.kind === 'idle' && (
          <div className="absolute inset-0 flex items-center justify-center text-on-surface-variant text-body-medium">Предпросмотр появится здесь</div>
        )}
      </div>
      </div>
    </SideSheet>
  );
}

/**
 * Строка отказа обновления (вариант В): знак, что не так и от какого времени показанный вариант, «Подробнее».
 * Объявляется вежливо (`status`), а не `alert`: отказы приходят, пока человек печатает в форме, и перебивать
 * чтение поля каждые полторы секунды нельзя. Раскрытое — текст сервера и «Повторить» под строкой.
 */
function StaleNotice({ failure, shownAt, open, onToggle, onRetry }: {
  failure: Failure;
  shownAt: number | null;
  open: boolean;
  onToggle: () => void;
  onRetry: () => void;
}) {
  const detailsId = useId();
  const what = failure.kind === 'no-template' ? 'нет шаблона' : 'не удалось построить';
  return (
    <div data-testid="preview-stale" data-state={failure.kind} className="shrink-0 flex flex-col gap-2">
      <div className="h-10 flex items-center gap-1.5">
        <Icon icon={ErrorIcon} size={18} className="shrink-0 text-error" />
        <p role="status" className="flex-1 min-w-0 text-body-small text-error">
          Не обновлён: {what}{shownAt ? ` · вариант от ${formatTime(shownAt)}` : ''}
        </p>
        <Button variant="text" aria-expanded={open} aria-controls={detailsId} onClick={onToggle}
          trailingIcon={<Icon icon={open ? KeyboardArrowUp : KeyboardArrowDown} size={18} />}>
          {open ? 'Скрыть' : 'Подробнее'}
        </Button>
      </div>
      {open && (
        <div id={detailsId} className="flex flex-col gap-1 rounded-sm bg-surface-container-highest pt-3 px-3 pb-1">
          {/* Отказ компилятора — несколько строк с отступами и указателем под словом (issue #1047). */}
          <pre data-testid="preview-stale-message"
            className="max-h-24 overflow-auto font-mono text-body-small text-on-surface whitespace-pre-wrap break-words">
            {failure.kind === 'error' ? failure.message : NO_TEMPLATE_HINT}
          </pre>
          <div className="flex justify-end">
            <Button variant="text" onClick={onRetry}>Повторить</Button>
          </div>
        </div>
      )}
    </div>
  );
}

function timeAgo(ts: number): string {
  const s = Math.round((Date.now() - ts) / 1000);
  if (s < 5) return 'только что';
  if (s < 60) return `${s} сек назад`;
  const m = Math.round(s / 60);
  return `${m} мин назад`;
}
