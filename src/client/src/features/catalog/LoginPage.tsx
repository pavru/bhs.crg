import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { FileCheck2 } from 'lucide-react';
import { useAuth } from '@/shared/hooks/useAuth';
import { useAppVersion } from '@/shared/api/version';

export function LoginPage() {
  const { login } = useAuth();
  const { data: version } = useAppVersion();
  const navigate = useNavigate();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
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
          <div>
            <label className="block text-sm font-medium mb-1 text-fg2">
              Email
            </label>
            <input
              type="email"
              value={email}
              onChange={e => setEmail(e.target.value)}
              className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface"
              required
            />
          </div>
          <div>
            <label className="block text-sm font-medium mb-1 text-fg2">
              Пароль
            </label>
            <input
              type="password"
              value={password}
              onChange={e => setPassword(e.target.value)}
              className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface"
              required
            />
          </div>
          {error && <p className="text-sm text-danger">{error}</p>}
          <button
            type="submit"
            disabled={loading}
            className="w-full bg-brand hover:bg-brand-hover text-white font-medium py-2 px-4 rounded-md text-sm transition-colors disabled:opacity-50 mt-2"
          >
            {loading ? 'Вход...' : 'Войти'}
          </button>
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
