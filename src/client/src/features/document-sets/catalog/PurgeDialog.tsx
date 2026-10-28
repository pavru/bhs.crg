import * as Dialog from '@radix-ui/react-dialog';
import { useId, useState, type FormEvent, type ReactNode } from 'react';
import { AlertTriangle } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { useToast } from '@/shared/ui/Toast';
import type { PurgeHolder, PurgeOffer } from '@/shared/api/recordPurge';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { useHistoryLayer } from '@/shared/ui/historyLayers';
import { countMatches, holderLabel, mismatchShown, type PurgeFailure } from './purgeRecord';

const HOLDER_COLUMNS: DataGridColumn<PurgeHolder>[] = [
  { key: 'holder', label: 'Кто ссылается', wrap: true,
    cell: h => (
      <>
        {holderLabel(h)}
        {h.documents && <div className="text-fg3">{h.documents}</div>}
        {!h.traceable && <div className="text-warning">после удаления эти ссылки не покажет никто</div>}
      </>
    ) },
  { key: 'rows', label: 'Ссылок', align: 'right', width: 'w-20' },
];

/** Что удаляют и какими словами об этом говорить: запись (issue #1187) или уровень (issue #1243). */
export interface PurgeSubject<O extends PurgeOffer> {
  /** «Удалить «X», потеряв ссылки?» */
  title: string;
  /** Строка над разбивкой держателей. */
  intro: string;
  /** Блок над разбивкой — что уйдёт вместе (у уровня). */
  above?: (offer: O) => ReactNode;
  /** Последствия — строками списка. */
  lines: (offer: O) => string[];
  submit: string;
  run: (references: number) => Promise<{ references: number }>;
  /** Слова успеха. */
  done: (references: number) => string;
  failure: (e: unknown, shown: O) => PurgeFailure<O>;
}

/**
 * Удаление того, что держат только данные выключенного или снятого модуля, — с потерей этих
 * ссылок. Одно окно на запись и на уровень: различаются они словами и адресом, а ввод числа,
 * пересчёт после отказа и три исхода неудачи — одни.
 *
 * Открывается только из отказа обычного удаления. Подтверждение — вводом числа теряемых ссылок. Число стоит в строке «Всего ссылок» под
 * разбивкой и нарочно НЕ повторено ни в подписи поля, ни в подсказке внутри него: чтобы его
 * перепечатать, взгляд обязан пройти по тому, что будет потеряно.
 *
 * Свой диалог, а не `ConfirmDialog`: у того подтверждение — галка, а здесь поле, разбивка и
 * пересчёт после отказа.
 */
