import { useState, useMemo } from 'react';
import { Loader2, AlertTriangle, CheckCircle } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { useRecipients } from '@/shared/api/subscriptions';

const EMAIL_RE = /^[^@\s]+@[^@\s]+\.[^@\s]+$/;

/** Разбирает произвольные адреса (через запятую/точку с запятой/перенос строки). */
function parseAddresses(text: string): string[] {
  return text.split(/[,;\n]+/).map(s => s.trim()).filter(Boolean);
}

/**
 * Отправка сгенерированных PDF: подписчикам (с учётом наследования) и/или на произвольные адреса
 * контрагентов. Итоговые получатели = отмеченные подписчики + введённые внешние адреса. Универсально
 * для комплекта и отдельного документа (готовность и отправка задаются пропсами). Фоновая задача.
 */
export function EmailSendDialog({ open, onClose, setId, itemName, defaultSubjectHint, defaultBodyHint, ready, notReadyHint, onSend }: {
  open: boolean; onClose: () => void;
  setId: string;                       // комплект — для резолва подписчиков (документ передаёт свой documentSetId)
  itemName: string;
  defaultSubjectHint: string; defaultBodyHint: string;
  ready: boolean; notReadyHint: string;
  onSend: (to: string[], subject?: string, body?: string) => Promise<unknown>;
}) {
  const { data: recipients = [] } = useRecipients('Set', setId, open);
  const [unchecked, setUnchecked] = useState<Set<string>>(new Set()); // снятые подписчики (по умолчанию все отмечены)
  const [external, setExternal] = useState('');
  const [subject, setSubject] = useState('');
  const [body, setBody] = useState('');
  const [sending, setSending] = useState(false);
  const [queued, setQueued] = useState(false);
  const [error, setError] = useState('');

  const validSubs = recipients.filter(r => r.validEmail);
  const externalList = useMemo(() => parseAddresses(external), [external]);
  const externalValid = externalList.filter(a => EMAIL_RE.test(a));
  const externalBad = externalList.filter(a => !EMAIL_RE.test(a));

  // Итоговые адреса: отмеченные подписчики + валидные внешние (без дублей).
  const to = useMemo(() => {
    const subs = validSubs.filter(r => !unchecked.has(r.userId)).map(r => r.email!);
    return [...new Set([...subs, ...externalValid].map(e => e.toLowerCase()))];
  }, [validSubs, unchecked, externalValid]);

  function toggleSub(userId: string) {
    setUnchecked(prev => { const n = new Set(prev); n.has(userId) ? n.delete(userId) : n.add(userId); return n; });
  }

  async function handleSend() {
    setError('');
    if (externalBad.length > 0) { setError(`Неверные адреса: ${externalBad.join(', ')}`); return; }
    if (to.length === 0) { setError('Не выбран ни один получатель.'); return; }
    setSending(true);
    try { await onSend(to, subject.trim() || undefined, body.trim() || undefined); setQueued(true); }
    finally { setSending(false); }
  }

  const field = "w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface";

  return (
    <Modal open={open} onOpenChange={o => { if (!o) { onClose(); setQueued(false); setError(''); } }} title={`Отправить: ${itemName}`}
      footer={
        <div className="flex justify-end gap-3">
          <button type="button" onClick={onClose} className="px-4 py-2 text-sm text-fg2 hover:bg-muted rounded-md">Закрыть</button>
          <button type="button" onClick={handleSend} disabled={sending || queued || !ready || to.length === 0}
            className="flex items-center gap-2 px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md disabled:opacity-50">
            {sending ? <Loader2 size={14} className="animate-spin" /> : null}
            Отправить ({to.length})
          </button>
        </div>
      }>
      <div className="space-y-4">
        {!ready && (
          <p className="flex items-start gap-2 text-sm text-warning bg-warning-subtle rounded-md p-3">
            <AlertTriangle size={16} className="shrink-0 mt-0.5" /> {notReadyHint}
          </p>
        )}

        {recipients.length > 0 && (
          <div>
            <p className="text-xs font-medium text-fg2 mb-1">Подписчики (с учётом наследования)</p>
            <div className="rounded-md border border-stroke divide-y divide-muted max-h-40 overflow-y-auto">
              {recipients.map(r => (
                <label key={r.userId} className={`flex items-center gap-2 px-2.5 py-1.5 text-sm ${r.validEmail ? 'cursor-pointer hover:bg-base' : 'opacity-60'}`}>
                  <input type="checkbox" disabled={!r.validEmail} checked={r.validEmail && !unchecked.has(r.userId)} onChange={() => toggleSub(r.userId)} />
                  <span className="text-fg1 flex-1 min-w-0 truncate">{r.displayName}</span>
                  <span className="text-xs text-fg4 min-w-0 truncate">{r.email || '—'}</span>
                  {!r.validEmail && <AlertTriangle size={13} className="text-warning shrink-0" />}
                </label>
              ))}
            </div>
          </div>
        )}

        <div>
          <label className="block text-xs font-medium text-fg2 mb-1">Внешние адреса (контрагенты)</label>
          <textarea className={field + ' min-h-16 resize-y'} value={external} onChange={e => { setExternal(e.target.value); setError(''); }}
            placeholder="Через запятую: client@zakazchik.ru, tn@stroynadzor.ru" />
          {externalBad.length > 0 && <p className="text-[11px] text-danger mt-0.5">Неверные: {externalBad.join(', ')}</p>}
        </div>

        <div>
          <label className="block text-xs font-medium text-fg2 mb-1">Тема (необязательно)</label>
          <input className={field} value={subject} onChange={e => setSubject(e.target.value)} placeholder={defaultSubjectHint} />
        </div>
        <div>
          <label className="block text-xs font-medium text-fg2 mb-1">Текст (необязательно)</label>
          <textarea className={field + ' min-h-24 resize-y'} value={body} onChange={e => setBody(e.target.value)} placeholder={defaultBodyHint} />
        </div>

        {error && <p className="text-sm text-danger">{error}</p>}
        {queued && (
          <p className="text-sm text-success flex items-center gap-1.5">
            <CheckCircle size={15} /> Отправка запущена — прогресс в индикаторе задач слева от колокольчика.
          </p>
        )}
      </div>
    </Modal>
  );
}
