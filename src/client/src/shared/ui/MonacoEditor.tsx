import * as monaco from 'monaco-editor';
import editorWorker from 'monaco-editor/esm/vs/editor/editor.worker?worker';
import Editor, { loader } from '@monaco-editor/react';

/*
 * Редактор берётся из НАШЕГО пакета, а не с CDN.
 *
 * По умолчанию @monaco-editor/react подгружает сам Monaco с cdn.jsdelivr.net. Это сторонний
 * скрипт, исполняющийся в нашем окне с полными правами страницы (включая доступ к токену), без
 * контроля версии со стороны поставки и без проверки целостности; заодно это обязательный выход
 * в интернет с рабочих мест — во внутреннем контуре редактор шаблонов просто не открылся бы.
 * Отдав загрузчику установленный пакет, мы раздаём редактор со своего же адреса — и тогда
 * политика безопасности (CSP в deploy/nginx.conf) может запретить скрипты с чужих доменов.
 *
 * Файл подключается ТОЛЬКО динамическим импортом (см. CodeEditor.tsx): Monaco весит несколько
 * мегабайт, и попадать в основной бандл к тем, кто шаблоны не открывает, ему незачем.
 */

// Воркер — из пакета, отдельным файлом сборки (не blob, не CDN).
(self as unknown as { MonacoEnvironment: monaco.Environment }).MonacoEnvironment = {
  getWorker: () => new editorWorker(),
};

// AMD-загрузчик CDN выставлял глобальный window.monaco, и на него опирается подсветка ошибок
// библиотеки (UserLibPanel). При локальном пакете этого никто не делает — выставляем сами,
// иначе маркеры молча перестали бы появляться.
(window as unknown as { monaco: typeof monaco }).monaco = monaco;

loader.config({ monaco });

export default Editor;
