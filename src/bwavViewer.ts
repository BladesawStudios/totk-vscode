import * as vscode from 'vscode';
import * as os from 'os';
import * as path from 'path';

export function getBwavViewerHtml(webview: vscode.Webview, bwavName: string, extensionUri: vscode.Uri) {
    return /* html */ `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<style>
    :root {
        --bg-color: var(--vscode-editor-background);
        --text-color: var(--vscode-editor-foreground);
        --player-bg: var(--vscode-editorWidget-background);
        --player-border: var(--vscode-widget-border);
        --player-text: var(--vscode-editorWidget-foreground);
        --hover-bg: var(--vscode-list-hoverBackground);
        --accent: var(--vscode-button-background);
        --accent-hover: var(--vscode-button-hoverBackground);
    }
    body {
        font-family: var(--vscode-font-family);
        background-color: var(--bg-color);
        color: var(--text-color);
        margin: 0;
        padding: 20px;
        display: flex;
        flex-direction: column;
        align-items: center;
        justify-content: center;
        height: 100vh;
        box-sizing: border-box;
    }

    .player-card {
        background-color: var(--player-bg);
        border: 1px solid var(--player-border);
        border-radius: 8px;
        padding: 24px;
        width: 100%;
        max-width: 600px;
        box-shadow: 0 4px 12px rgba(0,0,0,0.15);
        display: flex;
        flex-direction: column;
        gap: 20px;
    }

    .header {
        font-size: 18px;
        font-weight: 600;
        color: var(--player-text);
        text-align: center;
        word-break: break-all;
    }

    .custom-player {
        display: flex;
        align-items: center;
        gap: 12px;
        width: 100%;
    }

    .player-play-btn {
        background: var(--accent);
        color: var(--vscode-button-foreground);
        border: none;
        width: 40px;
        height: 40px;
        border-radius: 50%;
        display: flex;
        align-items: center;
        justify-content: center;
        cursor: pointer;
        flex-shrink: 0;
        transition: background 0.2s, opacity 0.2s;
    }
    .player-play-btn:hover:not(:disabled) {
        background: var(--accent-hover);
    }
    .player-play-btn:disabled {
        opacity: 0.5;
        cursor: not-allowed;
    }

    .player-repeat-btn {
        background: transparent;
        color: var(--vscode-descriptionForeground);
        border: none;
        width: 32px;
        height: 32px;
        border-radius: 4px;
        display: flex;
        align-items: center;
        justify-content: center;
        cursor: pointer;
        transition: background 0.2s, color 0.2s;
        flex-shrink: 0;
    }
    .player-repeat-btn:hover {
        background: var(--hover-bg);
        color: var(--vscode-foreground);
    }
    .player-repeat-btn.active {
        color: var(--accent);
        background: var(--vscode-toolbar-hoverBackground);
    }

    .time-display {
        font-size: 12px;
        color: var(--vscode-descriptionForeground);
        min-width: 40px;
        font-variant-numeric: tabular-nums;
        text-align: center;
    }

    .progress-container {
        flex-grow: 1;
        height: 24px;
        position: relative;
        display: flex;
        align-items: center;
    }

    .progress-bar {
        -webkit-appearance: none;
        width: 100%;
        height: 6px;
        background: var(--vscode-scrollbarSlider-background);
        border-radius: 3px;
        outline: none;
        margin: 0;
        z-index: 2;
    }
    .progress-bar::-webkit-slider-thumb {
        -webkit-appearance: none;
        width: 12px;
        height: 12px;
        border-radius: 50%;
        background: var(--accent);
        cursor: pointer;
        transition: transform 0.1s;
    }
    .progress-bar:not(:disabled)::-webkit-slider-thumb:hover {
        transform: scale(1.2);
    }

    .loop-marker {
        position: absolute;
        top: 50%;
        transform: translate(-50%, -50%);
        width: 4px;
        height: 14px;
        background-color: #f59e0b;
        border-radius: 2px;
        pointer-events: none;
        z-index: 1;
    }

    .loop-label {
        font-size: 12px;
        color: #f59e0b;
        text-align: center;
        font-weight: 500;
        margin-top: -10px;
    }
</style>
</head>
<body>
    <div class="player-card">
        <div class="header" id="track-name">${bwavName}</div>
        
        <div class="custom-player" id="player-container">
            <button class="player-play-btn" id="play-btn" disabled title="Loading...">
                <span class="play-icon">▶</span>
                <span class="pause-icon" style="display:none;">⏸</span>
            </button>
            <button class="player-repeat-btn" id="repeat-btn" style="display: none;" title="Repeat">
                <svg viewBox="0 0 24 24" width="16" height="16" stroke="currentColor" stroke-width="2" fill="none" stroke-linecap="round" stroke-linejoin="round"><polyline points="17 1 21 5 17 9"></polyline><path d="M3 11V9a4 4 0 0 1 4-4h14"></path><polyline points="7 23 3 19 7 15"></polyline><path d="M21 13v2a4 4 0 0 1-4 4H3"></path></svg>
            </button>
            <span class="time-display" id="time-current">0:00</span>
            <div class="progress-container">
                <input type="range" class="progress-bar" id="progress" value="0" min="0" max="100" step="0.01" disabled>
                <div id="loop-start-marker" class="loop-marker" style="display: none;"></div>
                <div id="loop-end-marker" class="loop-marker" style="display: none;"></div>
            </div>
            <span class="time-display" id="time-total">--:--</span>
        </div>
        <div class="loop-label" id="loop-label" style="display: none;"></div>
    </div>

    <script>
        const vscode = acquireVsCodeApi();
        
        function formatTime(seconds) {
            if (isNaN(seconds) || !isFinite(seconds)) return '--:--';
            const m = Math.floor(seconds / 60);
            const s = Math.floor(seconds % 60);
            return m + ':' + (s < 10 ? '0' : '') + s;
        }

        // Web Audio API
        const audioCtx = new (window.AudioContext || window.webkitAudioContext)();
        let player = null;
        
        function updateUI() {
            const btn = document.getElementById('play-btn');
            if (!btn || !player) return;
            const playIcon = btn.querySelector('.play-icon');
            const pauseIcon = btn.querySelector('.pause-icon');
            if (player.isPlaying) {
                playIcon.style.display = 'none';
                pauseIcon.style.display = 'inline';
            } else {
                playIcon.style.display = 'inline';
                pauseIcon.style.display = 'none';
                
                if (player.pausedAt === 0) {
                    const progress = document.getElementById('progress');
                    const timeCurrent = document.getElementById('time-current');
                    if (progress) progress.value = 0;
                    if (timeCurrent) timeCurrent.textContent = formatTime(0);
                }
            }
        }

        function playAudio() {
            if (audioCtx.state === 'suspended') audioCtx.resume();
            
            if (!player || !player.buffer) return;
            if (player.isPlaying) return;

            player.source = audioCtx.createBufferSource();
            player.source.buffer = player.buffer;
            if (player.isLooping && player.loopEnd !== null) {
                player.source.loop = true;
                player.source.loopStart = player.loopStart;
                player.source.loopEnd = player.loopEnd;
            }
            player.source.connect(audioCtx.destination);
            
            player.source.start(0, player.pausedAt);
            player.startedAt = audioCtx.currentTime - player.pausedAt;
            player.isPlaying = true;
            
            const currentSource = player.source;
            player.source.onended = () => {
                if (player.source !== currentSource) return;
                if (player.isPlaying && (!player.isLooping || player.loopEnd === null)) {
                    player.isPlaying = false;
                    player.pausedAt = 0;
                    updateUI();
                }
            };

            updateUI();
        }

        function pauseAudio() {
            if (!player || !player.isPlaying) return;
            
            player.source.stop();
            player.pausedAt = audioCtx.currentTime - player.startedAt;
            
            if (player.isLooping && player.loopEnd !== null && player.pausedAt >= player.loopEnd) {
                const loopDur = player.loopEnd - player.loopStart;
                while(player.pausedAt >= player.loopEnd) {
                    player.pausedAt -= loopDur;
                }
            }

            player.isPlaying = false;
            updateUI();
        }

        function seekAudio(time) {
            if (!player) return;
            
            const wasPlaying = player.isPlaying;
            if (wasPlaying) pauseAudio();
            player.pausedAt = time;
            if (wasPlaying) playAudio();
        }
        
        function renderLoop() {
            requestAnimationFrame(renderLoop);
            if (!player || !player.isPlaying) return;
            
            let currentTime = audioCtx.currentTime - player.startedAt;
            if (player.isLooping && player.loopEnd !== null && currentTime >= player.loopEnd) {
                const loopDur = player.loopEnd - player.loopStart;
                const pastLoop = currentTime - player.loopStart;
                currentTime = player.loopStart + (pastLoop % loopDur);
            } else if (currentTime > player.buffer.duration) {
                 currentTime = player.buffer.duration;
            }
            
            const progress = document.getElementById('progress');
            const timeCurrent = document.getElementById('time-current');
            if (!player.isSeeking && progress && timeCurrent) {
                progress.value = currentTime;
                timeCurrent.textContent = formatTime(currentTime);
            }
        }
        requestAnimationFrame(renderLoop);

        // UI Event Listeners
        const btn = document.getElementById('play-btn');
        const progress = document.getElementById('progress');
        const timeCurrent = document.getElementById('time-current');
        const repeatBtn = document.getElementById('repeat-btn');

        btn.addEventListener('click', () => {
            if (!player) return;
            if (!player.isPlaying) {
                playAudio();
            } else {
                pauseAudio();
            }
        });

        repeatBtn.addEventListener('click', () => {
            if (!player) return;
            player.isLooping = !player.isLooping;
            repeatBtn.classList.toggle('active', player.isLooping);
            
            const wasPlaying = player.isPlaying;
            if (wasPlaying) pauseAudio();
            if (wasPlaying) playAudio();
        });

        progress.addEventListener('input', () => {
            if (player) player.isSeeking = true;
            timeCurrent.textContent = formatTime(progress.value);
        });

        progress.addEventListener('change', () => {
            if (player) {
                player.isSeeking = false;
                seekAudio(parseFloat(progress.value));
            }
        });

        window.addEventListener('message', event => {
            const message = event.data;
            if (message.type === 'audio-loaded') {
                const { url, result } = message;
                const btn = document.getElementById('play-btn');
                const progress = document.getElementById('progress');
                const timeTotal = document.getElementById('time-total');

                fetch(url)
                    .then(response => response.arrayBuffer())
                    .then(arrayBuffer => audioCtx.decodeAudioData(arrayBuffer))
                    .then(audioBuffer => {
                        player = {
                            buffer: audioBuffer,
                            source: null,
                            startedAt: 0,
                            pausedAt: 0,
                            isPlaying: false,
                            loopStart: result.loopStart !== undefined ? result.loopStart : null,
                            loopEnd: result.loopEnd !== undefined ? result.loopEnd : null,
                            isLooping: result.loopStart !== undefined && result.loopStart !== null,
                            isSeeking: false
                        };
                        
                        if (timeTotal) timeTotal.textContent = formatTime(audioBuffer.duration);
                        if (progress) {
                            progress.max = audioBuffer.duration;
                            progress.disabled = false;
                        }
                        if (btn) {
                            btn.disabled = false;
                            btn.title = "Play/Pause";
                            const playIcon = btn.querySelector('.play-icon');
                            if (playIcon) playIcon.textContent = '▶';
                        }
                        
                        if (player.isLooping) {
                            const repeatBtn = document.getElementById('repeat-btn');
                            const markerStart = document.getElementById('loop-start-marker');
                            const markerEnd = document.getElementById('loop-end-marker');
                            const loopLabel = document.getElementById('loop-label');
                            
                            if (repeatBtn) {
                                repeatBtn.classList.add('active');
                                repeatBtn.style.display = 'flex';
                            }
                            if (markerStart) {
                                markerStart.style.display = 'block';
                                markerStart.style.left = (player.loopStart / audioBuffer.duration * 100) + '%';
                            }
                            if (markerEnd) {
                                markerEnd.style.display = 'block';
                                markerEnd.style.left = (player.loopEnd / audioBuffer.duration * 100) + '%';
                            }
                            if (loopLabel) {
                                loopLabel.style.display = 'block';
                                loopLabel.textContent = \`Loop: \${formatTime(player.loopStart)} - \${formatTime(player.loopEnd)}\`;
                            }
                        }
                    })
                    .catch(e => {
                        console.error('Error decoding audio', e);
                        if (btn) {
                            btn.title = "Error decoding";
                            const playIcon = btn.querySelector('.play-icon');
                            if (playIcon) playIcon.textContent = '✕';
                        }
                    });
            } else if (message.type === 'audio-error') {
                const btn = document.getElementById('play-btn');
                if (btn) {
                    btn.title = "Error loading";
                    const playIcon = btn.querySelector('.play-icon');
                    if (playIcon) playIcon.textContent = '✕';
                }
            }
        });

        // Trigger fetch immediately
        vscode.postMessage({ type: 'fetch-audio' });
    </script>
</body>
</html>`;
}
