import * as vscode from 'vscode';
import { getFormatRegistry } from './formatRegistry';

/** File types that the Python bridge can convert to/from editor text. */

export function isEditableFile(filePath: string): boolean {
    return getFormatRegistry().isEditable(filePath);
}

export function toTotkDiskUri(fileUri: vscode.Uri): vscode.Uri {
    return fileUri.with({ scheme: 'totk-disk' });
}
