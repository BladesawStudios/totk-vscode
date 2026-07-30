import * as vscode from 'vscode';
import type { TkvscConfig } from './tkvscConfig';
import { DEFAULT_PROJECT_GAME_ID } from './tkvscConfig';

export class TkvscEditorProvider implements vscode.CustomTextEditorProvider {
    public static readonly viewType = 'totk-editor.tkvscEditor';

    public static register(context: vscode.ExtensionContext): vscode.Disposable {
        return vscode.window.registerCustomEditorProvider(
            TkvscEditorProvider.viewType,
            new TkvscEditorProvider(context),
            { supportsMultipleEditorsPerDocument: false },
        );
    }

    constructor(_context: vscode.ExtensionContext) {}

    public async resolveCustomTextEditor(
        document: vscode.TextDocument,
        webviewPanel: vscode.WebviewPanel,
    ): Promise<void> {
        webviewPanel.webview.options = { enableScripts: true };

        const update = () => {
            try {
                const text = document.getText().trim();
                const data = text ? (JSON.parse(text) as TkvscConfig) : {};
                webviewPanel.webview.html = buildHtml(data);
            } catch {
                webviewPanel.webview.html = buildErrorHtml(document.getText());
            }
        };

        const changeDocSub = vscode.workspace.onDidChangeTextDocument((e) => {
            if (e.document.uri.toString() === document.uri.toString()) {
                update();
            }
        });
        webviewPanel.onDidDispose(() => changeDocSub.dispose());

        webviewPanel.webview.onDidReceiveMessage(async (msg: {
            type: string;
            settingName?: string;
            value?: string;
        }) => {
            if (msg.type === 'add' && msg.settingName && msg.value) {
                const text = document.getText().trim();
                const data = text ? (JSON.parse(text) as TkvscConfig) : {};
                const key = msg.settingName as 'canonicalSyncBlacklistPrefixes' | 'canonicalSyncFileExtensionBlacklist';
                if (!data[key]) {
                    data[key] = [];
                }
                if (!data[key]!.includes(msg.value)) {
                    data[key]!.push(msg.value);
                }
                await writeBack(document, data);
            }
            if (msg.type === 'delete' && msg.settingName && msg.value) {
                const text = document.getText().trim();
                const data = text ? (JSON.parse(text) as TkvscConfig) : {};
                const key = msg.settingName as 'canonicalSyncBlacklistPrefixes' | 'canonicalSyncFileExtensionBlacklist';
                if (data[key]) {
                    data[key] = data[key]!.filter(v => v !== msg.value);
                }
                await writeBack(document, data);
            }
            if (msg.type === 'setGameId' && typeof msg.value === 'string') {
                const text = document.getText().trim();
                const data = text ? (JSON.parse(text) as TkvscConfig) : {};
                const next = msg.value.trim() || DEFAULT_PROJECT_GAME_ID;
                data.gameId = next;
                await writeBack(document, data);
                await document.save();
                void vscode.commands.executeCommand('totk-editor.refreshArchives');
            }
        });

        update();
    }
}

async function writeBack(document: vscode.TextDocument, data: TkvscConfig): Promise<void> {
    const edit = new vscode.WorkspaceEdit();
    edit.replace(
        document.uri,
        new vscode.Range(0, 0, document.lineCount, 0),
        JSON.stringify(data, null, 2),
    );
    await vscode.workspace.applyEdit(edit);
}

function buildErrorHtml(raw: string): string {
    return `<!DOCTYPE html><html><body style="color:#ccc;background:#1e1e1e;padding:20px;">
<h2>Invalid .tkvsc file</h2>
<pre style="white-space:pre-wrap;color:#e88;">${escHtml(raw.slice(0, 2000))}</pre>
</body></html>`;
}

