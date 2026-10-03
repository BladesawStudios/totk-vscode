import * as vscode from 'vscode';
import { tokenizeXlnkLine, XLNK_TOKEN_TYPES } from './xlnkTokenizer';

const XLNK_LANGUAGE_ID = 'totk-xlnk';

// VS Code's own cut-offs for tokenizing a buffer (textModel.ts).
const LARGE_FILE_SIZE_THRESHOLD = 20 * 1024 * 1024;
const LARGE_FILE_LINE_COUNT_THRESHOLD = 300 * 1000;

const legend = new vscode.SemanticTokensLegend([...XLNK_TOKEN_TYPES]);
const typeIndex = new Map<string, number>(XLNK_TOKEN_TYPES.map((type, index) => [type, index]));

/**
 * True when VS Code has switched TextMate highlighting off for this document.
 * Small files keep the grammar, so the semantic layer stays out of their way.
 */
function isTooLargeForTokenization(document: vscode.TextDocument): boolean {
    const optimize = vscode.workspace
        .getConfiguration('editor', document)
        .get<boolean>('largeFileOptimizations', true);
    if (!optimize) {
        return false;
    }
    if (document.lineCount > LARGE_FILE_LINE_COUNT_THRESHOLD) {
        return true;
    }
    const lastLine = document.lineAt(document.lineCount - 1);
    return document.offsetAt(lastLine.range.end) > LARGE_FILE_SIZE_THRESHOLD;
}

/**
 * Highlights only the lines VS Code asks for (the viewport), so the decoded
 * elink2/slink2 databases get colour without tokenizing millions of lines.
 * Token types map back onto the grammar's scopes through `semanticTokenScopes`
 * in package.json, so themes colour them the same as the TextMate output.
 */
class XlnkRangeSemanticTokensProvider implements vscode.DocumentRangeSemanticTokensProvider {
    provideDocumentRangeSemanticTokens(
        document: vscode.TextDocument,
        range: vscode.Range,
        cancel: vscode.CancellationToken,
    ): vscode.SemanticTokens | undefined {
        const builder = new vscode.SemanticTokensBuilder(legend);
        if (!isTooLargeForTokenization(document)) {
            return builder.build();
        }

        const lastLine = Math.min(range.end.line, document.lineCount - 1);
        for (let line = range.start.line; line <= lastLine; line++) {
            if (cancel.isCancellationRequested) {
                return undefined;
            }
            for (const token of tokenizeXlnkLine(document.lineAt(line).text)) {
                builder.push(line, token.start, token.length, typeIndex.get(token.type)!);
            }
        }
        return builder.build();
    }
}

export function registerXlnkSemanticTokens(context: vscode.ExtensionContext): void {
    context.subscriptions.push(
        vscode.languages.registerDocumentRangeSemanticTokensProvider(
            { language: XLNK_LANGUAGE_ID },
            new XlnkRangeSemanticTokensProvider(),
            legend,
        ),
    );
}
