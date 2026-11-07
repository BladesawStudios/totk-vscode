import * as path from 'path';
import * as vscode from 'vscode';
import type { BarsSectionInfo } from './bridge';
import { WAVEFORM_SCRIPT } from './waveformScript';

export interface SectionEditorSource {
    name: string;
    /** A 16-bit WAV of the source, decoded the way the replace will read it. */
    wavPath: string;
    sampleRate: number;
    channels: number;
    samples: number;
    sections: BarsSectionInfo[];
    hasTable: boolean;
}

export interface SectionEditorResult {
    updateTable: boolean;
    sections: { index: number; start: number; end: number; loop: boolean }[];
}

/** Opens the editor that cuts one source across several entries. Resolves with the cuts, or undefined when cancelled or closed. */
export function openSectionEditor(source: SectionEditorSource): Promise<SectionEditorResult | undefined> {
    const panel = vscode.window.createWebviewPanel(
        'totkSectionEditor',
        `Sections: ${source.name}`,
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
                resolve({ updateTable: !!message.updateTable, sections: message.sections });
                panel.dispose();
            } else if (message.type === 'cancel') {
                panel.dispose();
            } else if (message.type === 'load-failed') {
                void vscode.window.showErrorMessage(`The section editor could not load the audio: ${message.error}`);
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

function buildHtml(source: SectionEditorSource, audioUrl: string): string {
    const data = JSON.stringify({
        name: source.name,
        url: audioUrl,
        rate: source.sampleRate,
        channels: source.channels,
        samples: source.samples,
        sections: source.sections,
        hasTable: source.hasTable,
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
    button.small { padding: 2px 6px; font-size: 12px; }
    input[type=text] {
        font: inherit;
        font-variant-numeric: tabular-nums;
        width: 86px;
        color: var(--vscode-input-foreground, #ccc);
        background: var(--vscode-input-background, #3c3c3c);
        border: 1px solid var(--vscode-input-border, transparent);
        border-radius: 3px;
        padding: 2px 6px;
        user-select: text;
    }
    input:focus { outline: 1px solid var(--vscode-focusBorder, #007fd4); }
    label { display: inline-flex; align-items: center; gap: 5px; cursor: pointer; }
    .help { color: var(--vscode-descriptionForeground, #a0a0a0); font-size: 11px; }
    #rows { display: flex; flex-direction: column; gap: 6px; }
    .section-row {
        background: var(--vscode-editorWidget-background, #252526);
        border: 1px solid var(--vscode-panel-border, #444);
        border-left: 4px solid transparent;
        border-radius: 4px;
        padding: 6px 10px;
        display: flex;
        flex-direction: column;
        gap: 4px;
    }
    .section-name { font-weight: 600; word-break: break-all; }
    .note { font-size: 11px; color: var(--vscode-editorWarning-foreground, #cca700); }
    #table-box { display: flex; flex-direction: column; gap: 4px; }
</style>
</head>
<body>
    <div>
        <h1 id="title"></h1>
        <div class="sub" id="info"></div>
    </div>

    <canvas id="wave"></canvas>
    <canvas id="mini"></canvas>
    <div class="help">Scroll to zoom. Drag the waveform to pan, click it to move the playhead, drag a cut line to move it. Hold Alt while dragging a cut to skip snapping.</div>

    <div class="row">
        <button id="play" title="Space">▶ Play</button>
        <button id="rewind">⏮ Rewind</button>
        <span class="sub" id="position"></span>
        <span class="spacer"></span>
        <button id="zoom-fit">Fit</button>
    </div>

    <div class="row">
        <label title="Move a dragged cut to the nearest zero crossing, which helps avoid clicks"><input type="checkbox" id="snap" checked> Snap to zero crossings</label>
        <span class="spacer"></span>
        <button id="layout-original" title="Each section keeps the length the game gives it, from the start of the source">Original lengths</button>
        <button id="layout-fit" title="Keeps the sections' proportions and stretches the cuts to use the whole source">Spread across the source</button>
    </div>

    <div id="rows"></div>

    <div id="table-box"></div>

    <div class="row">
        <span class="sub" id="summary"></span>
        <span class="spacer"></span>
        <button id="cancel">Cancel</button>
        <button id="apply" class="primary" disabled></button>
    </div>

<script>
    const data = ${data};
    const vscode = acquireVsCodeApi();
${WAVEFORM_SCRIPT}

    // In playing order. A section runs from cuts[i] to cuts[i + 1], so they always join up in the source.
    const order = data.sections.map((s) => ({ ...s, loop: !!s.looped }));
    let cuts = [];
    const MIN_LENGTH = Math.max(1, Math.round(RATE * 0.01));
    let source = null, sources = [], segments = [], segmentsStart = 0, pausedPos = 0;

    // What the game plays of a section: its fixed length if it has one, else its whole file.
    const gameSeconds = (s) => (s.fixedSeconds !== null && s.fixedSeconds !== undefined ? s.fixedSeconds : s.seconds);

    // Names without what they all share, for labels on the waveform.
    const shortNames = (() => {
        const names = order.map((s) => s.name);
        let shared = names[0] || '';
        for (const name of names) while (!name.startsWith(shared)) shared = shared.slice(0, -1);
        return names.map((name) => name.slice(shared.length) || name);
    })();

    const shortName = (s) => shortNames[data.sections.findIndex((other) => other.index === s.index)];
    const COLORS = ['--vscode-charts-blue', '--vscode-charts-green', '--vscode-charts-orange', '--vscode-charts-purple', '--vscode-charts-yellow', '--vscode-charts-red'];
    const FALLBACKS = ['#3794ff', '#89d185', '#d18616', '#b180d7', '#cca700', '#f14c4c'];
    const sectionColor = (i) => color(COLORS[i % COLORS.length], FALLBACKS[i % FALLBACKS.length]);

    $('title').textContent = data.name;
    $('info').textContent = N.toLocaleString() + ' samples, ' + formatTime(N / RATE) + ', '
        + RATE.toLocaleString() + ' Hz, ' + data.channels + (data.channels === 1 ? ' channel' : ' channels')
        + ', cut into ' + order.length + ' sections';

    // ---- the cuts ----

    // Keeps every section at least the minimum long and everything inside the source.
    function settleCuts() {
        const n = order.length;
        cuts[0] = Math.max(0, Math.min(N - n * MIN_LENGTH, cuts[0]));
        for (let i = 1; i <= n; i++) cuts[i] = Math.max(cuts[i - 1] + MIN_LENGTH, cuts[i]);
        cuts[n] = Math.min(N, cuts[n]);
        for (let i = n - 1; i >= 0; i--) cuts[i] = Math.min(cuts[i + 1] - MIN_LENGTH, cuts[i]);
    }

    function layoutOriginal() {
        let at = 0;
        cuts = [0];
        for (const s of order) {
            at += Math.round(gameSeconds(s) * RATE);
            cuts.push(at);
        }
        settleCuts();
        changed();
    }

    function layoutFit() {
        const total = order.reduce((sum, s) => sum + Math.max(gameSeconds(s), 0.001), 0);
        let at = 0;
        cuts = [0];
        for (const s of order) {
            at += Math.max(gameSeconds(s), 0.001);
            cuts.push(Math.round(at / total * N));
        }
        cuts[order.length] = N;
        settleCuts();
        changed();
    }

    // Moves just the one cut, so only its two neighbours change length.
    function moveCut(i, value) {
        const low = i > 0 ? cuts[i - 1] + MIN_LENGTH : 0;
        const high = i < order.length ? cuts[i + 1] - MIN_LENGTH : N;
        cuts[i] = Math.max(low, Math.min(high, Math.round(value)));
        changed();
    }

    // Sets a section's length and shifts the ones after it, which keeps their lengths. The last section takes up the
    // difference, so the end of the group stays put unless it has to move to fit.
    function setLength(i, samples) {
        const last = order.length - 1;
        const wanted = Math.max(MIN_LENGTH, Math.round(samples));
        const change = wanted - (cuts[i + 1] - cuts[i]);
        if (i === last) {
            cuts[last + 1] += Math.min(change, N - cuts[last + 1]);
        } else {
            const delta = Math.min(change, N - MIN_LENGTH - cuts[last]);
            for (let k = i + 1; k <= last; k++) cuts[k] += delta;
            cuts[last + 1] = Math.max(cuts[last + 1], cuts[last] + MIN_LENGTH);
        }
        settleCuts();
        changed();
    }

    function changed() {
        refreshRows();
        refreshSummary();
        requestDraw();
    }

    // ---- playback: a list of source ranges played back to back ----

    function position() {
        if (!source) return pausedPos;
        let elapsed = audioCtx.currentTime - segmentsStart;
        for (const seg of segments) {
            const length = (seg.to - seg.from) / RATE;
            if (elapsed < length) return (seg.from / RATE) + elapsed;
            elapsed -= length;
        }
        const last = segments[segments.length - 1];
        return last ? last.to / RATE : pausedPos;
    }

    function stop() {
        if (!source) return;
        pausedPos = position();
        source = null;
        for (const node of sources) {
            try { node.stop(); } catch { /* already ended */ }
        }
        sources = [];
        refreshPlay();
    }

    function playSegments(list) {
        if (!buffer) return;
        stop();
        audioCtx.resume();
        segments = list;
        segmentsStart = audioCtx.currentTime + 0.02;
        let when = segmentsStart;
        sources = list.map((seg) => {
            const node = audioCtx.createBufferSource();
            node.buffer = buffer;
            node.connect(audioCtx.destination);
            const length = (seg.to - seg.from) / RATE;
            node.start(when, seg.from / RATE, length);
            when += length;
            return node;
        });
        const lastNode = sources[sources.length - 1];
        source = lastNode;
        lastNode.onended = () => {
            if (source !== lastNode) return;
            pausedPos = segments[segments.length - 1].to / RATE;
            source = null;
            sources = [];
            refreshPlay();
            requestDraw();
        };
        refreshPlay();
        requestDraw();
    }

    function togglePlay() {
        if (source) {
            stop();
            requestDraw();
        } else {
            const from = pausedPos >= N / RATE ? 0 : pausedPos;
            playSegments([{ from: Math.round(from * RATE), to: N }]);
        }
    }

    function seek(seconds) {
        pausedPos = Math.max(0, Math.min(N / RATE, seconds));
        if (source) playSegments([{ from: Math.round(pausedPos * RATE), to: N }]);
        else requestDraw();
    }

    const JOIN_SECONDS = 2;

    // The end of one section into the start of whichever the game plays next.
    function hearJoin(i, nextIndex) {
        const reach = Math.round(JOIN_SECONDS * RATE);
        const end = cuts[i + 1], start = cuts[nextIndex];
        playSegments([
            { from: Math.max(cuts[i], end - reach), to: end },
            { from: start, to: Math.min(cuts[nextIndex + 1], start + reach) },
        ]);
    }

    // ---- drawing ----

    function drawAll() {
        const fg = color('--vscode-foreground', '#ccc');
        const muted = color('--vscode-descriptionForeground', '#a0a0a0');
        const dim = color('--vscode-editor-background', '#1e1e1e');
        const waveColor = color('--vscode-charts-blue', '#3794ff');
        const pos = position() * RATE;

        {
            const { g, w, h } = fitCanvas($('wave'));
            g.clearRect(0, 0, w, h);
            const toX = (s) => (s - viewFrom) / (viewTo - viewFrom) * w;

            for (let i = 0; i < order.length; i++) {
                g.globalAlpha = 0.16;
                g.fillStyle = sectionColor(i);
                g.fillRect(toX(cuts[i]), RULER, toX(cuts[i + 1]) - toX(cuts[i]), h - RULER);
            }
            g.globalAlpha = 1;
            drawWave(g, w, RULER, h - RULER, viewFrom, viewTo, waveColor);

            // Audio no section uses.
            g.globalAlpha = 0.65;
            g.fillStyle = dim;
            g.fillRect(0, RULER, toX(cuts[0]), h - RULER);
            g.fillRect(toX(cuts[order.length]), RULER, w - toX(cuts[order.length]), h - RULER);
            g.globalAlpha = 1;

            drawRuler(g, w, muted);
            g.font = 'bold 11px sans-serif';
            for (let i = 0; i < order.length; i++) {
                const left = Math.max(0, toX(cuts[i])), right = Math.min(w, toX(cuts[i + 1]));
                if (right - left < 24) continue;
                g.save();
                g.beginPath();
                g.rect(left, RULER, right - left, 18);
                g.clip();
                g.fillStyle = sectionColor(i);
                g.fillText(shortName(order[i]), left + 6, RULER + 13);
                g.restore();
            }
            for (let i = 0; i <= order.length; i++) {
                g.fillStyle = fg;
                g.fillRect(toX(cuts[i]) - 1, RULER, 2, h - RULER);
            }
            g.fillStyle = fg;
            g.globalAlpha = 0.7;
            g.fillRect(toX(pos), RULER, 1, h - RULER);
            g.globalAlpha = 1;
        }

        {
            const { g, w, h } = fitCanvas($('mini'));
            g.clearRect(0, 0, w, h);
            drawWave(g, w, 0, h, 0, N, muted);
            for (let i = 0; i < order.length; i++) {
                g.globalAlpha = 0.25;
                g.fillStyle = sectionColor(i);
                g.fillRect(cuts[i] / N * w, 0, (cuts[i + 1] - cuts[i]) / N * w, h);
            }
            g.globalAlpha = 0.2;
            g.fillStyle = fg;
            g.fillRect(viewFrom / N * w, 0, Math.max(2, (viewTo - viewFrom) / N * w), h);
            g.globalAlpha = 1;
            g.fillStyle = fg;
            g.fillRect(pos / N * w, 0, 1, h);
        }

        $('position').textContent = formatTime(pos / RATE) + ' (sample ' + Math.round(pos).toLocaleString() + ')';
    }

    // ---- the waveform ----

    const wave = $('wave');
    attachWheelZoom(wave);
    attachMinimap($('mini'));
    const sampleAt = (ev) => {
        const rect = wave.getBoundingClientRect();
        return viewFrom + (ev.clientX - rect.left - 1) / (rect.width - 2) * (viewTo - viewFrom);
    };
    const xOf = (sample) => (sample - viewFrom) / (viewTo - viewFrom) * (wave.getBoundingClientRect().width - 2);

    function cutAt(ev) {
        const x = ev.clientX - wave.getBoundingClientRect().left - 1;
        let best = -1, bestDistance = 7;
        for (let i = 0; i < cuts.length; i++) {
            const distance = Math.abs(x - xOf(cuts[i]));
            if (distance < bestDistance) {
                best = i;
                bestDistance = distance;
            }
        }
        return best;
    }

    let gesture = null;
    wave.addEventListener('pointerdown', (ev) => {
        wave.setPointerCapture(ev.pointerId);
        const cut = cutAt(ev);
        gesture = cut >= 0
            ? { kind: 'cut', cut }
            : { kind: 'pan', x: ev.clientX, from: viewFrom, to: viewTo, moved: false, sample: sampleAt(ev) };
    });
    wave.addEventListener('pointermove', (ev) => {
        if (!gesture) {
            wave.style.cursor = cutAt(ev) >= 0 ? 'ew-resize' : 'grab';
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
        moveCut(gesture.cut, sample);
    });
    wave.addEventListener('pointerup', () => {
        if (gesture && gesture.kind === 'pan' && !gesture.moved) seek(gesture.sample / RATE);
        gesture = null;
    });
    wave.addEventListener('pointercancel', () => { gesture = null; });

    // ---- the section rows ----

    const nextOf = (i) => (order[i].next ? order.findIndex((s) => s.name === order[i].next) : -1);
    const tableUsed = order.some((s) => s.inTable);

    function buildRows() {
        const rows = $('rows');
        rows.textContent = '';
        order.forEach((s, i) => {
            const row = document.createElement('div');
            row.className = 'section-row';
            row.style.borderLeftColor = sectionColor(i);

            const top = document.createElement('div');
            top.className = 'row';
            const button = (text, title, onClick, disabled) => {
                const b = document.createElement('button');
                b.className = 'small';
                b.textContent = text;
                b.title = title;
                b.disabled = !!disabled;
                b.addEventListener('click', onClick);
                return b;
            };
            top.append(
                button('▲', 'Play this section earlier in the source', () => swap(i, i - 1), i === 0),
                button('▼', 'Play this section later in the source', () => swap(i, i + 1), i === order.length - 1),
            );
            const name = document.createElement('span');
            name.className = 'section-name';
            name.textContent = s.name;
            top.append(name);
            const spacer = document.createElement('span');
            spacer.className = 'spacer';
            top.append(spacer);

            const length = document.createElement('input');
            length.type = 'text';
            length.id = 'length-' + i;
            length.title = 'Length as m:ss.mmm, or seconds. Later sections move to make room.';
            length.addEventListener('change', () => {
                const seconds = parseTime(length.value);
                if (seconds === null) refreshRows();
                else setLength(i, seconds * RATE);
            });
            top.append(length);

            const was = document.createElement('span');
            was.className = 'sub';
            was.id = 'was-' + i;
            top.append(was);
            top.append(button('Original', 'Set this section back to the length the game gives it', () => setLength(i, gameSeconds(s) * RATE)));
            top.append(button('▶', 'Play this section', () => playSegments([{ from: cuts[i], to: cuts[i + 1] }])));
            const next = nextOf(i);
            if (next >= 0) {
                top.append(button('Hear join', 'Plays the end of this section into ' + order[next].name + ', which the game plays next', () => hearJoin(i, next)));
            }
            const loop = document.createElement('label');
            loop.title = 'Loop this section from its start to its end';
            const loopBox = document.createElement('input');
            loopBox.type = 'checkbox';
            loopBox.checked = s.loop;
            loopBox.addEventListener('change', () => { s.loop = loopBox.checked; });
            loop.append(loopBox, 'Loop');
            top.append(loop);
            row.append(top);

            const note = document.createElement('div');
            note.className = 'note';
            note.id = 'note-' + i;
            row.append(note);
            rows.append(row);
        });
    }

    // Reordering swaps which part of the source each section gets, and keeps the cuts where they are.
    function swap(a, b) {
        [order[a], order[b]] = [order[b], order[a]];
        buildRows();
        changed();
    }

    function refreshRows() {
        order.forEach((s, i) => {
            const samples = cuts[i + 1] - cuts[i];
            const field = $('length-' + i);
            if (document.activeElement !== field) field.value = formatTime(samples / RATE);
            $('was-' + i).textContent = 'was ' + formatTime(gameSeconds(s));

            const notes = [];
            if (samples < RATE * 0.25) notes.push('This section is very short.');
            if (data.hasTable && tableUsed && !s.inTable) notes.push('The music table never plays this track, so replacing it may not be heard in the game.');
            const next = nextOf(i);
            if (next >= 0 && cuts[i + 1] !== cuts[next]) {
                notes.push('Into ' + order[next].name + ': jump of ' + jumpVerdict(jumpBetween(cuts[i + 1] - 1, cuts[next])) + '.');
            }
            $('note-' + i).textContent = notes.join(' ');
        });
    }

    // ---- the music table ----

    const fixedSections = order.filter((s) => s.fixedSeconds !== null && s.fixedSeconds !== undefined);

    function buildTableBox() {
        const box = $('table-box');
        if (!data.hasTable) {
            const note = document.createElement('div');
            note.className = 'help';
            note.textContent = 'No music table found, so these are in name order. Use the arrows if that is not how they play.';
            box.append(note);
            return;
        }
        if (fixedSections.length === 0) return;
        const label = document.createElement('label');
        const checkbox = document.createElement('input');
        checkbox.type = 'checkbox';
        checkbox.id = 'update-table';
        checkbox.checked = true;
        checkbox.addEventListener('change', refreshSummary);
        label.id = 'update-table-label';
        label.append(checkbox, ' Let fixed-length sections play their whole file (updates the music table in your mod)');
        const help = document.createElement('div');
        help.className = 'help';
        help.textContent = 'Fixed by the table: ' + fixedSections.map((s) => s.name + ' at ' + formatTime(s.fixedSeconds)).join(', ') + '.';
        const status = document.createElement('div');
        status.className = 'help';
        status.id = 'table-status';
        box.append(label, help, status);
    }

    // The table only has to change if a fixed section's new length differs from what it sets.
    function fixedLengthsDiffer() {
        return order.some((s, i) => s.fixedSeconds !== null && s.fixedSeconds !== undefined
            && Math.abs((cuts[i + 1] - cuts[i]) - s.fixedSeconds * RATE) > 1);
    }

    function refreshTableBox() {
        const label = $('update-table-label');
        if (!label) return;
        const differ = fixedLengthsDiffer();
        label.style.display = differ ? '' : 'none';
        $('table-status').textContent = !differ
            ? 'These sections match the lengths the table sets, so the table stays as it is.'
            : $('update-table').checked
                ? 'A fixed section has a new length, so your mod gets its own copy of the table.'
                : 'Unticked, the game moves on from a fixed section at its old time, whatever the new length is.';
    }

    function refreshSummary() {
        refreshTableBox();
        const used = cuts[order.length] - cuts[0];
        const unused = N - used;
        $('summary').textContent = 'Uses ' + formatTime(used / RATE) + ' of ' + formatTime(N / RATE)
            + (unused > 0 ? ', leaving out ' + formatTime(unused / RATE) : '') + '.';
    }

    function refreshPlay() {
        $('play').textContent = source ? '⏸ Pause' : '▶ Play';
        $('play').disabled = !buffer;
        $('rewind').disabled = !buffer;
        $('apply').disabled = !buffer;
    }

    $('apply').textContent = 'Replace ' + order.length + ' tracks';
    $('play').addEventListener('click', togglePlay);
    $('rewind').addEventListener('click', () => {
        stop();
        pausedPos = 0;
        requestDraw();
    });
    $('zoom-fit').addEventListener('click', () => setView(0, N));
    $('layout-original').addEventListener('click', layoutOriginal);
    $('layout-fit').addEventListener('click', layoutFit);
    $('cancel').addEventListener('click', () => vscode.postMessage({ type: 'cancel' }));
    $('apply').addEventListener('click', () => {
        stop();
        const box = $('update-table');
        vscode.postMessage({
            type: 'apply',
            updateTable: !!box && box.checked && fixedLengthsDiffer(),
            sections: order.map((s, i) => ({ index: s.index, start: cuts[i], end: cuts[i + 1], loop: s.loop })),
        });
    });
    document.addEventListener('keydown', (ev) => {
        const typing = ev.target.tagName === 'INPUT' && ev.target.type !== 'checkbox';
        if (ev.code === 'Space' && !typing) {
            ev.preventDefault();
            togglePlay();
        }
    });

    buildRows();
    buildTableBox();
    const gameTotal = order.reduce((sum, s) => sum + gameSeconds(s), 0) * RATE;
    // Squeezing the original lengths into a shorter source would leave the last sections almost empty.
    if (gameTotal > N) {
        $('layout-original').disabled = true;
        $('layout-original').title = 'This source is ' + formatTime((gameTotal - N) / RATE) + ' too short for the lengths the game gives these sections';
    }
    // Close to the game's total, its own lengths fit as they are; otherwise spread them out so all of the source gets used.
    if (gameTotal > 0 && gameTotal <= N && (N - gameTotal) / N < 0.01) layoutOriginal();
    else layoutFit();
    refreshPlay();
    requestDraw();
    loadAudio(() => {
        refreshRows();
        refreshPlay();
    });
</script>
</body>
</html>`;
}
