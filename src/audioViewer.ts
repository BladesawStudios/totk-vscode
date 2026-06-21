import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';

const panels = new Map<string, vscode.WebviewPanel>();
let extensionUri: vscode.Uri | undefined;

export function initAudioViewer(extUri: vscode.Uri): void {
    extensionUri = extUri;
}

export function openAudioViewer(
    audioName: string,
    key: string,
    base64Wav: string,
): void {
    const existing = panels.get(key);
    
    if (existing) {
        existing.reveal();
        existing.webview.html = buildHtml(audioName, base64Wav, existing.webview);
        return;
    }

    const localRoots = extensionUri
        ? [vscode.Uri.joinPath(extensionUri, 'icons')]
        : [];

    const panel = vscode.window.createWebviewPanel(
        'totkAudioViewer',
        `Audio: ${audioName}`,
        vscode.ViewColumn.Active,
        { enableScripts: true, retainContextWhenHidden: false, localResourceRoots: localRoots },
    );

    panel.webview.html = buildHtml(audioName, base64Wav, panel.webview);
    panels.set(key, panel);
    panel.onDidDispose(() => {
        panels.delete(key);
    });
}

function buildHtml(audioName: string, base64Wav: string, webview: vscode.Webview): string {
    const audioSrc = `data:audio/wav;base64,${base64Wav}`;

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
        display: flex;
        flex-direction: column;
        align-items: center;
        justify-content: center;
        gap: 24px;
        padding: 20px;
        min-height: 100vh;
    }
    .audio-container {
        display: flex;
        flex-direction: column;
        align-items: center;
        background: var(--vscode-editorWidget-background, #252526);
        border: 1px solid var(--vscode-panel-border, #444);
        padding: 32px;
        border-radius: 8px;
        box-shadow: 0 4px 6px rgba(0,0,0,0.3);
    }
    .audio-title {
        font-size: 18px;
        font-weight: 600;
        margin-bottom: 24px;
        color: var(--vscode-foreground, #ddd);
        word-break: break-all;
    }
    audio {
        width: 300px;
        outline: none;
    }
    audio::-webkit-media-controls-panel {
        background-color: var(--vscode-editor-background, #1e1e1e);
    }
    audio::-webkit-media-controls-current-time-display,
    audio::-webkit-media-controls-time-remaining-display {
        color: var(--vscode-foreground, #ccc);
    }
</style>
</head>
<body>
    <div class="audio-container">
        <div class="audio-title">${escapeHtml(audioName)}</div>
        <audio controls autoplay src="${audioSrc}"></audio>
    </div>
    <script>
        const vscode = acquireVsCodeApi();
    </script>
</body>
</html>`;
}

function escapeHtml(value: string): string {
    return value
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}
