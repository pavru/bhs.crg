import type { ReactNode } from 'react';
import { BrandLogo } from '@/shared/ui/Brand';
import { useProductName } from '@/shared/api/branding';

/** Общая центрированная карточка для вспомогательных экранов авторизации
 *  (сброс пароля, подтверждение почты) — в едином стиле со страницей входа. */
export function AuthCard({ title, subtitle, children }: {
  title: string; subtitle?: string; children: ReactNode;
}) {
  const productName = useProductName();

  return (
    <div className="min-h-screen flex items-center justify-center bg-surface-container-low p-4">
      <div className="w-full max-w-sm rounded-xl bg-surface border border-outline-variant p-8 shadow-elevation-2">
        <div className="flex items-center gap-3 mb-6">
          <BrandLogo className="w-11 h-11 rounded-md bg-primary text-on-primary shadow-elevation-1" iconSize={22} />
          <span className="text-headline-small font-semibold text-primary leading-none">{productName}</span>
        </div>
        <h1 className="text-title-large text-on-surface">{title}</h1>
        {subtitle && <p className="mt-1.5 mb-6 text-body-medium text-on-surface-variant">{subtitle}</p>}
        <div className={subtitle ? '' : 'mt-6'}>{children}</div>
      </div>
    </div>
  );
}
