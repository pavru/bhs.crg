import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { FileCheck2, Eye, EyeOff } from 'lucide-react';
import { useAuth } from '@/shared/hooks/useAuth';
import { useAppVersion } from '@/shared/api/version';
import { Button, IconButton } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';

export function LoginPage() {
  const { login } = useAuth();
  const { data: version } = useAppVersion();
  const navigate = useNavigate();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    setLoading(true);
    try {
      await login(email, password);
      navigate('/document-sets', { replace: true });
    } catch {
      setError('Неверный email или пароль');
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="min-h-screen flex items-center justify-center bg-base">
      <div
        className="w-full max-w-sm rounded-lg p-8 bg-surface border border-stroke"
        style={{ boxShadow: 'var(--f-shadow16)' }}
      >
        <div className="flex items-center gap-3 mb-1">
          <div className="flex items-center justify-center w-11 h-11 rounded-lg bg-brand text-white shrink-0"
            style={{ boxShadow: 'var(--f-shadow4)' }}>
            <FileCheck2 size={24} />
          </div>
          <h1 className="text-2xl font-semibold text-brand leading-none">
            BHS.CRG
          </h1>
        </div>
        <p className="text-sm mb-6 text-fg3">
          Исполнительная документация
        </p>
        <form onSubmit={handleSubmit} className="space-y-4">
          <TextField label="Email" type="email" autoComplete="email" required autoFocus
            value={email} onChange={e => setEmail(e.target.value)} />
          <TextField label="Пароль" type={showPassword ? 'text' : 'password'} autoComplete="current-password" required
            value={password} onChange={e => setPassword(e.target.value)}
            trailing={
              <IconButton label={showPassword ? 'Скрыть пароль' : 'Показать пароль'} size="sm"
                onClick={() => setShowPassword(v => !v)}>
                {showPassword ? <EyeOff size={16} /> : <Eye size={16} />}
              </IconButton>
            } />
          {error && <p className="text-sm text-danger">{error}</p>}
          <Button type="submit" variant="filled" size="lg" fullWidth loading={loading} className="mt-2">
            {loading ? 'Вход…' : 'Войти'}
          </Button>
        </form>
        {version && (
          <p className="mt-6 text-center text-[11px] text-fg4"
            title={version.buildDate ? new Date(version.buildDate).toLocaleString('ru-RU') : undefined}>
            v{version.version}{version.commit ? ` · ${version.commit}` : ''}
          </p>
        )}
      </div>
    </div>
  );
}
