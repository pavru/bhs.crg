import * as Dialog from '@radix-ui/react-dialog';
import { useId, useState, type FormEvent, type ReactNode } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Icon } from '@/shared/ui/Icon';
import { ChevronRight, KeyboardArrowDown, Warning } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { useToast } from '@/shared/ui/Toast';
import { ArchivedMark } from '@/shared/ui/ArchivedMark';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { useHistoryLayer } from '@/shared/ui/historyLayers';
import { toggleInSet } from '@/shared/utils/toggleInSet';
import { apiError } from '@/shared/utils/apiError';
import {
  previewPurgeBatch, usePurgeBatch, type PurgeBatchPreview, type PurgeBatchSource, type PurgeOffer,
} from '@/shared/api/recordPurge';
import { countMatches, holderLabel, mismatchShown } from './purgeRecord';
import {
  batchConsequences, batchFailure, carriedOver, batchSubtitle, batchTitle, batchToast, emptyText, excludedGroups, isNamed,
  leftOut, moreNote, wasNote,
} from './purgeBatch';

/**
 * Пакетная очистка: удалить разом записи, которые держат только данные выключенных или снятых
 * модулей (issue #1245). То же действие, что `PurgeRecordDialog`, над многими записями.
 *
 * Открывается сразу и сам спрашивает у сервера, что войдёт в пакет: источник — готовый отбор по
 * типу или записи, отмеченные в списке. Человек вводит ОБЩЕЕ число теряемых ссылок; удаляется весь
 * показанный список или ничего. Изменилось что-то, пока диалог был открыт, — сервер присылает
 * свежий список, и он показывается заново с пустым полем.
 */
