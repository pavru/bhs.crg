import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { FileCheck2, Eye, EyeOff } from 'lucide-react';
import { useAuth } from '@/shared/hooks/useAuth';
import { useAppVersion } from '@/shared/api/version';
import { Button, IconButton } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';

/** Фоновая сетка бренд-панели (по макету 2a) — тонкие белые линии 34×34. */
const GRID_BG: React.CSSProperties = {
  backgroundImage:
    'linear-gradient(rgba(255,255,255,.06) 1px,transparent 1px),' +
    'linear-gradient(90deg,rgba(255,255,255,.06) 1px,transparent 1px)',
  backgroundSize: '34px 34px',
};

export function LoginPage() {
  const { login } = useAuth();
  const { data: version } = useAppVersion();
  const navigate = useNavigate();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [remember, setRemember] = useState(true);
  const [forgotOpen, setForgotOpen] = useState(false);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    setLoading(true);
    try {
      await login(email, password, remember);
      navigate('/document-sets', { replace: true });
    } catch {
      setError('Неверный email или пароль');
    } finally {
      setLoading(false);
    }
  }

  const versionLabel = version
    ? `v${version.version}${version.commit ? ` · ${version.commit}` : ''}`
    : '';

  return (
    <div className="min-h-screen flex items-center justify-center bg-base p-4">
      <div
        className="w-full max-w-md md:max-w-[900px] md:h-[600px] flex overflow-hidden rounded-[28px] bg-surface border border-stroke"
        style={{ boxShadow: 'var(--f-shadow16)' }}
      >
        {/* ── Бренд-панель (слева, только на широких экранах) ───────────────── */}
        <div
          className="hidden md:flex md:w-[42%] flex-col justify-between p-11 bg-brand text-white"
          style={GRID_BG}
        >
          <div className="flex items-center gap-3">
            <span className="flex items-center justify-center w-12 h-12 rounded-2xl bg-white/20">
              <FileCheck2 size={24} />
            </span>
            <span className="text-[22px] font-medium tracking-wide">BHS.CRG</span>
          </div>
          <div>
            <div className="text-[32px] font-normal leading-tight">Исполнительная документация</div>
            <p className="mt-4 text-[15px] leading-relaxed text-white/70 max-w-[300px]">
              Единая система ведения и согласования исполнительной документации по объекту строительства.
            </p>
          </div>
          <div className="text-xs tracking-wide text-white/55"
            title={version?.buildDate ? new Date(version.buildDate).toLocaleString('ru-RU') : undefined}>
            {versionLabel || ' '}
          </div>
        </div>

        {/* ── Форма входа (справа) ──────────────────────────────────────────── */}
        <div className="flex-1 flex flex-col justify-center p-8 md:px-12 md:py-14">
          {/* Компактный бренд для узких экранов (панель скрыта) */}
          <div className="flex md:hidden items-center gap-3 mb-6">
            <span className="flex items-center justify-center w-11 h-11 rounded-lg bg-brand text-white shrink-0"
              style={{ boxShadow: 'var(--f-shadow4)' }}>
              <FileCheck2 size={22} />
            </span>
            <span className="text-2xl font-semibold text-brand leading-none">BHS.CRG</span>
          </div>

          <h1 className="text-2xl font-normal text-fg1">Вход в систему</h1>
          <p className="mt-1.5 mb-8 text-sm text-fg3">Введите данные учётной записи</p>

          <form onSubmit={handleSubmit} className="space-y-6">
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

            <div className="flex justify-end -mt-2">
              <button type="button" onClick={() => setForgotOpen(o => !o)} aria-expanded={forgotOpen}
                className="text-sm font-medium text-brand px-3 py-2 rounded-full hover:bg-brand/10 transition-colors">
                Забыли пароль?
              </button>
            </div>
            {forgotOpen && (
              <p className="-mt-3 px-1 text-xs text-fg3">
                Самостоятельный сброс пока недоступен — пароль сбрасывает администратор системы.
              </p>
            )}

            <label className="flex items-center gap-3 text-sm text-fg1 cursor-pointer select-none">
              <input type="checkbox" checked={remember} onChange={e => setRemember(e.target.checked)}
                className="w-[18px] h-[18px] accent-brand cursor-pointer" />
              Запомнить меня
            </label>

            {error && <p className="text-sm text-danger">{error}</p>}
            <Button type="submit" variant="filled" size="lg" fullWidth loading={loading} className="mt-1">
              {loading ? 'Вход…' : 'Войти'}
            </Button>
          </form>

          {version && (
            <p className="md:hidden mt-6 text-center text-[11px] text-fg4"
              title={version.buildDate ? new Date(version.buildDate).toLocaleString('ru-RU') : undefined}>
              {versionLabel}
            </p>
          )}
        </div>
      </div>
    </div>
  );
}
