import { useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { AuthCard } from './AuthCard';
import { TextField } from '@/shared/ui/TextField';
import { Button } from '@/shared/ui/Button';
import { useResetPassword } from '@/shared/api/auth';
import { apiError } from '@/shared/utils/apiError';

/** Установка нового пароля по ссылке из письма (issue #148). email и token — из query. */
export function ResetPasswordPage() {
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const reset = useResetPassword();
  const email = params.get('email') ?? '';
  const token = params.get('token') ?? '';

  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState('');
  const [done, setDone] = useState(false);

  const linkValid = !!email && !!token;

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    if (password !== confirm) { setError('Пароль и подтверждение не совпадают'); return; }
    try {
      await reset.mutateAsync({ email, token, newPassword: password });
      setDone(true);
      setTimeout(() => navigate('/login', { replace: true }), 1500);
    } catch (err) {
      setError(apiError(err, 'Не удалось сбросить пароль. Возможно, ссылка устарела.'));
    }
  }

  if (!linkValid) {
    return (
      <AuthCard title="Ссылка недействительна">
        <div className="space-y-4">
          <p className="text-sm text-fg2">Ссылка для сброса пароля неполная или устарела. Запросите новую.</p>
          <Link to="/forgot-password" className="inline-block text-sm font-medium text-brand hover:underline">
            Запросить сброс заново
          </Link>
        </div>
      </AuthCard>
    );
  }

  if (done) {
    return (
      <AuthCard title="Пароль изменён">
        <p className="text-sm text-success">Готово. Перенаправляем ко входу…</p>
      </AuthCard>
    );
  }

  return (
    <AuthCard title="Новый пароль" subtitle={`Аккаунт: ${email}`}>
      <form onSubmit={submit} className="space-y-5">
        <TextField label="Новый пароль" type="password" autoComplete="new-password" required autoFocus minLength={6}
          value={password} onChange={e => setPassword(e.target.value)} />
        <TextField label="Подтверждение" type="password" autoComplete="new-password" required minLength={6}
          value={confirm} onChange={e => setConfirm(e.target.value)} />
        {error && <p className="text-sm text-danger">{error}</p>}
        <Button type="submit" variant="filled" size="lg" fullWidth loading={reset.isPending}>
          Сохранить пароль
        </Button>
        <Link to="/login" className="block text-center text-sm font-medium text-brand hover:underline">
          Вернуться ко входу
        </Link>
      </form>
    </AuthCard>
  );
}
