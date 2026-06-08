import * as vscode from 'vscode';
import { BarsEntry, BarsAudioResult } from './bridge';

const panels = new Map<string, vscode.WebviewPanel>();
let extensionUri: vscode.Uri | undefined;

export function initBarsViewer(extUri: vscode.Uri): void {
    extensionUri = extUri;
}

export function openBarsViewer(
    barsName: string,
    key: string,
    entries: BarsEntry[],
    fetchAudio: (index: number) => Promise<BarsAudioResult>,
): void {
    const existing = panels.get(key);
    
    if (existing) {
        existing.reveal();
        return;
    }

    const os = require('os');
    const panel = vscode.window.createWebviewPanel(
        'totkBarsViewer',
        `BARS: ${barsName}`,
        vscode.ViewColumn.Active,
        { 
            enableScripts: true, 
            retainContextWhenHidden: true,
            localResourceRoots: [
                vscode.Uri.file(os.tmpdir()),
                ...(extensionUri ? [extensionUri] : []),
                ...(vscode.workspace.workspaceFolders ? vscode.workspace.workspaceFolders.map(f => f.uri) : [])
            ]
        },
    );

    panel.webview.html = buildHtml(barsName, entries);

    panel.webview.onDidReceiveMessage(async (message) => {
        if (message.type === 'play-entry') {
            try {
                const res = await fetchAudio(message.index);
                if (res.wavPath) {
                    const uri = panel.webview.asWebviewUri(vscode.Uri.file(res.wavPath));
                    panel.webview.postMessage({ type: 'play-audio', index: message.index, url: uri.toString() });
                } else {
                    void vscode.window.showErrorMessage('Failed to decode BARS audio entry: ' + (res.error || 'Unknown error'));
                    // Reset UI loading state
                    panel.webview.postMessage({ type: 'play-error', index: message.index });
                }
            } catch (e) {
                const msg = e instanceof Error ? e.message : String(e);
                void vscode.window.showErrorMessage(`Error fetching audio: ${msg}`);
                panel.webview.postMessage({ type: 'play-error', index: message.index });
            }
        }
    });

    panels.set(key, panel);
    panel.onDidDispose(() => {
        panels.delete(key);
    });
}

