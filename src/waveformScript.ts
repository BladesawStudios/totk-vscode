/**
 * Webview code shared by the audio editors: loading a decoded WAV at its own sample rate and drawing and zooming its
 * waveform. It's inlined into each page's script after `const data = { url, rate, samples, ... }`, and calls that page's
 * `drawAll()`.
 */
export const WAVEFORM_SCRIPT = /* js */ `
    const N = data.samples, RATE = data.rate;
    const $ = (id) => document.getElementById(id);

    // Sample positions only stay exact if the context doesn't resample the audio on decode.
    const audioCtx = new AudioContext({ sampleRate: RATE });
    const BLOCK = 256;
    let buffer = null, mono = null, blockMin = null, blockMax = null, peak = 0, displayGain = 1;
    let viewFrom = 0, viewTo = N;

    function formatTime(seconds) {
        const m = Math.floor(seconds / 60);
        const s = seconds - m * 60;
        return m + ':' + (s < 10 ? '0' : '') + s.toFixed(3);
    }

    function parseTime(text) {
        const match = /^\\s*(?:(\\d+):)?(\\d*\\.?\\d+)\\s*$/.exec(text);
        return match ? (match[1] ? parseInt(match[1], 10) * 60 : 0) + parseFloat(match[2]) : null;
    }

    function color(name, fallback) {
        return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
    }

    function loadAudio(onLoaded) {
        fetch(data.url)
            .then((response) => response.arrayBuffer())
            .then((bytes) => audioCtx.decodeAudioData(bytes))
            .then((decoded) => {
                buffer = decoded;
                analyse();
                onLoaded();
                requestDraw();
            })
            .catch((e) => vscode.postMessage({ type: 'load-failed', error: String(e) }));
    }

    function analyse() {
        mono = new Float32Array(buffer.length);
        for (let c = 0; c < buffer.numberOfChannels; c++) {
            const channel = buffer.getChannelData(c);
            for (let i = 0; i < channel.length; i++) mono[i] += channel[i] / buffer.numberOfChannels;
        }
        const blocks = Math.ceil(mono.length / BLOCK);
        blockMin = new Float32Array(blocks);
        blockMax = new Float32Array(blocks);
        for (let b = 0; b < blocks; b++) {
            let lo = 1, hi = -1;
            const stop = Math.min(mono.length, (b + 1) * BLOCK);
            for (let i = b * BLOCK; i < stop; i++) {
                if (mono[i] < lo) lo = mono[i];
                if (mono[i] > hi) hi = mono[i];
            }
            blockMin[b] = lo;
            blockMax[b] = hi;
            peak = Math.max(peak, -lo, hi);
        }
        displayGain = 1 / Math.max(peak, 0.001);
    }

    function nearestZeroCrossing(sample, reach) {
        if (!mono) return sample;
        const crossesAt = (i) => i > 0 && i < mono.length && (mono[i - 1] < 0) !== (mono[i] < 0);
        for (let d = 0; d <= reach; d++) {
            if (crossesAt(sample - d)) return sample - d;
            if (crossesAt(sample + d)) return sample + d;
        }
        return sample;
    }

    // The largest jump across all channels when one sample is followed by another, as a share of full scale.
    function jumpBetween(lastSample, firstSample) {
        if (!buffer) return 0;
        let jump = 0;
        for (let c = 0; c < buffer.numberOfChannels; c++) {
            const d = buffer.getChannelData(c);
            jump = Math.max(jump, Math.abs(d[lastSample] - d[firstSample]));
        }
        return jump;
    }

    function jumpVerdict(jump) {
        const percent = jump * 100;
        return percent.toFixed(1) + '% of full scale (' + (percent < 2 ? 'smooth' : percent < 10 ? 'may click' : 'likely to click') + ')';
    }

    let drawQueued = false;
    function requestDraw() {
        if (drawQueued) return;
        drawQueued = true;
        requestAnimationFrame(() => {
            drawQueued = false;
            drawAll();
            if (source) requestDraw();
        });
    }

    function fitCanvas(canvas) {
        const rect = canvas.getBoundingClientRect();
        const scale = window.devicePixelRatio || 1;
        const w = Math.max(1, Math.round((rect.width - 2) * scale)), h = Math.max(1, Math.round((rect.height - 2) * scale));
        if (canvas.width !== w || canvas.height !== h) {
            canvas.width = w;
            canvas.height = h;
        }
        const g = canvas.getContext('2d');
        g.setTransform(scale, 0, 0, scale, 0, 0);
        return { g, w: w / scale, h: h / scale };
    }

    function drawWave(g, w, top, h, from, to, fill) {
        if (!mono) return;
        // Scaled to the clip's peak, or a quiet sound is a flat line.
        const mid = top + h / 2, amp = (h / 2 - 1) * displayGain;
        const perPixel = (to - from) / w;
        g.fillStyle = fill;
        g.strokeStyle = fill;
        if (perPixel >= 1) {
            for (let x = 0; x < w; x++) {
                const a = Math.max(0, Math.floor(from + x * perPixel));
                const b = Math.min(mono.length, Math.ceil(from + (x + 1) * perPixel));
                let lo = 1, hi = -1;
                if (perPixel >= BLOCK) {
                    const last = Math.min(blockMin.length - 1, Math.floor((b - 1) / BLOCK));
                    for (let k = Math.floor(a / BLOCK); k <= last; k++) {
                        if (blockMin[k] < lo) lo = blockMin[k];
                        if (blockMax[k] > hi) hi = blockMax[k];
                    }
                } else {
                    for (let i = a; i < b; i++) {
                        if (mono[i] < lo) lo = mono[i];
                        if (mono[i] > hi) hi = mono[i];
                    }
                }
                if (lo > hi) continue;
                g.fillRect(x, mid - hi * amp, 1, Math.max(1, (hi - lo) * amp));
            }
        } else {
            // Few enough samples to show each one.
            const first = Math.max(0, Math.floor(from)), last = Math.min(mono.length - 1, Math.ceil(to));
            g.beginPath();
            for (let i = first; i <= last; i++) {
                const x = (i - from) / perPixel, y = mid - mono[i] * amp;
                if (i === first) g.moveTo(x, y);
                else g.lineTo(x, y);
            }
            g.stroke();
            if (perPixel < 0.2) {
                for (let i = first; i <= last; i++) g.fillRect((i - from) / perPixel - 2, mid - mono[i] * amp - 2, 4, 4);
            }
        }
    }

    const RULER = 18;
    const TICKS = [0.0001, 0.0002, 0.0005, 0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600];

    function drawRuler(g, w, muted) {
        const seconds = (viewTo - viewFrom) / RATE;
        const step = TICKS.find((t) => t >= seconds / (w / 90)) || 1200;
        g.fillStyle = muted;
        g.font = '10px sans-serif';
        for (let t = Math.ceil(viewFrom / RATE / step) * step; t * RATE <= viewTo; t += step) {
            const x = (t * RATE - viewFrom) / (viewTo - viewFrom) * w;
            g.fillRect(x, RULER - 5, 1, 5);
            g.fillText(formatTime(t + 1e-9), x + 3, 11);
        }
    }

    function clampView() {
        const span = Math.max(32, Math.min(N, viewTo - viewFrom));
        viewFrom = Math.max(0, Math.min(N - span, viewFrom));
        viewTo = viewFrom + span;
    }

    function setView(from, to) {
        viewFrom = from;
        viewTo = to;
        clampView();
        requestDraw();
    }

    function zoomAround(sample) {
        const span = Math.max(32, Math.min(N, RATE * 0.05));
        setView(sample - span / 2, sample + span / 2);
    }

    // Scroll zooms around the pointer; shift or sideways scroll pans.
    function attachWheelZoom(canvas) {
        canvas.addEventListener('wheel', (ev) => {
            ev.preventDefault();
            const span = viewTo - viewFrom;
            if (ev.shiftKey || Math.abs(ev.deltaX) > Math.abs(ev.deltaY)) {
                const delta = (ev.deltaX || ev.deltaY) / canvas.clientWidth * span;
                setView(viewFrom + delta, viewTo + delta);
                return;
            }
            const rect = canvas.getBoundingClientRect();
            const anchor = viewFrom + (ev.clientX - rect.left - 1) / (rect.width - 2) * span;
            const nextSpan = Math.max(32, Math.min(N, span * Math.pow(1.0015, ev.deltaY)));
            const from = anchor - (anchor - viewFrom) / span * nextSpan;
            setView(from, from + nextSpan);
        }, { passive: false });
    }

    // The whole-clip strip: click or drag to move the view.
    function attachMinimap(canvas) {
        let dragging = false;
        const moveTo = (ev) => {
            const rect = canvas.getBoundingClientRect();
            const center = (ev.clientX - rect.left) / rect.width * N;
            const span = viewTo - viewFrom;
            setView(center - span / 2, center + span / 2);
        };
        canvas.addEventListener('pointerdown', (ev) => { canvas.setPointerCapture(ev.pointerId); dragging = true; moveTo(ev); });
        canvas.addEventListener('pointermove', (ev) => { if (dragging) moveTo(ev); });
        canvas.addEventListener('pointerup', () => { dragging = false; });
    }

    new ResizeObserver(requestDraw).observe(document.body);
`;
