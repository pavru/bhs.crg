import { useState, useMemo } from 'react';
import { Icon } from './Icon';
import { Cancel, CheckCircle, Warning } from './icons';
import { Modal, ModalExit } from './Modal';
import { Button } from './Button';
import { Checkbox } from './Checkbox';
import { toggleInSet } from '@/shared/utils/toggleInSet';
import { useSendEmail } from '@/shared/api/users';
import { apiError } from '@/shared/utils/apiError';

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
    setSelected(prev => toggleInSet(prev, id));
    setResult(null);
  }

  async function handleSend() {
    setResult(null);
    try { setResult(await sendEmail.mutateAsync({ userIds: [...selected], subject: subject.trim(), body: body.trim() })); }
    catch (e) { setResult({ ok: false, error: apiError(e, 'Не удалось отправить.') }); }
  }

  const canSend = selected.size > 0 && subject.trim() && body.trim() && !sendEmail.isPending;
  const field = "w-full border border-outline rounded-sm px-3 py-2 text-body-medium focus:outline-none focus-visible:ring-2 focus-visible:ring-primary bg-surface";

  return (
    <Modal open={open} onOpenChange={o => { if (!o) { onClose(); setResult(null); } }} title={title}
      footer={
        <div className="flex justify-end gap-2">
          <ModalExit>Закрыть</ModalExit>
          <Button variant="filled" onClick={handleSend} disabled={!canSend} loading={sendEmail.isPending}>
            {sendEmail.isPending ? 'Отправка…' : `Отправить (${selected.size})`}
          </Button>
        </div>
      }>
      <div className="space-y-4">
        {/* Получатели */}
        <div>
          <label className="block text-body-small font-medium text-on-surface-variant mb-1">Получатели</label>
          {candidates.length === 0 ? (
            <p className="text-body-small text-on-surface-variant">Нет кандидатов.</p>
          ) : (
            <div className="rounded-sm border border-outline-variant divide-y divide-surface-container max-h-48 overflow-y-auto">
              {candidates.map(c => {
                const ok = validEmail(c.email);
                return (
                  // Адрес без почты выбрать нельзя: строка — отключённым видом MD3 ([UI-64]), знак — своим цветом.
                  <label key={c.id} className={`flex items-center gap-2 px-2.5 py-1.5 text-body-medium ${ok ? 'cursor-pointer state-layer' : ''}`}>
                    <Checkbox checked={selected.has(c.id)} disabled={!ok} onChange={() => toggle(c.id)} />
                    <span className={`${ok ? 'text-on-surface' : 'text-on-surface/38'} flex-1 min-w-0 truncate`}>{c.displayName || c.email}</span>
                    <span className={`text-body-small ${ok ? 'text-on-surface-variant' : 'text-on-surface/38'} min-w-0 truncate`}>{c.email || '—'}</span>
                    {!ok && <Icon icon={Warning} size={18} className="text-warning shrink-0" />}
                  </label>
                );
              })}
            </div>
          )}
        </div>

        <div>
          <label className="block text-body-small font-medium text-on-surface-variant mb-1">Тема</label>
          <input className={field} value={subject} onChange={e => { setSubject(e.target.value); setResult(null); }} />
        </div>
        <div>
          <label className="block text-body-small font-medium text-on-surface-variant mb-1">Текст</label>
          <textarea className={field + ' min-h-32 resize-y'} value={body} onChange={e => { setBody(e.target.value); setResult(null); }} />
        </div>

        {result && (result.ok
          ? <p className="text-body-medium text-success flex items-center gap-1.5">
              <Icon icon={CheckCircle} size={18} /> Отправлено получателям: {result.sent}
              {result.skipped && result.skipped.length > 0 && <span className="text-warning ml-1">(пропущены без email: {result.skipped.join(', ')})</span>}
            </p>
          : <p className="text-body-medium text-error flex items-start gap-1.5"><Icon icon={Cancel} size={18} className="shrink-0 mt-0.5" /> {result.error}</p>
        )}
      </div>
    </Modal>
  );
}
