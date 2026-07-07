import { useState } from 'react';
import { Loader2, AlertTriangle, CheckCircle } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { useRecipients } from '@/shared/api/subscriptions';
import { useDocumentSetOutput, useEmailSetToSubscribers } from '@/shared/api/documentSets';

/**
 * Отправка собранного комплекта подписчикам (с учётом наследования). Требует собранного комплекта
 * (DocumentSetOutput). Файл вкладывается, если некрупный; иначе получателям придёт пометка скачать
 * в системе. Сама отправка — фоновая задача.
 */
export function EmailKitDialog({ open, onClose, setId, setName }: {
  open: boolean; onClose: () => void; setId: string; setName: string;
}) {
  const { data: recipients = [] } = useRecipients('Set', setId, open);
  const { data: output } = useDocumentSetOutput(setId);
  const send = useEmailSetToSubscribers();
  const [subject, setSubject] = useState('');
  const [body, setBody] = useState('');
  const [queued, setQueued] = useState(false);

  const valid = recipients.filter(r => r.validEmail);
  const noOutput = !output;

  async function handleSend() {
    await send.mutateAsync({ setId, subject: subject.trim() || undefined, body: body.trim() || undefined });
    setQueued(true);
  }

  const field = "w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface";

  return (
    <Modal open={open} onOpenChange={o => { if (!o) { onClose(); setQueued(false); } }} title="Отправить комплект подписчикам"
      footer={
        <div className="flex justify-end gap-3">
          <button type="button" onClick={onClose} className="px-4 py-2 text-sm text-fg2 hover:bg-muted rounded-md">Закрыть</button>
          <button type="button" onClick={handleSend} disabled={send.isPending || queued || noOutput || valid.length === 0}
            className="flex items-center gap-2 px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md disabled:opacity-50">
            {send.isPending ? <Loader2 size={14} className="animate-spin" /> : null}
            Отправить ({valid.length})
          </button>
        </div>
      }>
      <div className="space-y-4">
        {noOutput && (
          <p className="flex items-start gap-2 text-sm text-warning bg-warning-subtle rounded-md p-3">
            <AlertTriangle size={16} className="shrink-0 mt-0.5" />
            Комплект ещё не собран. Сначала соберите его («Собрать комплект»), затем отправляйте.
          </p>
        )}

        <div>
          <p className="text-xs font-medium text-fg2 mb-1">Получатели (подписчики с учётом наследования)</p>
          {recipients.length === 0 ? (
            <p className="text-xs text-fg4">Нет подписчиков. Добавьте их в панели «Подписчики» на уровне комплекта/раздела/стройки.</p>
          ) : (
            <div className="rounded-md border border-stroke divide-y divide-muted max-h-40 overflow-y-auto">
              {recipients.map(r => (
                <div key={r.userId} className="flex items-center gap-2 px-2.5 py-1.5 text-sm">
                  <span className="text-fg1 flex-1 min-w-0 truncate">{r.displayName}</span>
                  <span className="text-xs text-fg4 min-w-0 truncate">{r.email || '—'}</span>
                  {!r.validEmail && <AlertTriangle size={13} className="text-warning shrink-0" />}
                </div>
              ))}
            </div>
          )}
          {recipients.length > valid.length && (
            <p className="text-[11px] text-warning mt-1">Без валидного email ({recipients.length - valid.length}) — не получат.</p>
          )}
        </div>

        <div>
          <label className="block text-xs font-medium text-fg2 mb-1">Тема (необязательно)</label>
          <input className={field} value={subject} onChange={e => setSubject(e.target.value)}
            placeholder={`Исполнительная документация — ${setName}`} />
        </div>
        <div>
          <label className="block text-xs font-medium text-fg2 mb-1">Текст (необязательно)</label>
          <textarea className={field + ' min-h-24 resize-y'} value={body} onChange={e => setBody(e.target.value)}
            placeholder={`Направляем собранный комплект исполнительной документации «${setName}».`} />
        </div>

        {queued && (
          <p className="text-sm text-success flex items-center gap-1.5">
            <CheckCircle size={15} /> Отправка запущена — прогресс в индикаторе задач слева от колокольчика.
          </p>
        )}
      </div>
    </Modal>
  );
}
