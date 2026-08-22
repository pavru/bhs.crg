import { useState, type ReactNode } from 'react';
import axios from 'axios';
import { useNavigate, Link } from 'react-router';
import { FileCheck2, Eye, EyeOff, ShieldAlert } from 'lucide-react';
import { useAuth } from '@/shared/hooks/useAuth';
import { useAppVersion } from '@/shared/api/version';
import { useRegistrationOpen, useRegisterFirstAdmin } from '@/shared/api/auth';
import { PASSWORD_MIN_LENGTH, PASSWORD_HINT } from '@/shared/auth/passwordPolicy';
import { registerErrorText } from '@/shared/auth/identityErrors';
import { Button, IconButton } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';

/** Фоновая сетка бренд-панели (по макету 2a) — тонкие белые линии 34×34. */
const GRID_BG: React.CSSProperties = {
  backgroundImage:
    'linear-gradient(rgba(255,255,255,.06) 1px,transparent 1px),' +
    'linear-gradient(90deg,rgba(255,255,255,.06) 1px,transparent 1px)',
  backgroundSize: '34px 34px',
};

/**
 * Вход — и, на свежей установке, регистрация первого администратора (issue #826).
 *
 * Второе здесь, а не отдельным адресом, по одной причине: адреса никто не знает. Установивший
 * систему открывает её корень и должен увидеть то, что ему сейчас доступно, — а доступен ему
 * ровно один шаг. До issue #826 он видел форму входа, войти которой было некем: пользователей
 * ноль, «Забыли пароль?» без учётной записи и без SMTP не помогает. Сервер при этом был готов
 * (`/api/auth/register` открыт, пока пользователей нет), а экрана к нему не написали никогда.
 */
