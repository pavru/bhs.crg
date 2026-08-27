import { initialsOf } from './avatarHelpers';

/**
 * Аватар пользователя (issue #245): картинка src (data-URI), иначе инициалы на тональном фоне.
 * Размер задаётся через className (классы w-…, h-…, text-…), по умолчанию 40px.
 */
export function Avatar({ src, name, email, className = 'w-10 h-10 text-[15px]', alt = '' }: {
  src?: string | null;
  name?: string | null;
  email?: string | null;
  className?: string;
  alt?: string;
}) {
  const base = `inline-flex items-center justify-center rounded-full shrink-0 overflow-hidden ${className}`;
  if (src) {
    return <span className={base}><img src={src} alt={alt} className="w-full h-full object-cover" /></span>;
  }
  return (
    <span className={`${base} bg-brand-subtle text-on-brand-subtle font-medium`}>
      {initialsOf(name, email)}
    </span>
  );
}
