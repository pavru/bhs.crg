/// <reference types="vite/client" />
declare module 'virtual:catalog-metadata' {
  const metadata: {
    commit: string; roles: string[]; sourceFiles: number;
    typography: [string, { size: string; 'line-height': string; tracking: string; weight: number }][];
    shape: [string, string][];
    inventory: import('./source-inventory').SourceFile[];
    state: Record<string, number | string>;
    motion: Record<string, unknown>;
    elevation: Record<string, string[] | string>;
  };
  export default metadata;
}
