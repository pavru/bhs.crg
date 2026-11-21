import ts from 'typescript';
import { readdirSync } from 'node:fs';
import { relative, resolve } from 'node:path';

export interface ApiProperty { name: string; type: string; description: string; required: boolean; options?: (string | number)[] }
export interface SourceExport { name: string; file: string; line: number; description: string; properties: ApiProperty[]; visual: boolean; propertyNames: string[] }
export interface SourceFile { file: string; exports: SourceExport[]; visual: boolean }
export function readSourceInventory(client: string): SourceFile[] {
  const dirs = ['ui', 'filter', 'siteFilter'].map(dir => resolve(client, 'src/shared', dir));
  const list = (dir: string): string[] => readdirSync(dir, { withFileTypes: true })
    .flatMap(item => item.isDirectory() ? list(resolve(dir, item.name)) : [resolve(dir, item.name)])
    .filter(file => /\.(ts|tsx)$/.test(file) && !/\.(test|spec)\./.test(file));
  const files = dirs.flatMap(list);
  const config = ts.readConfigFile(resolve(client, 'tsconfig.app.json'), ts.sys.readFile);
  const parsed = ts.parseJsonConfigFileContent(config.config, ts.sys, client);
  const program = ts.createProgram(files, { ...parsed.options, noEmit: true });
  const checker = program.getTypeChecker();
  const display = (symbol: ts.Symbol) => ts.displayPartsToString(symbol.getDocumentationComment(checker));
  return files.map(file => {
    const source = program.getSourceFile(file)!;
    const module = checker.getSymbolAtLocation(source);
    const exports: SourceExport[] = [];
    for (let symbol of module ? checker.getExportsOfModule(module) : []) {
      const exported = symbol.name;
      if (symbol.flags & ts.SymbolFlags.Alias) symbol = checker.getAliasedSymbol(symbol);
      if (!symbol.valueDeclaration || !(symbol.flags & ts.SymbolFlags.Value)) continue;
      const declaration = symbol.valueDeclaration;
      const type = checker.getTypeOfSymbolAtLocation(symbol, declaration);
      const call = type.getCallSignatures()[0] ?? type.getConstructSignatures()[0];
      const visual = (file.endsWith('.tsx') && !!call && /^[A-Z]|^default$/.test(exported)) || exported === 'Snackbar';
      const props = call?.parameters[0];
      const propType = props ? checker.getTypeOfSymbolAtLocation(props, declaration) : undefined;
      const properties = (propType ? checker.getPropertiesOfType(propType) : []).filter(property =>
        property.declarations?.some(d => !d.getSourceFile().fileName.includes('node_modules')),
      ).map(property => {
        const propertyType = checker.getTypeOfSymbolAtLocation(property, declaration);
        const choices = (propertyType.isUnion() ? propertyType.types : [propertyType]).filter(t => !(t.flags & ts.TypeFlags.Undefined));
        const options = choices.every(t => t.isStringLiteral() || t.isNumberLiteral()) ? choices.map(t => (t as ts.StringLiteralType | ts.NumberLiteralType).value) : undefined;
        return { name: property.name, type: checker.typeToString(propertyType, declaration, ts.TypeFormatFlags.NoTruncation), description: display(property), required: !(property.flags & ts.SymbolFlags.Optional), options };
      });
      const name = exported === 'default' ? file.replace(/.*[\\/]|\.tsx$/g, '') : exported;
      const owningSource = declaration.getSourceFile();
      exports.push({ name, file: relative(client, owningSource.fileName).replaceAll('\\', '/'), line: owningSource.getLineAndCharacterOfPosition(declaration.getStart()).line + 1, description: display(symbol), properties, propertyNames: propType ? checker.getPropertiesOfType(propType).map(p => p.name) : [], visual });
    }
    return { file: relative(client, file).replaceAll('\\', '/'), exports, visual: exports.some(e => e.visual) };
  });
}
