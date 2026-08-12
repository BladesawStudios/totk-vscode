import * as vscode from 'vscode';

const XLNK_LANGUAGE_ID = 'totk-xlnk';

/**
 * VS Code stops tokenizing a buffer above 20 MB or 300k lines when
 * `editor.largeFileOptimizations` is on, which covers the whole decoded
 * elink2/slink2 databases. The extension ships a `[totk-xlnk]` default that
 * turns the limit off; `TKVSC.xlnkSyntaxHighlighting` lets the user put it back
 * by writing an explicit language override into their own settings.
 */
async function syncLargeFileOptimizations(): Promise<void> {
    const mode = vscode.workspace
        .getConfiguration('TKVSC')
        .get<string>('xlnkSyntaxHighlighting', 'always');

    const editorConfig = vscode.workspace.getConfiguration('editor', {
        languageId: XLNK_LANGUAGE_ID,
    });
    const inspected = editorConfig.inspect<boolean>('largeFileOptimizations');
    const current = inspected?.globalLanguageValue;

    // 'always' is what the shipped default already does, so clear the override
    // rather than writing a redundant copy of it into the user's settings.
    const desired = mode === 'smallFilesOnly' ? true : undefined;
    if (current === desired) {
        return;
    }

    try {
        await editorConfig.update(
            'largeFileOptimizations',
            desired,
            vscode.ConfigurationTarget.Global,
            true,
        );
    } catch (error) {
        console.error('TKVSC: failed to update XLNK highlighting setting', error);
    }
}

export function registerXlnkEditorSettings(context: vscode.ExtensionContext): void {
    void syncLargeFileOptimizations();

    context.subscriptions.push(
        vscode.workspace.onDidChangeConfiguration((event) => {
            if (event.affectsConfiguration('TKVSC.xlnkSyntaxHighlighting')) {
                void syncLargeFileOptimizations();
            }
        }),
    );
}
