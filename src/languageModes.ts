import * as vscode from 'vscode';
import { getFormatRegistry } from './formatRegistry';

export function languageIdForPath(filePath: string): string | undefined {
    return getFormatRegistry().getLanguageId(filePath);
}

export function registerDocumentLanguageModes(context: vscode.ExtensionContext): void {
    const apply = (document: vscode.TextDocument) => {
        if (document.uri.scheme !== 'sarc' && document.uri.scheme !== 'totk-disk' && document.uri.scheme !== 'totk-dump') {
            return;
        }

        const languageId = languageIdForPath(document.uri.fsPath);
        if (languageId && document.languageId !== languageId) {
            void vscode.languages.setTextDocumentLanguage(document, languageId);
        }
    };

    context.subscriptions.push(
        vscode.workspace.onDidOpenTextDocument(apply),
        vscode.workspace.onDidChangeTextDocument((event) => apply(event.document)),
    );

    for (const document of vscode.workspace.textDocuments) {
        apply(document);
    }
}
