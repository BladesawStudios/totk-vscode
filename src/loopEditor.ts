import * as path from 'path';
import * as vscode from 'vscode';
import { WAVEFORM_SCRIPT } from './waveformScript';

export interface LoopEditorSource {
    name: string;
    /** A 16-bit WAV of the source, decoded the way the replace will read it. */
    wavPath: string;
    sampleRate: number;
    channels: number;
    samples: number;
    loopStart?: number | null;
    loopEnd?: number | null;
    /** The audio is a track already in the game or mod, not a new file. Its loop is the current one, and cutting it re-encodes. */
    existingTrack?: boolean;
}

export type LoopEditorResult =
    | { start: number; end: number; trimAfterLoop: boolean }
    | 'none';

/** Opens the waveform loop editor. Resolves with the choice, or undefined when it was cancelled or closed. */
export function openLoopEditor(source: LoopEditorSource): Promise<LoopEditorResult | undefined> {
    const panel = vscode.window.createWebviewPanel(
        'totkLoopEditor',
        `Loop: ${source.name}`,
        vscode.ViewColumn.Active,
        {
            enableScripts: true,
            retainContextWhenHidden: true,
            localResourceRoots: [vscode.Uri.file(path.dirname(source.wavPath))],
        },
    );
    panel.webview.html = buildHtml(source, panel.webview.asWebviewUri(vscode.Uri.file(source.wavPath)).toString());

    return new Promise((resolve) => {
        let answered = false;
        panel.webview.onDidReceiveMessage((message) => {
            if (message.type === 'apply') {
                answered = true;
                resolve(message.none
                    ? 'none'
                    : { start: message.start, end: message.end, trimAfterLoop: !!message.trimAfterLoop });
                panel.dispose();
            } else if (message.type === 'cancel') {
                panel.dispose();
            } else if (message.type === 'load-failed') {
                void vscode.window.showErrorMessage(`The loop editor could not load the audio: ${message.error}`);
                panel.dispose();
            }
        });
        panel.onDidDispose(() => {
            if (!answered) {
                resolve(undefined);
            }
        });
    });
}