export function PurgeBatchDialog({ source: opened, typeName, onClose }: {
  source: PurgeBatchSource;
  typeName?: string;
  /** `done` — пакет удалён: вызывающий выходит из режима выбора. */
  onClose: (done: boolean) => void;
}) {
  const purge = usePurgeBatch();
  const toast = useToast();
  const [source, setSource] = useState(opened);
  // Свежий список из отказа «пакет изменился» — поверх запрошенного. `before` — что было показано.
  const [fresh, setFresh] = useState<{ now: PurgeBatchPreview; before: PurgeBatchPreview; note: string } | null>(null);
  const [input, setInput] = useState('');
  const [settled, setSettled] = useState(false);
  const [refused, setRefused] = useState<string | null>(null);
  // Ответа на удаление нет: удалён ли пакет, неизвестно. Хранит записи пакета — перепроверять по ним.
  const [unknown, setUnknown] = useState<string[] | null>(null);
  const [open, setOpen] = useState<Set<string>>(new Set());
  // Номер попытки — в ключе запроса: «Проверить заново» обязано дойти до сервера и тогда, когда
  // источник не изменился (те же записи в том же порядке), иначе диалог показал бы прежний ответ.
  const [attempt, setAttempt] = useState(0);
  const fieldId = useId(), hintId = useId(), errorId = useId();

  const asked = useQuery({
    queryKey: ['purge-batch-preview', source, attempt],
    queryFn: () => previewPurgeBatch(source),
    gcTime: 0, staleTime: Infinity, retry: false, refetchOnWindowFocus: false,
  });
  const preview = fresh?.now ?? asked.data;

  const columns: DataGridColumn<PurgeBatchPreview['records'][number]>[] = [
    { key: 'name', label: 'Будут удалены', wrap: true,
      cell: r => {
        const shown = open.has(r.id);
        return (
          <>
            <button type="button" aria-expanded={shown} onClick={() => setOpen(was => toggleInSet(was, r.id))}
              className="flex items-start gap-1 text-left outline-none focus-visible:ring-2 focus-visible:ring-primary rounded">
              {shown ? <Icon icon={KeyboardArrowDown} size={18} className="mt-0.5 shrink-0 text-fg4" /> : <Icon icon={ChevronRight} size={18} className="mt-0.5 shrink-0 text-fg4" />}
              <span>{r.name}{r.archived && <ArchivedMark className="ml-1.5" />}</span>
            </button>
            {r.untraceable > 0 && !shown && <div className="pl-[17px] text-warning">{r.untraceable} не покажет никто</div>}
            {shown && r.holders.map((h, i) => (
              <div key={i} className="pl-[17px] text-on-surface-variant">
                {holderLabel(h)} — {h.rows}
                {h.documents && <span className="text-fg3"> ({h.documents})</span>}
                {!h.traceable && <div className="text-warning">после удаления эти ссылки не покажет никто</div>}
              </div>
            ))}
          </>
        );
      } },
    { key: 'references', label: 'Ссылок', align: 'right', width: 'w-24',
      cell: r => {
        const was = wasNote(fresh?.before ?? null, r.id, r.references);
        return <>{r.references}{was && <span className="text-warning"> ({was})</span>}</>;
      } },
  ];
  const offer: PurgeOffer | null = preview
    ? { allowed: true, references: preview.references, untraceable: preview.untraceable, holders: [] } : null;
  const matches = !!offer && countMatches(input, offer);
  const mismatch = !!offer && mismatchShown(input, offer, settled);
  const busy = purge.isPending;
  // «Назад» на телефоне (issue #1418) — «Закрыть»; пока идёт удаление, окно не закрывается ничем.
  useHistoryLayer(true, () => {
    if (busy) return false;
    onClose(false);
    return true;
  });

  async function submit(e: FormEvent) {
    e.preventDefault();
    setSettled(true);
    if (!preview || !matches || busy) return;
    try {
      const done = await purge.mutateAsync(preview);
      toast.success(batchToast(done, leftOut(preview)));
      onClose(true);
    } catch (err) {
      const failure = batchFailure(err);
      if (failure.kind === 'changed') {
        setFresh({ now: carriedOver(preview, failure.preview), before: preview, note: failure.note });
        setInput('');
        setSettled(false);
      } else if (failure.kind === 'final') setRefused(failure.text);
      else setUnknown(preview.records.map(r => r.id));
    }
  }

  // Перепроверка после сбоя — по записям пакета, а не по типу: отбор по типу ответил бы «удалять
  // нечего» и на удалённый пакет, и переодел бы «удалено» в «нечего было удалять».
  function recheck() {
    if (!unknown) return;
    setSource({ ids: unknown });
    setAttempt(n => n + 1);
    setFresh(null);
    setUnknown(null);
    setInput('');
    setSettled(false);
  }

  let title = 'Удаление записей с потерей ссылок';
  let body: ReactNode;
  if (refused) {
    title = 'Удаление невозможно';
    body = <Notice tone="danger" text={refused} close={() => onClose(false)} />;
  } else if (unknown) {
    title = 'Ответ не получен';
    body = (
      <>
        <Alert tone="danger">Ответ сервера не получен. Удалены ли записи — неизвестно: проверьте, прежде чем повторять.</Alert>
        <div className="flex gap-2 justify-end mt-4">
          <Button variant="text" size="sm" onClick={() => onClose(false)}>Закрыть</Button>
          <Button variant="tonal" size="sm" onClick={recheck}>Проверить заново</Button>
        </div>
      </>
    );
  } else if (asked.isError && !preview) {
    body = (
      <>
        <Alert tone="danger">Не удалось проверить записи. {apiError(asked.error, '')}</Alert>
        <div className="flex gap-2 justify-end mt-4">
          <Button variant="text" size="sm" onClick={() => onClose(false)}>Закрыть</Button>
          <Button variant="tonal" size="sm" loading={asked.isFetching} onClick={() => void asked.refetch()}>Повторить</Button>
        </div>
      </>
    );
  } else if (!preview) {
    body = (
      <>
        <p className="text-xs text-fg3" role="status">Проверяем записи…</p>
        <div className="flex justify-end mt-4"><Button variant="text" size="sm" onClick={() => onClose(false)}>Отмена</Button></div>
      </>
    );
  } else {
    title = batchTitle(preview);
    const more = moreNote(preview);
    body = (
      <form onSubmit={submit} noValidate>
        <Dialog.Description className="text-xs text-on-surface-variant">{batchSubtitle(source, typeName, preview)}</Dialog.Description>
        {fresh && <Alert tone="warning">{fresh.note}</Alert>}

        {preview.records.length === 0 ? (
          <p className="mt-3 text-xs text-on-surface">{emptyText(source, typeName, preview)}</p>
        ) : (
          <>
            {/* Все строки, прокрутка внутри: удаляемое обязано быть обозримо целиком, без «показать ещё». */}
            <DataGrid density="compact" className="mt-3 max-h-64" columns={columns} rows={preview.records} rowKey={r => r.id} />
            {/* Итог — вне прокрутки, всегда на виду. Число нарочно не повторено ни в подписи поля,
                ни внутри него: чтобы его перепечатать, взгляд обязан пройти по списку. */}
            <div className="flex justify-between px-2 py-1.5 text-xs font-semibold text-on-surface">
              <span>Всего ссылок</span><span className="tabular-nums">{preview.references}</span>
            </div>
            {more && <p className="text-xs text-fg3">{more}</p>}
          </>
        )}

        <Excluded preview={preview} named={isNamed(source)} />

        {preview.records.length === 0 ? (
          <div className="flex justify-end mt-4"><Button type="button" variant="tonal" size="sm" onClick={() => onClose(false)}>Понятно</Button></div>
        ) : (
          <>
            <ul className="mt-3 text-xs text-on-surface-variant list-disc pl-4 space-y-1">
              {batchConsequences(preview).map(line => <li key={line}>{line}</li>)}
            </ul>
            <div className="mt-4">
              <label htmlFor={fieldId} className="block text-xs font-medium text-on-surface-variant mb-1">Число теряемых ссылок</label>
              <input id={fieldId} type="text" inputMode="numeric" autoComplete="off" autoFocus
                value={input} disabled={busy}
                onChange={e => { setInput(e.target.value); setSettled(false); }}
                onBlur={() => setSettled(true)}
                onKeyDown={e => { if (e.key === 'Enter') setSettled(true); }}
                aria-invalid={mismatch || undefined} aria-describedby={mismatch ? errorId : hintId}
                className={`w-full h-10 px-3 rounded-md bg-surface border text-base sm:text-sm text-on-surface tabular-nums outline-none focus-visible:ring-2 ${
                  mismatch ? 'border-error focus-visible:ring-error' : 'border-outline focus-visible:ring-primary'}`} />
              {mismatch
                ? <p id={errorId} role="alert" className="mt-1 text-xs text-error">Не совпадает с числом в строке «Всего ссылок».</p>
                : <p id={hintId} className="mt-1 text-xs text-fg3">Введите число из строки «Всего ссылок».</p>}
            </div>
            <div className="flex gap-2 justify-end items-start mt-4">
              <Button type="button" variant="text" size="sm" className="shrink-0" disabled={busy} data-modal-exit="" onClick={() => onClose(false)}>Отмена</Button>
              <Button type="submit" variant="filled" danger size="sm" multiline className="min-w-0" disabled={!matches} loading={busy}>
                Удалить записи, потеряв ссылки
              </Button>
            </div>
          </>
        )}
      </form>
    );
  }

  return (
    <Dialog.Root open onOpenChange={o => { if (!o && !busy) onClose(false); }}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-black/40" style={{ backdropFilter: 'blur(2px)' }} />
        <Dialog.Content
          // То же, что у `PurgeDialog`: запрос, исход или набранное подтверждение держат человека.
          data-modal-state={busy ? 'busy' : refused ?? unknown ?? fresh ? 'result' : input ? 'touched' : 'free'}
          className="fixed left-1/2 top-4 sm:top-1/2 -translate-x-1/2 sm:-translate-y-1/2 z-50 rounded-[28px] p-6 w-[calc(100%-2rem)] max-w-xl max-h-[calc(100dvh-2rem)] overflow-y-auto bg-surface border border-outline-variant focus:outline-none"
          style={{ boxShadow: 'var(--md-sys-elevation-level3)' }}>
          <Dialog.Title className="text-sm font-semibold mb-2 text-on-surface">{title}</Dialog.Title>
          {body}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

function Alert({ tone, children }: { tone: 'danger' | 'warning'; children: ReactNode }) {
  return (
    <div role="alert" className={`mt-2 flex items-start gap-2.5 rounded-md px-3 py-2.5 text-xs text-on-surface ${tone === 'danger' ? 'bg-error-container' : 'bg-warning-container'}`}>
      <Icon icon={Warning} size={18} className={`shrink-0 mt-0.5 ${tone === 'danger' ? 'text-error' : 'text-warning'}`} aria-hidden />
      <div className="min-w-0 whitespace-pre-line">{children}</div>
    </div>
  );
}

function Notice({ tone, text, close }: { tone: 'danger' | 'warning'; text: string; close: () => void }) {
  return (
    <>
      <Alert tone={tone}>{text}</Alert>
      <div className="flex justify-end mt-4"><Button variant="tonal" size="sm" onClick={close}>Понятно</Button></div>
    </>
  );
}

/**
 * «Не войдут» — группами по причине, нейтральным цветом: это не ошибка, а объяснение. У выбора
 * галочками короткий список раскрыт сразу — человек отмечал эти записи сам и ждёт о них ответа.
 */
function Excluded({ preview, named }: { preview: PurgeBatchPreview; named: boolean }) {
  const count = leftOut(preview);
  if (count === 0) return null;
  return (
    <details className="mt-3 text-xs" open={named && count <= 5}>
      <summary className="cursor-pointer text-on-surface-variant font-medium">Не войдут: {count}</summary>
      <ul className="mt-1 space-y-1.5 text-on-surface-variant">
        {excludedGroups(preview.excluded).map(g => (
          <li key={g.reason}>
            <div className="text-on-surface">{g.names.join(', ')}</div>
            <div className="text-fg3">{g.reason}</div>
          </li>
        ))}
        {preview.missing > 0 && <li className="text-fg3">Записей уже нет в справочнике: {preview.missing}.</li>}
      </ul>
    </details>
  );
}
