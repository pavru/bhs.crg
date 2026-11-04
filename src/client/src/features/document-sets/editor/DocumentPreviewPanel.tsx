import { useCallback, useEffect, useRef, useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { CircularProgress } from '@/shared/ui/CircularProgress';
import { Refresh, ScanDelete } from '@/shared/ui/icons';
import { previewDocument, type PreviewResult } from '@/shared/api/documentSets';
import { Button, IconButton } from '@/shared/ui/Button';
import { SideSheet } from '@/shared/ui/SideSheet';
import { setDocumentPreviewOpen, useDocumentPreview } from './documentPreviewOpen';
import { PdfViewer } from '@/shared/ui/viewer/PdfViewer';
import { preloadPdfViewer } from '@/shared/ui/viewer/viewerGate';

type State =
  | { kind: 'idle' }
  | { kind: 'loading' }
  | { kind: 'pdf'; blob: Blob }
  | { kind: 'no-template' }
  | { kind: 'error'; message: string };

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

  return (
    <SideSheet variant="standard" open={open} onClose={() => setDocumentPreviewOpen(false)} autoFocus={false}
      title="Предпросмотр" label="Предпросмотр документа" closeLabel="Скрыть предпросмотр"
      actions={<IconButton label="Обновить" onClick={() => void run()} disabled={state.kind === 'loading'} loading={busy} icon={Refresh} />}>
      <div data-testid="document-preview" className="flex flex-col h-full gap-2">
      {updatedAt && state.kind === 'pdf' && (
        <p className="shrink-0 text-body-small text-on-surface-variant">обновлено {timeAgo(updatedAt)}</p>
      )}
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
    </SideSheet>
  );
}

function timeAgo(ts: number): string {
  const s = Math.round((Date.now() - ts) / 1000);
  if (s < 5) return 'только что';
  if (s < 60) return `${s} сек назад`;
  const m = Math.round(s / 60);
  return `${m} мин назад`;
}
