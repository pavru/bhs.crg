import { useCallback, useEffect, useRef, useState } from 'react';
import { Loader2, RefreshCw, PanelRightClose, PanelRightOpen, FileWarning, FileText } from 'lucide-react';
import { previewDocument, type PreviewResult } from '@/shared/api/documentSets';

type State =
  | { kind: 'idle' }
  | { kind: 'loading' }
  | { kind: 'pdf'; url: string }
  | { kind: 'no-template' }
  | { kind: 'error'; message: string };

const LS_KEY = 'doc-preview-open';

/**
 * Панель живого предпросмотра документа (issue #193): справа от формы, реальный PDF по дефолтному
 * шаблону. Перегенерация с дебаунсом при правках + «Обновить»; single-flight (отмена устаревшего
 * ответа); сворачивается в тонкую полосу (выбор запоминается в localStorage).
 */
export function DocumentPreviewPanel({ instanceId, requisites }: {
  instanceId: string;
  requisites: unknown;
}) {
  // По умолчанию свёрнута (issue #193 follow-up): открыта только если пользователь явно её открывал.
  const [open, setOpen] = useState(() => localStorage.getItem(LS_KEY) === '1');
  const [state, setState] = useState<State>({ kind: 'idle' });
  const [updatedAt, setUpdatedAt] = useState<number | null>(null);
  const reqId = useRef(0);
  const urlRef = useRef<string | null>(null);

  const setOpenPersist = (v: boolean) => { setOpen(v); localStorage.setItem(LS_KEY, v ? '1' : '0'); };

  const run = useCallback(async () => {
    const id = ++reqId.current;
    setState(s => s.kind === 'pdf' ? s : { kind: 'loading' }); // при первом — спиннер; при рефреше держим старый PDF
    let res: PreviewResult;
    try { res = await previewDocument(instanceId, requisites); }
    catch (e) { res = { kind: 'error', message: e instanceof Error ? e.message : 'Ошибка' }; }
    if (id !== reqId.current) { if (res.kind === 'pdf') URL.revokeObjectURL(res.url); return; } // устарел
    if (res.kind === 'pdf') {
      if (urlRef.current) URL.revokeObjectURL(urlRef.current);
      urlRef.current = res.url;
      setState({ kind: 'pdf', url: res.url });
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

  // Освобождаем blob-URL при размонтировании.
  useEffect(() => () => { if (urlRef.current) URL.revokeObjectURL(urlRef.current); }, []);

  if (!open) {
    return (
      <button type="button" onClick={() => setOpenPersist(true)}
        title="Показать предпросмотр"
        className="hidden xl:flex flex-col items-center gap-2 w-11 shrink-0 border-l border-stroke pt-4 text-fg4 hover:text-brand hover:bg-muted transition-colors">
        <PanelRightOpen size={18} />
        <span className="text-xs [writing-mode:vertical-rl] rotate-180 tracking-wide">Предпросмотр</span>
      </button>
    );
  }

  return (
    <div className="hidden xl:flex flex-col w-[44%] min-w-[380px] shrink-0 border-l border-stroke bg-muted/40">
      <div className="flex items-center gap-2 shrink-0 px-3 h-11 border-b border-stroke bg-surface">
        <FileText size={16} className="text-fg4 shrink-0" />
        <span className="text-sm font-medium text-fg1 flex-1">Предпросмотр документа</span>
        {updatedAt && state.kind === 'pdf' && (
          <span className="text-xs text-fg4">обновлено {timeAgo(updatedAt)}</span>
        )}
        <button type="button" onClick={() => void run()} disabled={state.kind === 'loading'}
          title="Обновить" className="p-1.5 rounded-full text-fg4 hover:text-brand hover:bg-black/5 dark:hover:bg-white/10 transition-colors disabled:opacity-50">
          <RefreshCw size={15} className={state.kind === 'loading' ? 'animate-spin' : ''} />
        </button>
        <button type="button" onClick={() => setOpenPersist(false)}
          title="Скрыть предпросмотр" className="p-1.5 rounded-full text-fg4 hover:text-fg1 hover:bg-black/5 dark:hover:bg-white/10 transition-colors">
          <PanelRightClose size={16} />
        </button>
      </div>
      <div className="flex-1 min-h-0 relative">
        {state.kind === 'pdf' && (
          <iframe title="Предпросмотр документа" src={state.url} className="w-full h-full border-0 bg-white" />
        )}
        {state.kind === 'loading' && (
          <div className="absolute inset-0 flex items-center justify-center text-fg4">
            <Loader2 size={22} className="animate-spin" />
          </div>
        )}
        {state.kind === 'no-template' && (
          <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 text-center px-6 text-fg4">
            <FileWarning size={28} />
            <p className="text-sm">Нет шаблона для предпросмотра</p>
            <p className="text-xs">Добавьте шаблон типа документа и отметьте его по умолчанию.</p>
          </div>
        )}
        {state.kind === 'error' && (
          <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 text-center px-6 text-fg4">
            <FileWarning size={28} className="text-danger" />
            <p className="text-sm text-danger">Не удалось построить предпросмотр</p>
            <p className="text-xs break-words max-w-full">{state.message}</p>
            <button type="button" onClick={() => void run()} className="text-xs text-brand hover:text-brand-hover mt-1">Повторить</button>
          </div>
        )}
        {state.kind === 'idle' && (
          <div className="absolute inset-0 flex items-center justify-center text-fg4 text-sm">Предпросмотр появится здесь</div>
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
