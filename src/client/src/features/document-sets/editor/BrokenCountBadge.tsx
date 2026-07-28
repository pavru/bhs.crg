
// ── Счётчик битых ссылок ────────────────────────────────────────────────────

/** Свод-бейдж числа битых ссылок (issue #334) — на пункте раздела / контейнерном поле / вкладке. */

export function BrokenCountBadge({ count, className = '' }: { count: number; className?: string }) {
  if (count <= 0) return null;
  return (
    <span title={`Битых ссылок: ${count}`}
      className={`inline-flex items-center justify-center min-w-[16px] h-4 px-1 rounded-full bg-danger text-white text-[10px] font-semibold leading-none ${className}`}>
      {count}
    </span>
  );
}

// ─── Базовый экземпляр (issue #71) ────────────────────────────────────────────
// Документ дочернего типа может наследоваться от базы — документа комплекта ЛИБО записи общих данных.
// Кандидаты берутся по всей цепочке типов-предков и по скоп-близости (комплект > раздел > стройка >
// система), внутри уровня — по близости наследования. Ссылка хранится как _baseRef {kind,id}.
