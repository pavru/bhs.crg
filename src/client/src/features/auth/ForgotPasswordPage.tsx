import { useState } from 'react';
import { Link } from 'react-router-dom';
import { AuthCard } from './AuthCard';
import { TextField } from '@/shared/ui/TextField';
import { Button } from '@/shared/ui/Button';
import { useForgotPassword } from '@/shared/api/auth';
import { MailCheck } from 'lucide-react';

/** Запрос ссылки для сброса пароля (issue #148). Ответ всегда «письмо отправлено»,
 *  существование адреса не раскрываем. */
export function ForgotPasswordPage() {
  const forgot = useForgotPassword();
  const [email, setEmail] = useState('');
  const [sent, setSent] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    try { await forgot.mutateAsync({ email: email.trim() }); } catch { /* enumeration-safe: игнорируем */ }
    setSent(true);
  }

  if (sent) {
    return (
      <AuthCard title="Проверьте почту">
        <div className="space-y-4">
          <p className="flex items-start gap-2 text-sm text-fg2">
            <MailCheck size={18} className="shrink-0 mt-0.5 text-success" />
            Если аккаунт с адресом <span className="font-medium">{email.trim()}</span> существует,
            мы отправили письмо со ссылкой для сброса пароля. Ссылка действует 1 час.
          </p>
          <Link to="/login" className="inline-block text-sm font-medium text-brand hover:underline">
            Вернуться ко входу
          </Link>
        </div>
      </AuthCard>
    );
  }

  return (
    <AuthCard title="Восстановление пароля" subtitle="Укажите email — пришлём ссылку для сброса.">
      <form onSubmit={submit} className="space-y-5">
        <TextField label="Email" type="email" autoComplete="email" required autoFocus
          value={email} onChange={e => setEmail(e.target.value)} />
        <Button type="submit" variant="filled" size="lg" fullWidth loading={forgot.isPending}>
          Отправить ссылку
        </Button>
        <Link to="/login" className="block text-center text-sm font-medium text-brand hover:underline">
          Вернуться ко входу
        </Link>
      </form>
    </AuthCard>
  );
}
