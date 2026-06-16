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
        if (message.type === 'fetch-audio') {
            try {
                const res = await fetchAudio(message.index);
                if (res.wavPath) {
                    const uri = panel.webview.asWebviewUri(vscode.Uri.file(res.wavPath));
                    panel.webview.postMessage({ type: 'audio-loaded', index: message.index, url: uri.toString() });
                } else {
                    void vscode.window.showErrorMessage('Failed to decode BARS audio entry: ' + (res.error || 'Unknown error'));
                    // Reset UI loading state
                    panel.webview.postMessage({ type: 'audio-error', index: message.index });
                }
            } catch (e) {
                const msg = e instanceof Error ? e.message : String(e);
                void vscode.window.showErrorMessage(`Error fetching audio: ${msg}`);
                panel.webview.postMessage({ type: 'audio-error', index: message.index });
            }
        }
    });

    panels.set(key, panel);
    panel.onDidDispose(() => {
        panels.delete(key);
    });
}

function buildHtml(barsName: string, entries: BarsEntry[]): string {
    const playableIndices: number[] = [];

    const entriesHtml = entries.map((e, idx) => {
        const canPlay = e.has_prefetch || e.has_romfs_bwav;
        if (canPlay) {
            playableIndices.push(idx);
        }

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
            <div class="entry-header">
                <div class="entry-name">${escapeHtml(e.name)}</div>
                <div class="entry-meta">
                    ${e.has_prefetch ? '<span class="tag prefetch">Prefetch</span>' : ''}
                    ${e.has_romfs_bwav ? '<span class="tag romfs">RomFS</span>' : ''}
                </div>
            </div>
            
            <div class="custom-player" id="player-container-${idx}">
                <button class="player-play-btn" id="play-btn-${idx}" data-index="${idx}" ${!canPlay ? 'disabled title="Unavailable"' : 'disabled title="Loading..."'}>
                    ${canPlay ? '<span class="play-icon">▶</span><span class="pause-icon" style="display:none;">⏸</span>' : '✕'}
                </button>
                <span class="time-display" id="time-current-${idx}">0:00</span>
                <input type="range" class="progress-bar" id="progress-${idx}" value="0" min="0" max="100" step="0.01" disabled>
                <span class="time-display" id="time-total-${idx}">--:--</span>
                <audio id="audio-${idx}" style="display: none;"></audio>
            </div>

            ${metaHtml}
        </div>
        `;
    }).join('');

    return /* html */ `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<style>
    :root {
        --player-bg: var(--vscode-editorWidget-background, #252526);
        --player-border: var(--vscode-panel-border, #444);
        --player-accent: var(--vscode-button-background, #0e639c);
        --player-accent-hover: var(--vscode-button-hoverBackground, #1177bb);
        --player-text: var(--vscode-foreground, #ccc);
    }
    * { box-sizing: border-box; margin: 0; padding: 0; }
    body {
        font-family: var(--vscode-font-family, 'Segoe UI', sans-serif);
        font-size: 13px;
        color: var(--player-text);
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
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(360px, 1fr));
        gap: 16px;
    }
    .entry {
        background: var(--player-bg);
        border: 1px solid var(--player-border);
        padding: 16px;
        border-radius: 8px;
        display: flex;
        flex-direction: column;
        gap: 14px;
    }
    .entry-header {
        display: flex;
        justify-content: space-between;
        align-items: flex-start;
        gap: 12px;
    }
    .entry-name {
        font-weight: 600;
        font-size: 14px;
        word-break: break-all;
    }
    .entry-meta {
        display: flex;
        gap: 6px;
        flex-wrap: wrap;
        justify-content: flex-end;
    }
    .tag {
        font-size: 10px;
        padding: 2px 6px;
        border-radius: 4px;
        text-transform: uppercase;
        font-weight: 600;
        white-space: nowrap;
    }
    .tag.prefetch { background: #3b82f640; color: #60a5fa; }
    .tag.romfs { background: #10b98140; color: #34d399; }
    
    .custom-player {
        display: flex;
        align-items: center;
        gap: 10px;
        background: var(--vscode-editor-background, #1e1e1e);
        padding: 10px 14px;
        border-radius: 6px;
        border: 1px solid var(--player-border);
    }
    .player-play-btn {
        background: var(--player-accent);
        color: #fff;
        border: none;
        width: 32px;
        height: 32px;
        border-radius: 50%;
        cursor: pointer;
        display: flex;
        align-items: center;
        justify-content: center;
        font-size: 14px;
        flex-shrink: 0;
        transition: background 0.1s;
    }
    .player-play-btn:hover:not(:disabled) {
        background: var(--player-accent-hover);
    }
    .player-play-btn:disabled {
        opacity: 0.5;
        cursor: not-allowed;
        background: var(--player-border);
    }
    .time-display {
        font-size: 11px;
        color: var(--vscode-descriptionForeground, #a0a0a0);
        min-width: 36px;
        text-align: center;
        font-variant-numeric: tabular-nums;
    }
    input[type=range].progress-bar {
        flex: 1;
        -webkit-appearance: none;
        background: transparent;
        cursor: pointer;
        height: 16px;
    }
    input[type=range].progress-bar::-webkit-slider-runnable-track {
        height: 4px;
        background: var(--player-border);
        border-radius: 2px;
    }
    input[type=range].progress-bar::-webkit-slider-thumb {
        -webkit-appearance: none;
        height: 12px;
        width: 12px;
        border-radius: 50%;
        background: var(--player-accent);
        margin-top: -4px;
        transition: transform 0.1s;
    }
    input[type=range].progress-bar:hover::-webkit-slider-thumb:not(:disabled) {
        transform: scale(1.2);
    }
    input[type=range].progress-bar:disabled {
        cursor: not-allowed;
        opacity: 0.5;
    }
    
    .metadata-block {
        font-size: 11px;
        color: var(--vscode-descriptionForeground, #a0a0a0);
        display: grid;
        grid-template-columns: 1fr 1fr;
        gap: 6px 12px;
        background: var(--vscode-editor-background, #1e1e1e);
        padding: 10px 12px;
        border-radius: 6px;
        border: 1px solid var(--player-border);
        margin-top: auto;
    }
    .meta-row strong {
        color: var(--player-text);
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
        
        function formatTime(seconds) {
            if (isNaN(seconds) || !isFinite(seconds)) return '--:--';
            const m = Math.floor(seconds / 60);
            const s = Math.floor(seconds % 60);
            return m + ':' + (s < 10 ? '0' : '') + s;
        }

        const playableIndices = ${JSON.stringify(playableIndices)};
        let fetchQueue = [...playableIndices];
        let isFetching = false;

        function fetchNext() {
            if (fetchQueue.length === 0) return;
            isFetching = true;
            const index = fetchQueue.shift();
            vscode.postMessage({ type: 'fetch-audio', index });
        }

        // Initialize players
        playableIndices.forEach(idx => {
            const btn = document.getElementById('play-btn-' + idx);
            const audio = document.getElementById('audio-' + idx);
            const progress = document.getElementById('progress-' + idx);
            const timeCurrent = document.getElementById('time-current-' + idx);
            const playIcon = btn.querySelector('.play-icon');
            const pauseIcon = btn.querySelector('.pause-icon');

            let isSeeking = false;

            btn.addEventListener('click', () => {
                if (audio.paused) {
                    // Pause all other audios
                    document.querySelectorAll('audio').forEach(a => {
                        if (a !== audio && !a.paused) {
                            a.pause();
                        }
                    });
                    audio.play().catch(e => console.error("Error playing audio", e));
                } else {
                    audio.pause();
                }
            });

            audio.addEventListener('play', () => {
                playIcon.style.display = 'none';
                pauseIcon.style.display = 'inline';
            });

            audio.addEventListener('pause', () => {
                playIcon.style.display = 'inline';
                pauseIcon.style.display = 'none';
            });

            audio.addEventListener('timeupdate', () => {
                if (!isSeeking) {
                    progress.value = audio.currentTime;
                    timeCurrent.textContent = formatTime(audio.currentTime);
                }
            });

            progress.addEventListener('input', () => {
                isSeeking = true;
                timeCurrent.textContent = formatTime(progress.value);
            });

            progress.addEventListener('change', () => {
                isSeeking = false;
                audio.currentTime = progress.value;
            });
            
            audio.addEventListener('ended', () => {
                playIcon.style.display = 'inline';
                pauseIcon.style.display = 'none';
                progress.value = 0;
                timeCurrent.textContent = formatTime(0);
            });
        });

        window.addEventListener('message', event => {
            const message = event.data;
            if (message.type === 'audio-loaded') {
                const { index, url } = message;
                const audio = document.getElementById('audio-' + index);
                const btn = document.getElementById('play-btn-' + index);
                const progress = document.getElementById('progress-' + index);
                const timeTotal = document.getElementById('time-total-' + index);

                if (audio) {
                    audio.src = url;
                    audio.load();
                    
                    audio.addEventListener('loadedmetadata', () => {
                        timeTotal.textContent = formatTime(audio.duration);
                        progress.max = audio.duration;
                        progress.disabled = false;
                        btn.disabled = false;
                        btn.title = "Play/Pause";
                    }, { once: true });
                }
                
                isFetching = false;
                fetchNext();
            } else if (message.type === 'audio-error') {
                const btn = document.getElementById('play-btn-' + message.index);
                if (btn) {
                    btn.title = "Error loading";
                    btn.querySelector('.play-icon').textContent = '✕';
                }
                isFetching = false;
                fetchNext();
            }
        });

        // Start fetching process
        fetchNext();
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
