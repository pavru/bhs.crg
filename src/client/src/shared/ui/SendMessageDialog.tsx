import { useState, useMemo } from 'react';
import { CheckCircle, XCircle, AlertTriangle } from 'lucide-react';
import { Modal } from './Modal';
import { useSendEmail } from '@/shared/api/users';

export interface MessageCandidate { id: string; displayName: string; email: string | null; }

function validEmail(e: string | null): boolean {
  return !!e && /^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(e);
}

/**
 * Диалог составления и отправки письма выбранным пользователям (адреса в Bcc). Кандидаты передаются
 * снаружи — на UsersPage это все пользователи, на страницах стройки/раздела/комплекта (этапы 3-4) —
 * подписчики соответствующего scope. Пользователи без валидного email — выбрать нельзя.
 */
export function SendMessageDialog({ open, onClose, candidates, title = 'Отправить сообщение', presetSubject = '', presetBody = '' }: {
  open: boolean; onClose: () => void; candidates: MessageCandidate[];
  title?: string; presetSubject?: string; presetBody?: string;
}) {
  const sendEmail = useSendEmail();
  const sendable = useMemo(() => candidates.filter(c => validEmail(c.email)), [candidates]);
  const [selected, setSelected] = useState<Set<string>>(() => new Set(sendable.map(c => c.id)));
  const [subject, setSubject] = useState(presetSubject);
  const [body, setBody] = useState(presetBody);
  const [result, setResult] = useState<{ ok: boolean; sent?: number; skipped?: string[]; error?: string } | null>(null);

  function toggle(id: string) {
    setSelected(prev => { const n = new Set(prev); n.has(id) ? n.delete(id) : n.add(id); return n; });
    setResult(null);
  }

  async function handleSend() {
    setResult(null);
    setResult(await sendEmail.mutateAsync({ userIds: [...selected], subject: subject.trim(), body: body.trim() }));
  }

  const canSend = selected.size > 0 && subject.trim() && body.trim() && !sendEmail.isPending;
  const field = "w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface";

  return (
    <Modal open={open} onOpenChange={o => { if (!o) { onClose(); setResult(null); } }} title={title}
      footer={
        <div className="flex justify-end gap-3">
          <button type="button" onClick={onClose} className="px-4 py-2 text-sm text-fg2 hover:bg-muted rounded-md">Закрыть</button>
          <button type="button" onClick={handleSend} disabled={!canSend}
            className="px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md disabled:opacity-50">
            {sendEmail.isPending ? 'Отправка...' : `Отправить (${selected.size})`}
          </button>
        </div>
      }>
      <div className="space-y-4">
        {/* Получатели */}
        <div>
          <label className="block text-xs font-medium text-fg2 mb-1">Получатели</label>
          {candidates.length === 0 ? (
            <p className="text-xs text-fg4">Нет кандидатов.</p>
          ) : (
            <div className="rounded-md border border-stroke divide-y divide-muted max-h-48 overflow-y-auto">
              {candidates.map(c => {
                const ok = validEmail(c.email);
                return (
                  <label key={c.id} className={`flex items-center gap-2 px-2.5 py-1.5 text-sm ${ok ? 'cursor-pointer hover:bg-base' : 'opacity-60'}`}>
                    <input type="checkbox" checked={selected.has(c.id)} disabled={!ok} onChange={() => toggle(c.id)} />
                    <span className="text-fg1 flex-1 min-w-0 truncate">{c.displayName || c.email}</span>
                    <span className="text-xs text-fg4 min-w-0 truncate">{c.email || '—'}</span>
                    {!ok && <AlertTriangle size={13} className="text-warning shrink-0" />}
                  </label>
                );
              })}
            </div>
          )}
        </div>

        <div>
          <label className="block text-xs font-medium text-fg2 mb-1">Тема</label>
          <input className={field} value={subject} onChange={e => { setSubject(e.target.value); setResult(null); }} />
        </div>
        <div>
          <label className="block text-xs font-medium text-fg2 mb-1">Текст</label>
          <textarea className={field + ' min-h-32 resize-y'} value={body} onChange={e => { setBody(e.target.value); setResult(null); }} />
        </div>

        {result && (result.ok
          ? <p className="text-sm text-success flex items-center gap-1.5">
              <CheckCircle size={15} /> Отправлено получателям: {result.sent}
              {result.skipped && result.skipped.length > 0 && <span className="text-warning ml-1">(пропущены без email: {result.skipped.join(', ')})</span>}
            </p>
          : <p className="text-sm text-danger flex items-start gap-1.5"><XCircle size={15} className="shrink-0 mt-0.5" /> {result.error}</p>
        )}
      </div>
    </Modal>
  );
}
