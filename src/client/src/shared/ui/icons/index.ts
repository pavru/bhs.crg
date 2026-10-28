// Набор значков ([UI-60]): вход для экранов — отсюда, рисует их `shared/ui/Icon`. Пакет Material
// Symbols напрямую не импортируют (храповик `lucide-import`): в бандл едут только значки манифеста.
export * from './icons.generated';
export { ICON_SIZES, type FilledIconDef, type IconDef, type IconSize } from './types';