function escHtml(s: string): string {
    return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

function buildHtml(data: TkvscConfig): string {
    const gameId = data.gameId?.trim() || DEFAULT_PROJECT_GAME_ID;
    const prefixes = data.canonicalSyncBlacklistPrefixes ?? [];
    const suffixes = data.canonicalSyncFileExtensionBlacklist ?? [];

    const prefixRows = prefixes.length > 0
        ? prefixes.map((val) => `
            <div class="rule-item">
                <span class="rule-value">${escHtml(val)}</span>
                <button class="btn-delete" title="Delete Rule" onclick="deleteRule('canonicalSyncBlacklistPrefixes', '${escHtml(val)}')">✕</button>
            </div>`).join('')
        : '<div class="empty-state">No folder prefix exclusions configured.</div>';

    const suffixRows = suffixes.length > 0
        ? suffixes.map((val) => `
            <div class="rule-item">
                <span class="rule-value">${escHtml(val)}</span>
                <button class="btn-delete" title="Delete Rule" onclick="deleteRule('canonicalSyncFileExtensionBlacklist', '${escHtml(val)}')">✕</button>
            </div>`).join('')
        : '<div class="empty-state">No file suffix exclusions configured.</div>';

    return /* html */ `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<style>
    * { box-sizing: border-box; margin: 0; padding: 0; }
    body {
        font-family: var(--vscode-font-family, 'Segoe UI', sans-serif);
        font-size: 13px;
        color: var(--vscode-foreground, #ccc);
        background: var(--vscode-editor-background, #1e1e1e);
        padding: 24px 32px;
        max-width: 800px;
        margin: 0 auto;
    }
    h1 {
        font-size: 22px;
        font-weight: 600;
        margin-bottom: 8px;
        color: var(--vscode-foreground, #eee);
        display: flex;
        align-items: center;
        gap: 10px;
    }
    .subtitle {
        font-size: 12px;
        color: var(--vscode-descriptionForeground, #999);
        margin-bottom: 24px;
    }
    .section {
        background: var(--vscode-editor-background, #1e1e1e);
        border: 1px solid var(--vscode-panel-border, #333);
        border-radius: 8px;
        margin-bottom: 24px;
        box-shadow: 0 4px 6px rgba(0, 0, 0, 0.15);
        overflow: hidden;
    }
    .section-header {
        background: var(--vscode-sideBarSectionHeader-background, #252526);
        padding: 12px 16px;
        font-size: 13px;
        font-weight: 600;
        text-transform: uppercase;
        letter-spacing: 0.5px;
        color: var(--vscode-sideBarSectionHeader-foreground, #bbb);
        border-bottom: 1px solid var(--vscode-panel-border, #333);
    }
    .section-body { padding: 16px; }
    .section-hint {
        font-size: 12px;
        color: var(--vscode-descriptionForeground, #999);
        margin-bottom: 12px;
    }
    .rules-list {
        display: flex;
        flex-direction: column;
        gap: 8px;
        margin-bottom: 16px;
    }
    .rule-item {
        display: flex;
        align-items: center;
        justify-content: space-between;
        background: var(--vscode-input-background, #2d2d2d);
        border: 1px solid var(--vscode-input-border, #444);
        border-radius: 4px;
        padding: 8px 12px;
        transition: all 0.2s ease;
    }
    .rule-item:hover {
        border-color: var(--vscode-focusBorder, #007fd4);
        transform: translateX(2px);
    }
    .rule-value {
        font-family: var(--vscode-editor-font-family, monospace);
        font-size: 12px;
        word-break: break-all;
    }
    .btn-delete {
        background: transparent;
        color: var(--vscode-errorForeground, #f48771);
        border: none;
        cursor: pointer;
        padding: 4px 8px;
        border-radius: 3px;
        transition: background 0.2s;
        display: flex;
        align-items: center;
        justify-content: center;
        font-size: 14px;
        font-weight: bold;
    }
    .btn-delete:hover {
        background: rgba(244, 135, 113, 0.15);
    }
    .add-form {
        display: flex;
        gap: 8px;
    }
    .input-add {
        flex: 1;
        background: var(--vscode-input-background, #3c3c3c);
        color: var(--vscode-input-foreground, #ccc);
        border: 1px solid var(--vscode-input-border, #555);
        border-radius: 4px;
        padding: 6px 12px;
        font-family: inherit;
        font-size: 13px;
        outline: none;
        transition: border 0.2s;
    }
    .input-add:focus {
        border-color: var(--vscode-focusBorder, #007fd4);
    }
    .btn-add {
        background: var(--vscode-button-background, #0e639c);
        color: var(--vscode-button-foreground, #fff);
        border: none;
        padding: 6px 16px;
        border-radius: 4px;
        cursor: pointer;
        font-size: 13px;
        font-weight: 500;
        transition: background 0.2s;
    }
    .btn-add:hover {
        background: var(--vscode-button-hoverBackground, #1177bb);
    }
    .empty-state {
        color: var(--vscode-descriptionForeground, #777);
        font-style: italic;
        padding: 8px 4px;
        font-size: 12px;
    }
</style>
</head>
<body>
    <h1>.tkvsc Configuration Editor</h1>
    <div class="subtitle">Project metadata for TKVSC, including game association and canonical-save blacklist rules.</div>

    <div class="section">
        <div class="section-header">Game</div>
        <div class="section-body">
            <div class="section-hint">Profile ID this project belongs to (e.g. <code>totk</code>). Your Projects only shows this project when the matching game is active.</div>
            <div class="add-form">
                <input type="text" id="gameId-input" class="input-add" value="${escHtml(gameId)}" placeholder="totk" onkeydown="if(event.key === 'Enter') saveGameId()" />
                <button class="btn-add" onclick="saveGameId()">Save</button>
            </div>
        </div>
    </div>

    <div class="section">
        <div class="section-header">Folder Prefix Exclusions</div>
        <div class="section-body">
            <div class="rules-list">
                ${prefixRows}
            </div>
            <div class="add-form">
                <input type="text" id="prefix-input" class="input-add" placeholder="e.g. Pack/Actor" onkeydown="if(event.key === 'Enter') addRule('canonicalSyncBlacklistPrefixes', 'prefix-input')" />
                <button class="btn-add" onclick="addRule('canonicalSyncBlacklistPrefixes', 'prefix-input')">+ Add</button>
            </div>
        </div>
    </div>

    <div class="section">
        <div class="section-header">File Suffix/Extension Exclusions</div>
        <div class="section-body">
            <div class="rules-list">
                ${suffixRows}
            </div>
            <div class="add-form">
                <input type="text" id="suffix-input" class="input-add" placeholder="e.g. .bgyml, .game__actor__ActorInfo.bgyml" onkeydown="if(event.key === 'Enter') addRule('canonicalSyncFileExtensionBlacklist', 'suffix-input')" />
                <button class="btn-add" onclick="addRule('canonicalSyncFileExtensionBlacklist', 'suffix-input')">+ Add</button>
            </div>
        </div>
    </div>

    <script>
        const vscode = acquireVsCodeApi();

        function addRule(settingName, inputId) {
            const input = document.getElementById(inputId);
            const value = input.value.trim();
            if (value) {
                vscode.postMessage({ type: 'add', settingName, value });
                input.value = '';
            }
        }

        function deleteRule(settingName, value) {
            vscode.postMessage({ type: 'delete', settingName, value });
        }

        function saveGameId() {
            const input = document.getElementById('gameId-input');
            vscode.postMessage({ type: 'setGameId', value: input.value });
        }
    </script>
</body>
</html>`;
}
