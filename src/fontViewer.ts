import * as vscode from 'vscode';

export class FontViewerProvider implements vscode.CustomReadonlyEditorProvider {
    public static register(context: vscode.ExtensionContext, getRawBytes?: (uri: vscode.Uri) => Promise<Uint8Array>): vscode.Disposable {
        const provider = new FontViewerProvider(context, getRawBytes);
        return vscode.window.registerCustomEditorProvider(FontViewerProvider.viewType, provider, {
            webviewOptions: {
                retainContextWhenHidden: true,
            }
        });
    }

    private static readonly viewType = 'totk-editor.fontViewer';

    constructor(
        private readonly context: vscode.ExtensionContext,
        private readonly getRawBytes?: (uri: vscode.Uri) => Promise<Uint8Array>
    ) { }

    async openCustomDocument(uri: vscode.Uri, openContext: vscode.CustomDocumentOpenContext, token: vscode.CancellationToken): Promise<vscode.CustomDocument> {
        return { uri, dispose: () => { } };
    }

    async resolveCustomEditor(document: vscode.CustomDocument, webviewPanel: vscode.WebviewPanel, token: vscode.CancellationToken): Promise<void> {
        webviewPanel.webview.options = { enableScripts: true };
        
        try {
            let fileData: Uint8Array;
            if (this.getRawBytes) {
                fileData = await this.getRawBytes(document.uri);
            } else {
                fileData = await vscode.workspace.fs.readFile(document.uri);
            }
            
            const base64Font = Buffer.from(fileData).toString('base64');
            const fileName = document.uri.path.split('/').pop() || 'font';

            let uniqueUnicodes: number[] = [];
            try {
                const opentype = require('opentype.js');
                const buffer = fileData.buffer.slice(fileData.byteOffset, fileData.byteOffset + fileData.byteLength);
                const font = opentype.parse(buffer);
                
                const unicodes = new Set<number>();
                for (let i = 0; i < font.glyphs.length; i++) {
                    const glyph = font.glyphs.get(i);
                    if (glyph.unicode !== undefined) {
                        unicodes.add(glyph.unicode);
                    }
                    if (glyph.unicodes && glyph.unicodes.length > 0) {
                        for (const u of glyph.unicodes) {
                            unicodes.add(u);
                        }
                    }
                }
                uniqueUnicodes = Array.from(unicodes).sort((a, b) => a - b);
            } catch (err) {
                console.warn('opentype.js failed to parse font for unicode extraction:', err);
            }

            webviewPanel.webview.html = this.getHtmlForWebview(webviewPanel.webview, base64Font, fileName, uniqueUnicodes);
        } catch (e) {
            webviewPanel.webview.html = `<body><h3>Error loading font: ${String(e)}</h3></body>`;
        }
    }

