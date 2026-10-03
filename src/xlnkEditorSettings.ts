import * as vscode from 'vscode';

const XLNK_LANGUAGE_ID = 'totk-xlnk';

/**
 * VS Code stops tokenizing a buffer above 20 MB or 300k lines when
 * `editor.largeFileOptimizations` is on, which covers the whole decoded
 * elink2/slink2 databases. By default that limit stays in place and
 * `xlnkSemanticTokens.ts` highlights just the visible lines of those files;
 * `TKVSC.xlnkSyntaxHighlighting: always` lifts the limit by writing an explicit
 * `[totk-xlnk]` override into the user's settings.
 */
async function syncLargeFileOptimizations(): Promise<void> {
    const mode = vscode.workspace
        .getConfiguration('TKVSC')
        .get<string>('xlnkSyntaxHighlighting', 'smallFilesOnly');

    const editorConfig = vscode.workspace.getConfiguration('editor', {
        languageId: XLNK_LANGUAGE_ID,
    });
    const inspected = editorConfig.inspect<boolean>('largeFileOptimizations');
    const current = inspected?.globalLanguageValue;

    // 'smallFilesOnly' is VS Code's own default, so clear the override rather
    // than writing a redundant copy of it into the user's settings.
    const desired = mode === 'always' ? false : undefined;
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
