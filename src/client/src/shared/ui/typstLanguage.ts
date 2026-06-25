import type * as Monaco from 'monaco-editor';

let registered = false;

export function registerTypstLanguage(monaco: typeof Monaco) {
  if (registered) return;
  registered = true;

  monaco.languages.register({ id: 'typst' });

  monaco.languages.setMonarchTokensProvider('typst', {
    keywords: [
      'let', 'import', 'include', 'export',
      'for', 'while', 'if', 'else',
      'set', 'show', 'context',
      'return', 'break', 'continue',
      'and', 'or', 'not', 'in',
      'none', 'auto', 'true', 'false',
    ],

    tokenizer: {
      root: [
        // Line comment
        [/\/\/.*$/, 'comment'],
        // Block comment
        [/\/\*/, { token: 'comment', next: '@blockComment' }],

        // # keyword: #let, #for, #import …
        [/#([a-zA-Z_][a-zA-Z0-9_]*)/, {
          cases: {
            '#$1@keywords': 'keyword.control',
            '@default': 'entity.name.function',
          },
        }],

        // Math mode  $...$
        [/\$/, { token: 'string.math', next: '@math' }],

        // String
        [/"/, { token: 'string.quote', next: '@string' }],

        // Numbers with optional Typst units
        [/\d+(\.\d+)?(pt|mm|cm|in|em|fr|%)?/, 'constant.numeric'],

        // Markup emphasis
        [/\*[^*]+\*/, 'markup.bold'],
        [/_[^_]+_/, 'markup.italic'],

        // Brackets
        [/[{}()\[\]]/, '@brackets'],

        // Identifiers
        [/[a-zA-Zа-яА-ЯёЁ_][a-zA-Zа-яА-ЯёЁ0-9_-]*/, 'identifier'],

        // Operators / punctuation
        [/[+\-*\/<>=!&|,.:;]/, 'operator'],
      ],

      blockComment: [
        [/[^/*]+/, 'comment'],
        [/\*\//, { token: 'comment', next: '@pop' }],
        [/[/*]/, 'comment'],
      ],

      math: [
        [/\$/, { token: 'string.math', next: '@pop' }],
        [/[^$]+/, 'string.math'],
      ],

      string: [
        [/[^"\\]+/, 'string'],
        [/\\./, 'string.escape'],
        [/"/, { token: 'string.quote', next: '@pop' }],
      ],
    },
  });

  monaco.languages.setLanguageConfiguration('typst', {
    comments: {
      lineComment: '//',
      blockComment: ['/*', '*/'],
    },
    brackets: [
      ['{', '}'],
      ['[', ']'],
      ['(', ')'],
    ],
    autoClosingPairs: [
      { open: '{', close: '}' },
      { open: '[', close: ']' },
      { open: '(', close: ')' },
      { open: '"', close: '"' },
      { open: '$', close: '$' },
    ],
    surroundingPairs: [
      { open: '{', close: '}' },
      { open: '[', close: ']' },
      { open: '(', close: ')' },
      { open: '"', close: '"' },
      { open: '*', close: '*' },
      { open: '_', close: '_' },
    ],
  });
}