    private getHtmlForWebview(webview: vscode.Webview, base64Font: string, fileName: string, unicodes: number[]): string {
        const unicodesJson = JSON.stringify(unicodes);
        return `<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <style>
        @font-face {
            font-family: 'PreviewFont';
            src: url(data:font/otf;base64,${base64Font}) format('opentype'),
                 url(data:font/truetype;base64,${base64Font}) format('truetype');
        }
        * { box-sizing: border-box; }
        body {
            font-family: var(--vscode-font-family, 'Segoe UI', sans-serif);
            color: var(--vscode-foreground);
            background: var(--vscode-editor-background);
            padding: 20px;
            margin: 0;
        }
        .container {
            max-width: 900px;
            margin: 0 auto;
        }
        .header {
            margin-bottom: 20px;
            padding-bottom: 10px;
            border-bottom: 1px solid var(--vscode-panel-border);
            display: flex;
            align-items: baseline;
            justify-content: space-between;
        }
        h1 { font-size: 24px; margin: 0 0 5px 0; }
        .info { color: var(--vscode-descriptionForeground); font-size: 13px; }
        
        .preview-area {
            font-family: 'PreviewFont';
            font-size: 48px;
            width: 100%;
            min-height: 150px;
            background: var(--vscode-input-background);
            color: var(--vscode-input-foreground);
            border: 1px solid var(--vscode-input-border);
            padding: 15px;
            border-radius: 4px;
            resize: vertical;
            outline: none;
            overflow-y: auto;
            white-space: pre-wrap;
            word-wrap: break-word;
        }
        .preview-area:focus {
            border-color: var(--vscode-focusBorder);
        }
        .controls {
            margin-top: 15px;
            display: flex;
            gap: 25px;
            align-items: center;
            background: var(--vscode-editorWidget-background, #252526);
            padding: 10px 15px;
            border-radius: 4px;
            border: 1px solid var(--vscode-panel-border);
        }
        .control-group {
            display: flex;
            align-items: center;
            gap: 10px;
        }
        .control-group label {
            font-size: 12px;
            font-weight: 600;
            color: var(--vscode-descriptionForeground);
            text-transform: uppercase;
        }
        input[type=range] {
            cursor: pointer;
            accent-color: #d46635;
        }
        .value-display {
            font-size: 13px;
            font-variant-numeric: tabular-nums;
            min-width: 40px;
        }

        h2 { 
            margin-top: 35px; 
            font-size: 18px; 
            font-weight: 600;
            border-bottom: 1px solid var(--vscode-panel-border);
            padding-bottom: 5px;
        }
        
        .glyph-grid {
            display: grid;
            grid-template-columns: repeat(auto-fill, minmax(60px, 1fr));
            gap: 8px;
            margin-top: 15px;
        }
        .glyph-item {
            display: flex;
            flex-direction: column;
            align-items: center;
            background: var(--vscode-editorWidget-background, #252526);
            border: 1px solid var(--vscode-panel-border);
            border-radius: 4px;
            padding: 10px 5px;
            cursor: pointer;
            transition: background 0.1s;
        }
        .glyph-item:hover {
            background: var(--vscode-list-hoverBackground);
        }
        .glyph-char {
            font-family: 'PreviewFont';
            font-size: 28px;
            margin-bottom: 8px;
            min-height: 35px;
            display: flex;
            align-items: center;
            justify-content: center;
        }
        .glyph-hex {
            font-size: 10px;
            color: var(--vscode-descriptionForeground);
            font-family: monospace;
        }
    </style>
</head>
<body>
    <div class="container">
        <div class="header">
            <div>
                <h1>${fileName}</h1>
                <div class="info">TOTK Font Viewer</div>
            </div>
            <div class="info">Standard TrueType / OpenType Extracted</div>
        </div>
        
        <div style="margin-bottom: 8px; font-size: 13px; font-weight: 600; color: var(--vscode-descriptionForeground); text-transform: uppercase;">
            Live Preview
        </div>
        <textarea class="preview-area" id="previewText" spellcheck="false">The quick brown fox jumps over the lazy dog.
0123456789
!@#$%^&*()_+-=[]{}|;':",./<>?</textarea>
        
        <div class="controls">
            <div class="control-group">
                <label>Size</label>
                <input type="range" min="8" max="144" value="48" id="sizeSlider">
                <span class="value-display" id="sizeValue">48px</span>
            </div>
            <div class="control-group">
                <label>Spacing</label>
                <input type="range" min="-5" max="20" value="0" id="spacingSlider">
                <span class="value-display" id="spacingValue">0px</span>
            </div>
            <div class="control-group">
                <label>Line Height</label>
                <input type="range" min="0.5" max="3" step="0.1" value="1.2" id="lineHeightSlider">
                <span class="value-display" id="lineHeightValue">1.2</span>
            </div>
        </div>

        <h2>Basic Latin & Latin-1 Supplement</h2>
        <div class="glyph-grid" id="glyphGrid"></div>
    </div>

    <script>
        const preview = document.getElementById('previewText');
        const sizeSlider = document.getElementById('sizeSlider');
        const sizeValue = document.getElementById('sizeValue');
        const spacingSlider = document.getElementById('spacingSlider');
        const spacingValue = document.getElementById('spacingValue');
        const lineHeightSlider = document.getElementById('lineHeightSlider');
        const lineHeightValue = document.getElementById('lineHeightValue');

        sizeSlider.addEventListener('input', (e) => {
            preview.style.fontSize = e.target.value + 'px';
            sizeValue.textContent = e.target.value + 'px';
        });

        spacingSlider.addEventListener('input', (e) => {
            preview.style.letterSpacing = e.target.value + 'px';
            spacingValue.textContent = e.target.value + 'px';
        });

        lineHeightSlider.addEventListener('input', (e) => {
            preview.style.lineHeight = e.target.value;
            lineHeightValue.textContent = e.target.value;
        });

        const grid = document.getElementById('glyphGrid');
        
        const availableUnicodes = ${unicodesJson};

        // If the font doesn't provide unicodes (or opentype failed), fallback to basic latin
        if (!availableUnicodes || availableUnicodes.length === 0) {
            for (let i = 33; i <= 126; i++) availableUnicodes.push(i);
            for (let i = 161; i <= 255; i++) availableUnicodes.push(i);
            document.querySelector('h2').textContent = "Basic Latin & Latin-1 Supplement (Fallback)";
        } else {
            document.querySelector('h2').textContent = "Supported Glyphs (" + availableUnicodes.length + " total)";
        }

        // Limit to max 2500 glyphs to avoid freezing the webview
        const maxGlyphs = 2500;
        const displayUnicodes = availableUnicodes.slice(0, maxGlyphs);

        // Populate the live preview with the first few printable characters if they aren't basic latin
        if (availableUnicodes.length > 0 && availableUnicodes[0] > 255) {
            let sampleText = "";
            for(let i = 0; i < Math.min(30, availableUnicodes.length); i++) {
                sampleText += String.fromCodePoint(availableUnicodes[i]) + " ";
            }
            preview.value = "Sample Glyphs:\\n" + sampleText;
        }

        for (const code of displayUnicodes) {
            const char = String.fromCodePoint(code);
            if (char.trim() === '' && code !== 32 && code !== 160) continue;
            
            const div = document.createElement('div');
            div.className = 'glyph-item';
            div.title = "Unicode: U+" + code.toString(16).padStart(4, '0').toUpperCase() + "\\nSystem Font: " + char;
            
            const charDiv = document.createElement('div');
            charDiv.className = 'glyph-char';
            charDiv.textContent = char;
            
            const hexDiv = document.createElement('div');
            hexDiv.className = 'glyph-hex';
            hexDiv.textContent = "U+" + code.toString(16).padStart(4, '0').toUpperCase();
            
            div.appendChild(charDiv);
            div.appendChild(hexDiv);
            grid.appendChild(div);
        }

        if (availableUnicodes.length > maxGlyphs) {
            const warning = document.createElement('div');
            warning.style.gridColumn = "1 / -1";
            warning.style.marginTop = "10px";
            warning.style.color = "var(--vscode-descriptionForeground)";
            warning.textContent = "Showing first " + maxGlyphs + " of " + availableUnicodes.length + " glyphs for performance.";
            grid.appendChild(warning);
        }
    </script>
</body>
</html>`;
    }
}
