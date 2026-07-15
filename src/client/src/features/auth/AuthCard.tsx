import type { ReactNode } from 'react';
import { FileCheck2 } from 'lucide-react';

/** Общая центрированная карточка для вспомогательных экранов авторизации
 *  (сброс пароля, подтверждение почты) — в едином стиле со страницей входа. */
export function AuthCard({ title, subtitle, children }: {
  title: string; subtitle?: string; children: ReactNode;
}) {
  return (
    <div className="min-h-screen flex items-center justify-center bg-base p-4">
      <div className="w-full max-w-sm rounded-[28px] bg-surface border border-stroke p-8"
        style={{ boxShadow: 'var(--f-shadow16)' }}>
        <div className="flex items-center gap-3 mb-6">
          <span className="flex items-center justify-center w-11 h-11 rounded-lg bg-brand text-white shrink-0"
            style={{ boxShadow: 'var(--f-shadow4)' }}>
            <FileCheck2 size={22} />
          </span>
          <span className="text-2xl font-semibold text-brand leading-none">BHS.CRG</span>
        </div>
        <h1 className="text-xl font-normal text-fg1">{title}</h1>
        {subtitle && <p className="mt-1.5 mb-6 text-sm text-fg3">{subtitle}</p>}
        <div className={subtitle ? '' : 'mt-6'}>{children}</div>
      </div>
    </div>
  );
}
