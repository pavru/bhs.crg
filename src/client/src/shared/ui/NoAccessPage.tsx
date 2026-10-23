import { Lock } from 'lucide-react';
import type { MissingAccess } from './navAccess';

/**
 * «Раздел недоступен» (ТЗ AUTH-15).
 *
 * Страница, а не перенаправление на главную. Молчаливое перенаправление запрещено требованием, и
 * причина в нём названа прямо: оно неотличимо от поломки. Человек нажал пункт меню, оказался на
 * списке строек и не знает, что произошло, — ссылка битая? система сломалась? не туда нажал? Он
 * идёт выяснять это к администратору, описывая не то, что случилось.
 *
 * Поэтому здесь три вещи: что недоступно, ЧЕГО именно не хватает и к кому идти. Код права
 * показывается как есть — его называет администратор в редакторе ролей, и совпадение строки
 * избавляет обоих от игры в угадайку. Раздел под «любым из» (plan-02) называет все права списка:
 * хватит любого, и выбрать, какое выдать, — дело администратора.
 */
export function NoAccessPage({ what, missing }: {
  what: string;
  missing: MissingAccess;
}) {
  return (
    <div className="flex h-full items-center justify-center p-8">
      <div className="max-w-md space-y-4 text-center">
        <Lock size={40} className="mx-auto text-fg4" aria-hidden />

        <h1 className="text-xl font-medium text-fg1">Раздел «{what}» недоступен</h1>

        {missing?.kind === 'module' && (
          <p className="text-sm text-fg3">
            Он относится к модулю «{missing.title}», а доступа к этому модулю у вас нет.
            Доступ к модулю даёт любое его право.
          </p>
        )}

        {missing?.kind === 'permission' && missing.codes.length === 1 && (
          <p className="text-sm text-fg3">
            Для него нужно право <Code>{missing.codes[0]}</Code>, и его у вас нет.
          </p>
        )}

        {missing?.kind === 'permission' && missing.codes.length > 1 && (
          <p className="text-sm text-fg3">
            Для него нужно одно из прав:{' '}
            {missing.codes.map((code, i) => (
              <span key={code}>{i > 0 && ', '}<Code>{code}</Code></span>
            ))}
            {' '}— ни одного из них у вас нет.
          </p>
        )}

        {/* Причина неизвестна — говорим и это. Придумать правдоподобную причину хуже, чем
            признаться: администратор пойдёт проверять не то. */}
        {!missing && (
          <p className="text-sm text-fg3">
            Права на него у вас нет. Если доступ нужен — назовите администратору адрес страницы.
          </p>
        )}

        <p className="text-sm text-fg4">
          Права выдаёт администратор системы — обратитесь к нему.
        </p>
      </div>
    </div>
  );
}

function Code({ children }: { children: string }) {
  return (
    <code className="rounded bg-muted px-1.5 py-0.5 font-mono text-xs text-fg2">{children}</code>
  );
}