function buildHtml(barsName: string, entries: BarsEntry[]): string {
    const entriesHtml = entries.map((e, idx) => {
        const canPlay = e.has_prefetch || e.has_romfs_bwav;
        let metaHtml = '';
        if (e.metadata) {
            const m = e.metadata;
            const markers = m.markers && m.markers.length > 0
                ? `<div class="meta-row">Markers: <strong>${m.markers.length}</strong></div>` : '';
            
            // Format name hash and offsets to hex as BarsReaderGUI does
            const nameHashHex = e.name_hash ? e.name_hash.toString(16).toUpperCase() : 'N/A';
            const metaOffsetHex = e.amta_offset ? e.amta_offset.toString(16).toUpperCase() : 'N/A';
            const bwavOffsetHex = e.bwav_offset !== -1 ? e.bwav_offset.toString(16).toUpperCase() : 'N/A';

            metaHtml = `
                <div class="metadata-block">
                    <div class="meta-row">Name Hash: <strong>${nameHashHex}</strong></div>
                    <div class="meta-row">Meta Offset: <strong>${metaOffsetHex}</strong></div>
                    <div class="meta-row">Asset Offset: <strong>${bwavOffsetHex}</strong></div>
                    <div class="meta-row">Type: <strong>${escapeHtml(m.audio_type)}</strong></div>
                    <div class="meta-row">Channels: <strong>${m.channel_count}</strong></div>
                    <div class="meta-row">Volume: <strong>${m.volume_db.toFixed(1)} dB</strong></div>
                    ${markers}
                </div>
            `;
        }

        return `
        <div class="entry">
            <div class="entry-info">
                <div class="entry-name">${escapeHtml(e.name)}</div>
                <div class="entry-meta">
                    ${e.has_prefetch ? '<span class="tag prefetch">Prefetch</span>' : ''}
                    ${e.has_romfs_bwav ? '<span class="tag romfs">RomFS</span>' : ''}
                </div>
                ${metaHtml}
            </div>
            <button class="play-btn" data-index="${idx}" ${!canPlay ? 'disabled' : ''}>
                ${canPlay ? '▶ Play' : 'Unavailable'}
            </button>
            <div class="audio-container" id="audio-container-${idx}" style="display: none;">
                <audio controls id="audio-player-${idx}"></audio>
            </div>
        </div>
        `;
    }).join('');

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
        padding: 20px;
    }
    .header {
        font-size: 18px;
        font-weight: 600;
        margin-bottom: 24px;
        color: var(--vscode-foreground, #ddd);
        word-break: break-all;
    }
    .entry-list {
        display: flex;
        flex-direction: column;
        gap: 12px;
    }
    .entry {
        background: var(--vscode-editorWidget-background, #252526);
        border: 1px solid var(--vscode-panel-border, #444);
        padding: 12px 16px;
        border-radius: 6px;
        display: flex;
        align-items: center;
        gap: 16px;
        flex-wrap: wrap;
    }
    .entry-info {
        flex: 1;
        min-width: 200px;
    }
    .entry-name {
        font-weight: 500;
        margin-bottom: 4px;
        word-break: break-all;
    }
    .entry-meta {
        display: flex;
        gap: 6px;
    }
    .tag {
        font-size: 10px;
        padding: 2px 6px;
        border-radius: 4px;
        text-transform: uppercase;
        font-weight: 600;
    }
    .tag.prefetch { background: #3b82f640; color: #60a5fa; }
    .tag.romfs { background: #10b98140; color: #34d399; }
    
    .metadata-block {
        margin-top: 10px;
        font-size: 11px;
        color: var(--vscode-descriptionForeground, #a0a0a0);
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
        gap: 4px 12px;
        background: var(--vscode-editor-background, #1e1e1e);
        padding: 8px 12px;
        border-radius: 4px;
        border: 1px solid var(--vscode-panel-border, #444);
    }
    .meta-row strong {
        color: var(--vscode-foreground, #ccc);
    }
    
    .play-btn {
        background: var(--vscode-button-background, #0e639c);
        color: var(--vscode-button-foreground, #fff);
        border: none;
        padding: 6px 12px;
        border-radius: 4px;
        cursor: pointer;
        font-weight: 600;
        min-width: 80px;
    }
    .play-btn:hover:not(:disabled) {
        background: var(--vscode-button-hoverBackground, #1177bb);
    }
    .play-btn:disabled {
        opacity: 0.5;
        cursor: not-allowed;
    }
    .play-btn.loading {
        opacity: 0.7;
        cursor: wait;
    }
    .audio-container {
        width: 100%;
        margin-top: 12px;
    }
    audio {
        width: 100%;
        outline: none;
        height: 36px;
    }
</style>
</head>
<body>
    <div class="header">BARS: ${escapeHtml(barsName)}</div>
    <div class="entry-list">
        ${entriesHtml}
    </div>
    
    <script>
        const vscode = acquireVsCodeApi();
        
        document.querySelectorAll('.play-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                const index = btn.getAttribute('data-index');
                
                // Pause all other audios
                document.querySelectorAll('audio').forEach(a => {
                    a.pause();
                });
                
                const container = document.getElementById('audio-container-' + index);
                const audio = document.getElementById('audio-player-' + index);
                
                if (audio.src && audio.src !== window.location.href) {
                    audio.currentTime = 0;
                    audio.play();
                    container.style.display = 'block';
                    return;
                }
                
                btn.classList.add('loading');
                btn.textContent = 'Loading...';
                
                vscode.postMessage({ type: 'play-entry', index: parseInt(index, 10) });
            });
        });

        window.addEventListener('message', event => {
            const message = event.data;
            if (message.type === 'play-audio' || message.type === 'play-error') {
                const index = message.index;
                const btn = document.querySelector(\`.play-btn[data-index="\${index}"]\`);
                
                if (btn) {
                    btn.classList.remove('loading');
                    btn.textContent = '▶ Play';
                }
                
                if (message.type === 'play-audio') {
                    const container = document.getElementById('audio-container-' + index);
                    const audio = document.getElementById('audio-player-' + index);
                    if (container && audio) {
                        audio.src = message.url;
                        container.style.display = 'block';
                        audio.play();
                    }
                }
            }
        });
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
