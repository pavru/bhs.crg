import type { CSSProperties } from 'react';
import { FileCheck2 } from 'lucide-react';
import { useBranding, logoUrl } from '@/shared/api/branding';

/**
 * Знак экземпляра: логотип компании, если он загружен, иначе общий значок продукта.
 *
 * Логотип вписывается в квадрат знака целиком (`object-contain`): логотипы приходят любых пропорций,
 * и обрезка по центру срезала бы половину надписи — а это ровно то, что заказчик сюда поставил.
 */
export function BrandLogo(
  { className = '', iconSize = 22, style }:
  { className?: string; iconSize?: number; style?: CSSProperties },
) {
  const { data } = useBranding();
  const src = logoUrl(data);

  if (src) {
    return (
      <span className={`flex items-center justify-center shrink-0 overflow-hidden ${className}`} style={style}>
        <img src={src} alt={data?.productName ?? ''} className="max-w-full max-h-full object-contain" />
      </span>
    );
  }

  return (
    <span className={`flex items-center justify-center shrink-0 ${className}`} style={style}>
      <FileCheck2 size={iconSize} />
    </span>
  );
}