export function PurgeDialog<O extends PurgeOffer>({ subject, offer: offered, pending, onClose, onDone }: {
  subject: PurgeSubject<O>;
  /** Предложение из отказа удаления: разбивка держателей и число. */
  offer: O;
  /** Идёт ли удаление: состояние держит мутация вызывающего. */
  pending: boolean;
  onClose: () => void;
  /** После успеха, вместо `onClose`: уровень удалён — со страницы надо уйти. */
  onDone?: () => void;
}) {
  const toast = useToast();
  // «Назад» на телефоне (issue #1418) — «Отмена»; пока идёт удаление, окно не закрывается ничем.
  useHistoryLayer(true, () => {
    if (pending) return false;
    onClose();
    return true;
  });
  const [offer, setOffer] = useState(offered);
  const [input, setInput] = useState('');
  const [settled, setSettled] = useState(false);
  const [changed, setChanged] = useState<string | null>(null);
  // Отказ, после которого выхода больше нет: модуль включили, запись стало держать ядро.
  const [refused, setRefused] = useState<string | null>(null);
  // Ответа по существу нет (сеть, сбой сервера): диалог остаётся, попытку можно повторить.
  const [failed, setFailed] = useState<string | null>(null);
  const fieldId = useId(), hintId = useId(), errorId = useId();

  const matches = countMatches(input, offer);
  const mismatch = mismatchShown(input, offer, settled);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setSettled(true);
    if (!matches || pending) return;
    setFailed(null);
    try {
      const done = await subject.run(offer.references);
      toast.success(subject.done(done.references));
      (onDone ?? onClose)();
    } catch (err) {
      const failure = subject.failure(err, offer);
      if (failure.kind === 'recount') {
        // Подтверждено не то, что удалялось бы: показываем заново, поле пустое — и говорим почему.
        setChanged(failure.note);
        setOffer(failure.offer);
        setInput('');
        setSettled(false);
      } else if (failure.kind === 'final') {
        setRefused(failure.text);
      } else {
        setFailed(failure.text);
      }
    }
  }

  return (
    <Dialog.Root open onOpenChange={o => { if (!o && !pending) onClose(); }}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-black/40" style={{ backdropFilter: 'blur(2px)' }} />
        {/* На узком экране — у верха: по центру клавиатура закрыла бы и поле, и кнопки. */}
        <Dialog.Content
          // Идёт удаление, показан его исход или набрано подтверждение — окно держит человека: поверх
          // него обвязка не открывает палитру и не уводит со страницы (см. `DialogHold`).
          data-modal-state={pending ? 'busy' : refused ?? failed ?? changed ? 'result' : input ? 'touched' : 'free'}
          className="fixed left-1/2 top-4 sm:top-1/2 -translate-x-1/2 sm:-translate-y-1/2 z-50 rounded-[28px] p-6 w-[calc(100%-2rem)] max-w-md max-h-[calc(100dvh-2rem)] overflow-y-auto bg-surface border border-outline-variant focus:outline-none"
          style={{ boxShadow: 'var(--md-sys-elevation-level3)' }}>
          <Dialog.Title className="text-sm font-semibold mb-2 text-on-surface">
            {refused ? 'Удаление невозможно' : subject.title}
          </Dialog.Title>

          {refused ? (
            <>
              <div className="mt-2 flex items-start gap-2.5 rounded-md bg-error-container px-3 py-2.5 text-xs text-on-surface" role="alert">
                <AlertTriangle size={16} className="shrink-0 mt-0.5 text-error" aria-hidden />
                <div className="max-h-40 overflow-y-auto whitespace-pre-line min-w-0">{refused}</div>
              </div>
              <div className="flex justify-end mt-4">
                <Button variant="tonal" size="sm" onClick={onClose}>Понятно</Button>
              </div>
            </>
          ) : (
            <form onSubmit={submit} noValidate>
              {subject.above?.(offer)}
              <Dialog.Description className="text-xs text-on-surface-variant">{subject.intro}</Dialog.Description>

              <DataGrid density="compact" className="mt-2 max-h-56" columns={HOLDER_COLUMNS} rows={offer.holders}
                footer={c => c.key === 'rows' ? offer.references : 'Всего ссылок'} />

              <ul className="mt-3 text-xs text-on-surface-variant list-disc pl-4 space-y-1">
                {subject.lines(offer).map(line => <li key={line}>{line}</li>)}
              </ul>

              {changed && (
                <div className="mt-3 rounded-md bg-warning-container px-3 py-2 text-xs text-on-surface" role="alert">{changed}</div>
              )}
              {failed && (
                <div className="mt-3 rounded-md bg-error-container px-3 py-2 text-xs text-on-surface" role="alert">{failed}</div>
              )}

              <div className="mt-4">
                <label htmlFor={fieldId} className="block text-xs font-medium text-on-surface-variant mb-1">Число теряемых ссылок</label>
                {/* type=text, а не number: без стрелок и прокрутки колесом; на телефоне цифровую
                    клавиатуру даёт inputMode. Шрифт 16px — иначе iOS увеличит страницу при фокусе. */}
                <input id={fieldId} type="text" inputMode="numeric" autoComplete="off" autoFocus
                  value={input} disabled={pending}
                  onChange={e => { setInput(e.target.value); setSettled(false); }}
                  onBlur={() => setSettled(true)}
                  // Кнопка при несовпадении выключена, и Enter форму не отправит: без этого нажатие
                  // не делало бы ничего и не объясняло почему (ревью PR #1246).
                  onKeyDown={e => { if (e.key === 'Enter') setSettled(true); }}
                  aria-invalid={mismatch || undefined} aria-describedby={mismatch ? errorId : hintId}
                  className={`w-full h-10 px-3 rounded-md bg-surface border text-base sm:text-sm text-on-surface tabular-nums outline-none focus-visible:ring-2 ${
                    mismatch ? 'border-error focus-visible:ring-error' : 'border-outline focus-visible:ring-primary'}`} />
                {mismatch
                  ? <p id={errorId} role="alert" className="mt-1 text-xs text-error">Не совпадает с числом в строке «Всего ссылок».</p>
                  : <p id={hintId} className="mt-1 text-xs text-fg3">Введите число из строки «Всего ссылок».</p>}
              </div>

              <div className="flex gap-2 justify-end items-start mt-4">
                <Button type="button" variant="text" size="sm" className="shrink-0" disabled={pending} data-modal-exit="" onClick={onClose}>Отмена</Button>
                <Button type="submit" variant="filled" danger size="sm" multiline className="min-w-0"
                  disabled={!matches} loading={pending}>
                  {subject.submit}
                </Button>
              </div>
            </form>
          )}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