export function LoginPage() {
  const { data: version } = useAppVersion();
  const { data: registrationOpen, isPending } = useRegistrationOpen();

  const versionLabel = version
    ? `v${version.version}${version.commit ? ` · ${version.commit}` : ''}`
    : '';

  return (
    <div className="min-h-screen flex items-center justify-center bg-base p-4">
      {/* Высота МИНИМАЛЬНАЯ, а не фиксированная: форма регистрации первого администратора выше
          формы входа на два поля и предупреждение, и при `h-[600px]` с `overflow-hidden` ей
          срезало заголовок сверху и кнопку снизу — экран, которым система встречает
          установившего, оказывался нерабочим (issue #826). */}
      <div
        className="w-full max-w-md md:max-w-[900px] md:min-h-[600px] flex overflow-hidden rounded-[28px] bg-surface border border-stroke"
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
            {versionLabel || ' '}
          </div>
        </div>

        {/* ── Форма (справа) ────────────────────────────────────────────────── */}
        <div className="flex-1 flex flex-col justify-center p-8 md:px-12 md:py-14">
          {/* Компактный бренд для узких экранов (панель скрыта) */}
          <div className="flex md:hidden items-center gap-3 mb-6">
            <span className="flex items-center justify-center w-11 h-11 rounded-lg bg-brand text-white shrink-0"
              style={{ boxShadow: 'var(--f-shadow4)' }}>
              <FileCheck2 size={22} />
            </span>
            <span className="text-2xl font-semibold text-brand leading-none">BHS.CRG</span>
          </div>

          {/* Пока не знаем, есть ли в системе пользователи, не рисуем НИЧЕГО. Показать вход и
              через мгновение подменить его регистрацией — значит первым делом мигнуть в лицо
              администратору формой, которой он воспользоваться не может. */}
          {isPending ? <div className="min-h-[320px]" aria-busy="true" />
            : registrationOpen ? <FirstAdminForm /> : <SignInForm />}

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

/** Заголовок формы — общий для обеих веток, чтобы они отличались только содержанием. */
function Head({ title, subtitle }: { title: string; subtitle: ReactNode }) {
  return (
    <>
      <h1 className="text-2xl font-normal text-fg1">{title}</h1>
      <p className="mt-1.5 mb-8 text-sm text-fg3">{subtitle}</p>
    </>
  );
}

function SignInForm() {
  const { login } = useAuth();
  const navigate = useNavigate();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [remember, setRemember] = useState(true);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    setLoading(true);
    try {
      await login(email, password, remember);
      navigate('/document-sets', { replace: true });
    } catch (e) {
      // Единственный ответ, кроме отказа, — 423: сервер отдаёт его ТОЛЬКО тому, кто ввёл верный
      // пароль к заблокированной учётной записи, поэтому показать причину здесь безопасно.
      // 429 отделяем обязательно: назвать «неверным паролем» упёршегося в лимит — прямая ложь,
      // человек будет менять пароль вместо того, чтобы подождать.
      const status = axios.isAxiosError(e) ? e.response?.status : undefined;
      const serverError = axios.isAxiosError<{ error?: string }>(e) ? e.response?.data?.error : undefined;
      setError(
        status === 423 ? serverError ?? 'Учётная запись временно заблокирована, попробуйте позже'
        : status === 429 ? 'Слишком много попыток входа. Попробуйте через несколько минут'
        : 'Неверный email или пароль');
    } finally {
      setLoading(false);
    }
  }

  return (
    <>
      <Head title="Вход в систему" subtitle="Введите данные учётной записи" />
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
          <Link to="/forgot-password"
            className="text-sm font-medium text-brand px-3 py-2 rounded-full hover:bg-brand/10 transition-colors">
            Забыли пароль?
          </Link>
        </div>

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
    </>
  );
}

/**
 * Регистрация первого администратора: видна, только пока в системе нет ни одного пользователя.
 *
 * Подтверждение пароля здесь не формальность, как на прочих экранах: восстановить его будет
 * нечем. Опечатался — учётная запись создана, войти нельзя, регистрация уже закрыта, а почта на
 * свежей установке не настроена, и «Забыли пароль?» письма никуда не отправит. Цена одного
 * лишнего поля — против правки базы руками.
 */
function FirstAdminForm() {
  const { login } = useAuth();
  const navigate = useNavigate();
  const register = useRegisterFirstAdmin();
  const [email, setEmail] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    if (password !== confirm) { setError('Пароль и подтверждение не совпадают'); return; }
    setBusy(true);
    try {
      await register.mutateAsync({ email, password, displayName });
      // Сразу входим: заставлять человека вводить те же данные второй раз — на ровном месте
      // повод усомниться, создалась ли учётная запись вообще.
      await login(email, password, true);
      navigate('/document-sets', { replace: true });
    } catch (err) {
      setError(registerErrorText(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <Head title="Первый вход"
        subtitle="Система установлена, пользователей ещё нет. Заведите администратора — он получит полные права." />

      <p className="flex gap-2 mb-6 text-xs leading-relaxed text-fg3 bg-warning/10 border border-warning/30 rounded-xl p-3">
        <ShieldAlert size={16} className="shrink-0 mt-0.5 text-warning" />
        <span>
          Пока администратора нет, эта форма открыта любому, кто дотянется до адреса системы.
          Заведите учётную запись сразу — после этого регистрация закроется сама.
        </span>
      </p>

      <form onSubmit={handleSubmit} className="space-y-6">
        <TextField label="Email" type="email" autoComplete="email" required autoFocus
          value={email} onChange={e => setEmail(e.target.value)} />
        <TextField label="Имя" autoComplete="name" required hint="Как показывать вас в системе"
          value={displayName} onChange={e => setDisplayName(e.target.value)} />
        <TextField label="Пароль" type={showPassword ? 'text' : 'password'} autoComplete="new-password" required
          minLength={PASSWORD_MIN_LENGTH} hint={PASSWORD_HINT}
          value={password} onChange={e => setPassword(e.target.value)}
          trailing={
            <IconButton label={showPassword ? 'Скрыть пароль' : 'Показать пароль'} size="sm"
              onClick={() => setShowPassword(v => !v)}>
              {showPassword ? <EyeOff size={16} /> : <Eye size={16} />}
            </IconButton>
          } />
        <TextField label="Подтверждение пароля" type={showPassword ? 'text' : 'password'}
          autoComplete="new-password" required minLength={PASSWORD_MIN_LENGTH}
          value={confirm} onChange={e => setConfirm(e.target.value)} />

        {error && <p className="text-sm text-danger">{error}</p>}
        <Button type="submit" variant="filled" size="lg" fullWidth loading={busy} className="mt-1">
          {busy ? 'Создаём…' : 'Создать администратора и войти'}
        </Button>
      </form>
    </>
  );
}
