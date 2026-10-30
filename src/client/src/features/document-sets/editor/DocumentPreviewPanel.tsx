import { useCallback, useEffect, useRef, useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { CircularProgress } from '@/shared/ui/CircularProgress';
import { Description, Refresh, RightPanelClose, RightPanelOpen, ScanDelete } from '@/shared/ui/icons';
import { previewDocument, type PreviewResult } from '@/shared/api/documentSets';
import { Tooltip } from '@/shared/ui/Tooltip';
import { Button, IconButton } from '@/shared/ui/Button';
import { PdfViewer } from '@/shared/ui/viewer/PdfViewer';
import { preloadPdfViewer } from '@/shared/ui/viewer/viewerGate';

type State =
  | { kind: 'idle' }
  | { kind: 'loading' }
  | { kind: 'pdf'; blob: Blob }
  | { kind: 'no-template' }
  | { kind: 'error'; message: string };

const LS_KEY = 'doc-preview-open';

/**
 * Панель живого предпросмотра документа (issue #193): справа от формы, реальный PDF по дефолтному
 * шаблону. Перегенерация с дебаунсом при правках + «Обновить»; single-flight (отмена устаревшего
 * ответа); сворачивается в тонкую полосу (выбор запоминается в localStorage).
 *
 * PDF показывает общий просмотрщик (issue #1493): ключ документа — сам документ, поэтому новый вариант
 * встаёт на том же листе и месте, с тем же масштабом и поворотом, без пустого кадра.
 *
 * Монтируется заново на каждый документ (`key={instance.id}` во вкладке реквизитов): вкладка при переходе
 * к другому документу не пересоздаётся, и панель показала бы PDF прежнего под ключом нового, а его
 * запоздавший ответ — как «новый вариант» нового.
 */
export function DocumentPreviewPanel({ instanceId, requisites }: {
  instanceId: string;
  requisites: unknown;
}) {
  // По умолчанию свёрнута (issue #193 follow-up): открыта только если пользователь явно её открывал.
  const [open, setOpen] = useState(() => localStorage.getItem(LS_KEY) === '1');
  const [state, setState] = useState<State>({ kind: 'idle' });
  const [updatedAt, setUpdatedAt] = useState<number | null>(null);
  // Идёт перегенерация. На обновлении прежний PDF остаётся на экране — крутится только «Обновить».
  const [busy, setBusy] = useState(false);
  const reqId = useRef(0);

  const setOpenPersist = (v: boolean) => { setOpen(v); localStorage.setItem(LS_KEY, v ? '1' : '0'); };

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
    } else {
      setState(res);
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

  if (!open) {
    return (
      <Tooltip content="Показать предпросмотр" name>
        <button type="button" onClick={() => setOpenPersist(true)}
          className="hidden xl:flex flex-col items-center gap-2 w-11 shrink-0 border-l border-outline-variant pt-4 text-on-surface-variant hover:text-primary state-layer">
          <Icon icon={RightPanelOpen} size={18} />
          <span className="text-body-small [writing-mode:vertical-rl] rotate-180">Предпросмотр</span>
        </button>
      </Tooltip>
    );
  }

  return (
    <div data-testid="document-preview" className="hidden xl:flex flex-col w-[44%] min-w-[380px] shrink-0 border-l border-outline-variant bg-surface-container/40">
      <div className="flex items-center gap-2 shrink-0 px-3 h-11 border-b border-outline-variant bg-surface">
        <Icon icon={Description} size={18} className="text-on-surface-variant shrink-0" />
        <span className="text-body-medium font-medium text-on-surface flex-1">Предпросмотр документа</span>
        {updatedAt && state.kind === 'pdf' && (
          <span className="text-body-small text-on-surface-variant">обновлено {timeAgo(updatedAt)}</span>
        )}
        <IconButton label="Обновить" onClick={() => void run()} disabled={state.kind === 'loading'} loading={busy} icon={Refresh} />
        <IconButton label="Скрыть предпросмотр" onClick={() => setOpenPersist(false)} icon={RightPanelClose} />
      </div>
      <div className="flex-1 min-h-0 relative">
        {state.kind === 'pdf' && (
          <PdfViewer source={state.blob} documentKey={instanceId} title="Предпросмотр документа"
            rememberZoomAs="document-preview" />
        )}
        {state.kind === 'loading' && (
          <div className="absolute inset-0 flex items-center justify-center text-on-surface-variant">
            <CircularProgress size={24} />
          </div>
        )}
        {state.kind === 'no-template' && (
          <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 text-center px-6 text-on-surface-variant">
            <Icon icon={ScanDelete} size={40} />
            <p className="text-body-medium">Нет шаблона для предпросмотра</p>
            <p className="text-body-small">Добавьте шаблон типа документа и отметьте его по умолчанию.</p>
          </div>
        )}
        {state.kind === 'error' && (
          <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 text-center px-6 text-on-surface-variant">
            <Icon icon={ScanDelete} size={40} className="text-error" />
            <p className="text-body-medium text-error">Не удалось построить предпросмотр</p>
            {/* whitespace-pre-line — отказ компилятора шаблона приходит несколькими строками (issue #1047). */}
            <p className="text-body-small break-words max-w-full whitespace-pre-line">{state.message}</p>
            <Button variant="text" onClick={() => void run()} className="mt-1">Повторить</Button>
          </div>
        )}
        {state.kind === 'idle' && (
          <div className="absolute inset-0 flex items-center justify-center text-on-surface-variant text-body-medium">Предпросмотр появится здесь</div>
        )}
      </div>
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
