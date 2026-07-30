import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';

const AAMP_HASH_NAMES_FILE_NAME = 'tkvsc-aamp-hash-names.json';

let aampHashNamesPath: string | undefined;

export function getAampHashNamesPath(): string | undefined {
    return aampHashNamesPath;
}

/** Writes the `TKVSC.aampHashNames` setting to disk so the Python bridge can register custom AAMP hash names. */
export function writeAampHashNames(globalStorageFsPath: string): string {
    const target = path.join(globalStorageFsPath, AAMP_HASH_NAMES_FILE_NAME);
    aampHashNamesPath = target;
    const hashNames = vscode.workspace
        .getConfiguration('TKVSC')
        .get<Record<string, string>>('aampHashNames', {});
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, JSON.stringify(hashNames, null, 2), 'utf8');
    return target;
}
