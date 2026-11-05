/** Отступ по глубине дерева — 16 на уровень (D8, `UserLibFileList`). Классом целиком: Tailwind не соберёт
 *  имя, склеенное из частей. */
const DEPTH_PAD = ['pl-4', 'pl-8', 'pl-12', 'pl-16', 'pl-20', 'pl-24'];
/** Отступ уровня дерева — тот же у строки и у заголовка папки рядом с ней (`UserLibFileList`). */
export const listDepthPad = (depth: number) => DEPTH_PAD[Math.min(Math.max(depth, 0), DEPTH_PAD.length - 1)];
