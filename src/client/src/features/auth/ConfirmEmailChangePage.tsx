import { useEffect, useRef, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { AuthCard } from './AuthCard';
import { useConfirmEmailChange } from '@/shared/api/auth';
import { apiError } from '@/shared/utils/apiError';
import { CheckCircle2, Loader2, AlertCircle } from 'lucide-react';

/** Подтверждение смены адреса входа по ссылке из письма на новый адрес (issue #148).
 *  uid/email(новый)/token — из query, POST один раз при монтировании. */
export function ConfirmEmailChangePage() {
  const [params] = useSearchParams();
  const confirm = useConfirmEmailChange();
  const userId = params.get('uid') ?? '';
  const newEmail = params.get('email') ?? '';
  const token = params.get('token') ?? '';
  const [status, setStatus] = useState<'pending' | 'ok' | 'error'>('pending');
  const [error, setError] = useState('');
  const ran = useRef(false);

  useEffect(() => {
    if (ran.current) return;
    ran.current = true;
    if (!userId || !newEmail || !token) { setStatus('error'); setError('Ссылка недействительна или устарела.'); return; }
    confirm.mutateAsync({ userId, newEmail, token })
      .then(() => setStatus('ok'))
      .catch(err => { setStatus('error'); setError(apiError(err, 'Не удалось сменить адрес. Возможно, ссылка устарела.')); });
  }, [userId, newEmail, token, confirm]);

  return (
    <AuthCard title="Смена адреса входа">
      {status === 'pending' && (
        <p className="flex items-center gap-2 text-sm text-fg3"><Loader2 size={16} className="animate-spin" /> Подтверждаем…</p>
      )}
      {status === 'ok' && (
        <div className="space-y-4">
          <p className="flex items-start gap-2 text-sm text-fg2">
            <CheckCircle2 size={18} className="shrink-0 mt-0.5 text-success" />
            Адрес входа изменён на <span className="font-medium">{newEmail}</span>. Войдите с новым email.
          </p>
          <Link to="/login" className="inline-block text-sm font-medium text-brand hover:underline">Перейти ко входу</Link>
        </div>
      )}
      {status === 'error' && (
        <div className="space-y-4">
          <p className="flex items-start gap-2 text-sm text-danger">
            <AlertCircle size={18} className="shrink-0 mt-0.5" /> {error}
          </p>
          <Link to="/login" className="inline-block text-sm font-medium text-brand hover:underline">Вернуться ко входу</Link>
        </div>
      )}
    </AuthCard>
  );
}