function buildHtml(source: LoopEditorSource, audioUrl: string): string {
    const samples = source.samples;
    // A loop from the file only counts if it fits the audio; an end past the last sample is cut back, as the encoder does.
    const fileEnd = source.loopEnd === null || source.loopEnd === undefined ? null : Math.min(source.loopEnd, samples);
    const fileStart = source.loopStart ?? null;
    const hasFileLoop = fileEnd !== null && fileStart !== null && fileStart >= 0 && fileStart < fileEnd;
    const data = JSON.stringify({
        name: source.name,
        url: audioUrl,
        rate: source.sampleRate,
        channels: source.channels,
        samples,
        fileLoop: hasFileLoop ? { start: fileStart, end: fileEnd } : null,
        existingTrack: !!source.existingTrack,
    }).replace(/</g, '\\u003c');

    return /* html */ `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<style>
    * { box-sizing: border-box; }
    body {
        font-family: var(--vscode-font-family, 'Segoe UI', sans-serif);
        font-size: 13px;
        color: var(--vscode-foreground, #ccc);
        background: var(--vscode-editor-background, #1e1e1e);
        margin: 0;
        padding: 16px 20px;
        display: flex;
        flex-direction: column;
        gap: 12px;
        user-select: none;
    }
    h1 { font-size: 16px; font-weight: 600; margin: 0; word-break: break-all; }
    .sub { color: var(--vscode-descriptionForeground, #a0a0a0); font-size: 12px; font-variant-numeric: tabular-nums; }
    .row { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
    .spacer { flex: 1; }
    canvas { display: block; width: 100%; background: var(--vscode-editorWidget-background, #252526); border: 1px solid var(--vscode-panel-border, #444); border-radius: 4px; }
    #wave { height: 240px; touch-action: none; }
    #mini { height: 36px; cursor: pointer; touch-action: none; }
    #seam { height: 110px; }
    button {
        font: inherit;
        color: var(--vscode-button-secondaryForeground, #fff);
        background: var(--vscode-button-secondaryBackground, #3a3d41);
        border: none;
        border-radius: 3px;
        padding: 4px 10px;
        cursor: pointer;
        white-space: nowrap;
    }
    button:hover:not(:disabled) { background: var(--vscode-button-secondaryHoverBackground, #45494e); }
    button:disabled { opacity: 0.45; cursor: default; }
    button.primary { color: var(--vscode-button-foreground, #fff); background: var(--vscode-button-background, #0e639c); padding: 6px 16px; }
    button.primary:hover:not(:disabled) { background: var(--vscode-button-hoverBackground, #1177bb); }
    button.small { padding: 2px 6px; font-size: 12px; font-variant-numeric: tabular-nums; }
    input[type=text], input[type=number] {
        font: inherit;
        font-variant-numeric: tabular-nums;
        color: var(--vscode-input-foreground, #ccc);
        background: var(--vscode-input-background, #3c3c3c);
        border: 1px solid var(--vscode-input-border, transparent);
        border-radius: 3px;
        padding: 3px 6px;
        user-select: text;
    }
    input:focus { outline: 1px solid var(--vscode-focusBorder, #007fd4); }
    input.samples { width: 110px; }
    input.time { width: 90px; }
    label { display: inline-flex; align-items: center; gap: 5px; cursor: pointer; }
    .point { display: grid; grid-template-columns: 56px auto; gap: 8px; align-items: center; }
    .point-name { font-weight: 600; }
    .points { display: flex; flex-direction: column; gap: 6px; }
    #start-name { color: var(--vscode-charts-green, #89d185); }
    #end-name { color: var(--vscode-charts-red, #f14c4c); }
    .disabled-group { opacity: 0.45; pointer-events: none; }
    h2 { font-size: 12px; font-weight: 600; margin: 0; }
    #seam-note { font-variant-numeric: tabular-nums; }
    .help { color: var(--vscode-descriptionForeground, #a0a0a0); font-size: 11px; }
</style>
</head>
<body>
    <div>
        <h1 id="title"></h1>
        <div class="sub" id="info"></div>
    </div>

    <canvas id="wave"></canvas>
    <canvas id="mini"></canvas>
    <div class="help">Scroll to zoom. Drag the waveform to pan, click it to move the playhead, drag the green and red markers to set the loop. Hold Alt while dragging a marker to skip snapping.</div>

    <div class="row">
        <button id="play" title="Space">▶ Play</button>
        <button id="rewind">⏮ Rewind</button>
        <button id="hear-seam" title="Plays the two seconds before the loop end, then two seconds from the loop start">Hear seam</button>
        <span class="sub" id="position"></span>
        <span class="spacer"></span>
        <button id="zoom-fit">Fit</button>
        <button id="zoom-loop">Zoom to loop</button>
        <button id="zoom-start">Zoom to start</button>
        <button id="zoom-end">Zoom to end</button>
    </div>

    <div class="row">
        <label><input type="checkbox" id="loop-on"> Loop this sound</label>
        <label title="Move a dragged marker to the nearest zero crossing, which helps avoid clicks"><input type="checkbox" id="snap" checked> Snap to zero crossings</label>
        <span class="spacer"></span>
        <button id="preset-whole">Whole clip</button>
        <button id="preset-file">Loop from file</button>
    </div>

    <div class="points" id="points"></div>
    <div class="row" id="trim-row">
        <label><input type="checkbox" id="trim" checked> Cut the audio after the loop end</label>
        <span class="help" id="trim-help">Recommended: every looping sound in the game ends exactly on its loop end.</span>
    </div>

    <div id="seam-section">
        <div class="row" style="margin-bottom: 4px;">
            <h2>Loop seam</h2>
            <span class="sub">end of the loop | start of the loop</span>
            <span class="spacer"></span>
            <span class="sub" id="seam-note"></span>
        </div>
        <canvas id="seam"></canvas>
    </div>

    <div class="row">
        <span class="sub" id="summary"></span>
        <span class="spacer"></span>
        <button id="cancel">Cancel</button>
        <button id="apply" class="primary" disabled>Use these loop points</button>
    </div>

<script>
    const data = ${data};
    const vscode = acquireVsCodeApi();
${WAVEFORM_SCRIPT}

    let loopOn = !!data.fileLoop;
    let start = data.fileLoop ? data.fileLoop.start : 0;
    let end = data.fileLoop ? data.fileLoop.end : N;
    let source = null, anchorTime = 0, anchorPos = 0, pausedPos = 0;

    $('title').textContent = data.name;
    $('info').textContent = N.toLocaleString() + ' samples, ' + formatTime(N / RATE) + ', '
        + RATE.toLocaleString() + ' Hz, ' + data.channels + (data.channels === 1 ? ' channel' : ' channels');
    $('preset-file').disabled = !data.fileLoop;
    $('loop-on').checked = loopOn;
    if (data.existingTrack) {
        $('preset-file').textContent = 'Current loop';
        $('trim-help').textContent += ' Cutting re-encodes this track, which loses a little quality. Otherwise only the loop changes.';
    }

    // ---- the loop points ----

    function setStart(value) {
        start = Math.max(0, Math.min(end - 1, Math.round(value)));
        changed();
    }

    function setEnd(value) {
        end = Math.max(start + 1, Math.min(N, Math.round(value)));
        changed();
    }

    function setLoop(s, e) {
        start = 0;
        end = N;
        setEnd(e);
        setStart(s);
    }

    function changed() {
        // A playing source follows the new points straight away.
        if (source) applyLoop(source);
        refreshControls();
        requestDraw();
    }

    function applyLoop(node) {
        node.loop = loopOn;
        node.loopStart = start / RATE;
        node.loopEnd = end / RATE;
    }

    // ---- playback ----

    function position() {
        if (!source) return pausedPos;
        let pos = anchorPos + (audioCtx.currentTime - anchorTime);
        const s = start / RATE, e = end / RATE;
        if (loopOn && pos >= e) {
            pos = s + ((pos - s) % (e - s));
            anchorPos = pos;
            anchorTime = audioCtx.currentTime;
        }
        return Math.min(pos, N / RATE);
    }

    function stop() {
        if (!source) return;
        const old = source;
        pausedPos = position();
        source = null;
        old.stop();
    }

    function play(fromSeconds, duration) {
        if (!buffer) return;
        stop();
        audioCtx.resume();
        const node = audioCtx.createBufferSource();
        node.buffer = buffer;
        node.connect(audioCtx.destination);
        applyLoop(node);
        anchorTime = audioCtx.currentTime;
        anchorPos = fromSeconds;
        node.start(anchorTime, fromSeconds);
        if (duration !== undefined) node.stop(anchorTime + duration);
        node.onended = () => {
            if (source !== node) return;
            pausedPos = position();
            source = null;
            refreshControls();
            requestDraw();
        };
        source = node;
        refreshControls();
        requestDraw();
    }

    function togglePlay() {
        if (source) {
            stop();
            refreshControls();
            requestDraw();
        } else {
            play(pausedPos >= N / RATE ? 0 : pausedPos);
        }
    }

    function seek(seconds) {
        pausedPos = Math.max(0, Math.min(N / RATE, seconds));
        if (source) play(pausedPos);
        else requestDraw();
    }

    // ---- drawing ----

    function drawMarker(g, x, h, fill, label, pointRight) {
        g.fillStyle = fill;
        g.fillRect(x - 1, 0, 2, h);
        g.beginPath();
        g.moveTo(x, 0);
        g.lineTo(x + (pointRight ? 16 : -16), 0);
        g.lineTo(x + (pointRight ? 16 : -16), 13);
        g.lineTo(x, 13);
        g.closePath();
        g.fill();
        g.fillStyle = '#000';
        g.font = 'bold 10px sans-serif';
        g.fillText(label, pointRight ? x + 5 : x - 11, 10);
    }

    function drawAll() {
        const waveColor = color('--vscode-charts-blue', '#3794ff');
        const startColor = color('--vscode-charts-green', '#89d185');
        const endColor = color('--vscode-charts-red', '#f14c4c');
        const fg = color('--vscode-foreground', '#ccc');
        const muted = color('--vscode-descriptionForeground', '#a0a0a0');
        const dim = color('--vscode-editor-background', '#1e1e1e');
        const pos = position() * RATE;

        // Main view.
        {
            const { g, w, h } = fitCanvas($('wave'));
            g.clearRect(0, 0, w, h);
            const toX = (s) => (s - viewFrom) / (viewTo - viewFrom) * w;
            if (loopOn) {
                g.globalAlpha = 0.12;
                g.fillStyle = startColor;
                g.fillRect(toX(start), RULER, toX(end) - toX(start), h - RULER);
                g.globalAlpha = 1;
            }
            drawWave(g, w, RULER, h - RULER, viewFrom, viewTo, waveColor);
            if (loopOn) {
                // Dim what the loop leaves out: the intro only plays once, and the tail never does.
                g.globalAlpha = 0.6;
                g.fillStyle = dim;
                g.fillRect(toX(end), RULER, w - toX(end), h - RULER);
                g.globalAlpha = 0.25;
                g.fillRect(0, RULER, toX(start), h - RULER);
                g.globalAlpha = 1;
            }
            drawRuler(g, w, muted);
            if (loopOn) {
                drawMarker(g, toX(start), h, startColor, 'S', true);
                drawMarker(g, toX(end), h, endColor, 'E', false);
            }
            g.fillStyle = fg;
            g.fillRect(toX(pos), RULER, 1, h - RULER);
        }

        // Whole-clip strip with the current view on it.
        {
            const { g, w, h } = fitCanvas($('mini'));
            g.clearRect(0, 0, w, h);
            drawWave(g, w, 0, h, 0, N, muted);
            const toX = (s) => s / N * w;
            if (loopOn) {
                g.fillStyle = startColor;
                g.fillRect(toX(start), 0, 1, h);
                g.fillStyle = endColor;
                g.fillRect(toX(end) - 1, 0, 1, h);
            }
            g.globalAlpha = 0.2;
            g.fillStyle = fg;
            g.fillRect(toX(viewFrom), 0, Math.max(2, toX(viewTo) - toX(viewFrom)), h);
            g.globalAlpha = 1;
            g.fillStyle = fg;
            g.fillRect(toX(pos), 0, 1, h);
        }

        if (loopOn) drawSeam(startColor, endColor, muted);
        $('position').textContent = formatTime(pos / RATE) + ' (sample ' + Math.round(pos).toLocaleString() + ')';
    }

    const SEAM_SAMPLES = 240;

    function drawSeam(startColor, endColor, muted) {
        const { g, w, h } = fitCanvas($('seam'));
        g.clearRect(0, 0, w, h);
        g.fillStyle = muted;
        g.fillRect(0, h / 2, w, 1);
        if (!buffer) return;

        const channels = Math.min(2, buffer.numberOfChannels);
        const pitch = w / (SEAM_SAMPLES * 2 - 1);
        // Scaled to what's on screen: the shape of the join is the point here, not its loudness.
        let localPeak = 0.001;
        for (let c = 0; c < channels; c++) {
            const d = buffer.getChannelData(c);
            for (let i = Math.max(0, end - SEAM_SAMPLES); i < end; i++) localPeak = Math.max(localPeak, Math.abs(d[i]));
            for (let i = start; i < Math.min(N, start + SEAM_SAMPLES); i++) localPeak = Math.max(localPeak, Math.abs(d[i]));
        }
        for (let c = 0; c < channels; c++) {
            const d = buffer.getChannelData(c);
            const y = (i) => h / 2 - d[i] / localPeak * (h / 2 - 2);
            g.globalAlpha = c === 0 ? 1 : 0.55;
            for (const [from, stopAt, offset, stroke] of [
                [Math.max(0, end - SEAM_SAMPLES), end, SEAM_SAMPLES - end, endColor],
                [start, Math.min(N, start + SEAM_SAMPLES), SEAM_SAMPLES - start, startColor],
            ]) {
                g.strokeStyle = stroke;
                g.beginPath();
                for (let i = from; i < stopAt; i++) {
                    const x = (i + offset) * pitch;
                    if (i === from) g.moveTo(x, y(i));
                    else g.lineTo(x, y(i));
                }
                g.stroke();
            }
            // The jump itself.
            g.strokeStyle = muted;
            g.beginPath();
            g.moveTo((SEAM_SAMPLES - 1) * pitch, y(end - 1));
            g.lineTo(SEAM_SAMPLES * pitch, y(start));
            g.stroke();
        }
        g.globalAlpha = 1;

        $('seam-note').textContent = 'Jump at the seam: ' + jumpVerdict(jumpBetween(end - 1, start));
    }

    // ---- the waveform: zoom, pan, markers, seek ----

    const wave = $('wave');
    const sampleAt = (ev) => {
        const rect = wave.getBoundingClientRect();
        return viewFrom + (ev.clientX - rect.left - 1) / (rect.width - 2) * (viewTo - viewFrom);
    };
    const xOf = (sample) => (sample - viewFrom) / (viewTo - viewFrom) * (wave.getBoundingClientRect().width - 2);

    function markerAt(ev) {
        if (!loopOn) return null;
        const x = ev.clientX - wave.getBoundingClientRect().left - 1;
        const y = ev.clientY - wave.getBoundingClientRect().top;
        // The tabs at the top reach 16px into the loop from each marker, so they stay grabbable when the two are close.
        if (y <= 14 && x >= xOf(start) && x <= xOf(start) + 16) return 'start';
        if (y <= 14 && x <= xOf(end) && x >= xOf(end) - 16) return 'end';
        const toStart = Math.abs(x - xOf(start)), toEnd = Math.abs(x - xOf(end));
        if (Math.min(toStart, toEnd) > 6) return null;
        return toStart <= toEnd ? 'start' : 'end';
    }

    attachWheelZoom(wave);

    let gesture = null;
    wave.addEventListener('pointerdown', (ev) => {
        wave.setPointerCapture(ev.pointerId);
        const marker = markerAt(ev);
        gesture = marker
            ? { kind: marker }
            : { kind: 'pan', x: ev.clientX, from: viewFrom, to: viewTo, moved: false, sample: sampleAt(ev) };
    });
    wave.addEventListener('pointermove', (ev) => {
        if (!gesture) {
            wave.style.cursor = markerAt(ev) ? 'ew-resize' : 'grab';
            return;
        }
        if (gesture.kind === 'pan') {
            const dx = ev.clientX - gesture.x;
            if (Math.abs(dx) > 3) gesture.moved = true;
            if (gesture.moved) {
                const delta = dx / wave.clientWidth * (gesture.to - gesture.from);
                wave.style.cursor = 'grabbing';
                setView(gesture.from - delta, gesture.to - delta);
            }
            return;
        }
        let sample = Math.round(sampleAt(ev));
        if ($('snap').checked && !ev.altKey) {
            const perPixel = (viewTo - viewFrom) / wave.clientWidth;
            sample = nearestZeroCrossing(sample, Math.max(16, Math.ceil(perPixel * 6)));
        }
        if (gesture.kind === 'start') setStart(sample);
        else setEnd(sample);
    });
    const endGesture = () => {
        if (gesture && gesture.kind === 'pan' && !gesture.moved) seek(gesture.sample / RATE);
        gesture = null;
    };
    wave.addEventListener('pointerup', endGesture);
    wave.addEventListener('pointercancel', () => { gesture = null; });

    attachMinimap($('mini'));

    // ---- the controls ----

    function pointRow(kind, label, get, set) {
        const row = document.createElement('div');
        row.className = 'point';
        const name = document.createElement('div');
        name.className = 'point-name';
        name.id = kind + '-name';
        name.textContent = label;
        const controls = document.createElement('div');
        controls.className = 'row';

        const samples = document.createElement('input');
        samples.type = 'number';
        samples.className = 'samples';
        samples.id = kind + '-samples';
        samples.min = 0;
        samples.max = N;
        samples.title = 'Sample position. Typed values are exact and never snapped.';
        samples.addEventListener('change', () => {
            const value = parseInt(samples.value, 10);
            if (isNaN(value)) refreshControls();
            else set(value);
        });

        const time = document.createElement('input');
        time.type = 'text';
        time.className = 'time';
        time.id = kind + '-time';
        time.title = 'Time as m:ss.mmm, or seconds';
        time.addEventListener('change', () => {
            const seconds = parseTime(time.value);
            if (seconds === null) refreshControls();
            else set(seconds * RATE);
        });
        controls.append(samples, time);

        const button = (text, title, onClick) => {
            const b = document.createElement('button');
            b.className = 'small';
            b.textContent = text;
            b.title = title;
            b.addEventListener('click', onClick);
            return b;
        };
        for (const delta of [-1000, -100, -1, 1, 100, 1000]) {
            const text = (delta > 0 ? '+' : '−') + Math.abs(delta);
            controls.append(button(text, 'Move ' + Math.abs(delta) + ' samples ' + (delta > 0 ? 'later' : 'earlier'), () => set(get() + delta)));
        }
        controls.append(
            button('Zero crossing', 'Move to the nearest zero crossing', () => set(nearestZeroCrossing(get(), RATE))),
            button('To playhead', 'Move this point to the playhead', () => set(position() * RATE)),
        );
        row.append(name, controls);
        $('points').append(row);
    }

    pointRow('start', 'Start', () => start, setStart);
    pointRow('end', 'End', () => end, setEnd);

    function refreshControls() {
        const focused = document.activeElement;
        const put = (id, value) => { if (focused !== $(id)) $(id).value = value; };
        put('start-samples', start);
        put('end-samples', end);
        put('start-time', formatTime(start / RATE));
        put('end-time', formatTime(end / RATE));

        $('points').classList.toggle('disabled-group', !loopOn);
        $('preset-whole').disabled = false;
        $('hear-seam').disabled = !loopOn || !buffer;
        $('seam-section').style.display = loopOn ? '' : 'none';
        $('trim-row').style.display = loopOn && end < N ? '' : 'none';
        $('zoom-loop').disabled = $('zoom-start').disabled = $('zoom-end').disabled = !loopOn;
        $('play').textContent = source ? '⏸ Pause' : '▶ Play';
        $('play').disabled = !buffer;
        $('rewind').disabled = !buffer;
        $('apply').disabled = !buffer;
        $('apply').textContent = loopOn ? 'Use these loop points' : 'Use no loop';

        if (!loopOn) {
            $('summary').textContent = 'Plays once, with no loop.';
        } else {
            const cut = end < N && $('trim').checked;
            $('summary').textContent = 'Plays from the top, then repeats ' + formatTime((end - start) / RATE)
                + ' (' + (end - start).toLocaleString() + ' samples).'
                + (cut ? ' Cuts ' + formatTime((N - end) / RATE) + ' from the end.' : '');
        }
    }

    $('play').addEventListener('click', togglePlay);
    $('rewind').addEventListener('click', () => {
        stop();
        pausedPos = 0;
        refreshControls();
        requestDraw();
    });
    $('hear-seam').addEventListener('click', () => {
        const before = Math.min(2, end / RATE);
        play(end / RATE - before, before + 2);
    });
    $('zoom-fit').addEventListener('click', () => setView(0, N));
    $('zoom-loop').addEventListener('click', () => {
        const margin = (end - start) * 0.05;
        setView(start - margin, end + margin);
    });
    $('zoom-start').addEventListener('click', () => zoomAround(start));
    $('zoom-end').addEventListener('click', () => zoomAround(end));
    $('loop-on').addEventListener('change', () => {
        loopOn = $('loop-on').checked;
        changed();
    });
    $('trim').addEventListener('change', refreshControls);
    $('preset-whole').addEventListener('click', () => {
        loopOn = $('loop-on').checked = true;
        setLoop(0, N);
    });
    $('preset-file').addEventListener('click', () => {
        if (!data.fileLoop) return;
        loopOn = $('loop-on').checked = true;
        setLoop(data.fileLoop.start, data.fileLoop.end);
    });
    $('cancel').addEventListener('click', () => vscode.postMessage({ type: 'cancel' }));
    $('apply').addEventListener('click', () => {
        stop();
        vscode.postMessage(loopOn
            ? { type: 'apply', start, end, trimAfterLoop: end < N && $('trim').checked }
            : { type: 'apply', none: true });
    });

    document.addEventListener('keydown', (ev) => {
        const typing = ev.target.tagName === 'INPUT' && ev.target.type !== 'checkbox';
        if (ev.code === 'Space' && !typing) {
            ev.preventDefault();
            togglePlay();
        }
    });

    refreshControls();
    requestDraw();

    loadAudio(refreshControls);
</script>
</body>
</html>`;
}
