import * as vscode from 'vscode';
import * as path from 'path';
import { getBwavViewerHtml } from './bwavViewer';
import { runBridgeJsonAsync } from './bridge';
import { getCachedPythonExecutable } from './pythonEnv';
import { getDiskArchivePath, getLocatorInsideDiskArchive, isPathInsideArchive } from './archives';

export class BwavEditorProvider implements vscode.CustomReadonlyEditorProvider {
    public static readonly viewType = 'totk-editor.bwavViewer';

    constructor(
        private readonly context: vscode.ExtensionContext
    ) {}

    public static register(context: vscode.ExtensionContext): vscode.Disposable {
        const provider = new BwavEditorProvider(context);
        return vscode.window.registerCustomEditorProvider(BwavEditorProvider.viewType, provider, {
            webviewOptions: {
                retainContextWhenHidden: true
            }
        });
    }

    public async openCustomDocument(
        uri: vscode.Uri,
        openContext: vscode.CustomDocumentOpenContext,
        token: vscode.CancellationToken
    ): Promise<vscode.CustomDocument> {
        return {
            uri,
            dispose: () => {}
        };
    }

    public async resolveCustomEditor(
        document: vscode.CustomDocument,
        webviewPanel: vscode.WebviewPanel,
        _token: vscode.CancellationToken
    ): Promise<void> {
        webviewPanel.webview.options = {
            enableScripts: true,
            localResourceRoots: [
                vscode.Uri.file(require('os').tmpdir()),
                this.context.extensionUri,
                ...(vscode.workspace.workspaceFolders ? vscode.workspace.workspaceFolders.map(f => f.uri) : [])
            ]
        };

        const bwavName = path.basename(document.uri.fsPath);
        webviewPanel.webview.html = getBwavViewerHtml(webviewPanel.webview, bwavName, this.context.extensionUri);

        webviewPanel.webview.onDidReceiveMessage(async (message) => {
            if (message.type === 'fetch-audio') {
                try {
                    const python = getCachedPythonExecutable();
                    if (!python) {
                        throw new Error("Python executable not found.");
                    }
                    const bridgePath = path.join(this.context.extensionPath, 'python', 'totk_bridge.py');

                    const isInsideArchive = isPathInsideArchive(document.uri.fsPath);
                    const diskArchive = isInsideArchive ? getDiskArchivePath(document.uri.fsPath) : document.uri.fsPath;
                    const internalPath = isInsideArchive ? getLocatorInsideDiskArchive(document.uri.fsPath, diskArchive) : "";

                    const res = await runBridgeJsonAsync<any>(
                        python,
                        bridgePath,
                        ['read-bwav-audio', diskArchive, internalPath]
                    );

                    if (res && res.wavPath) {
                        const uri = webviewPanel.webview.asWebviewUri(vscode.Uri.file(res.wavPath));
                        webviewPanel.webview.postMessage({ type: 'audio-loaded', url: uri.toString(), result: res });
                    } else {
                        void vscode.window.showErrorMessage('Failed to decode BWAV audio: ' + (res?.error || 'Unknown error'));
                        webviewPanel.webview.postMessage({ type: 'audio-error' });
                    }
                } catch (e) {
                    const msg = e instanceof Error ? e.message : String(e);
                    void vscode.window.showErrorMessage(`Error fetching BWAV audio: ${msg}`);
                    webviewPanel.webview.postMessage({ type: 'audio-error' });
                }
            }
        });
    }
}
