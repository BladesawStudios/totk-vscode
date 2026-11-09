import * as vscode from 'vscode';
import { isBntxFile } from './archives';
import { logger } from './logger';

/**
 * Expanding a BNTX that holds exactly one texture opens that texture, so there is no dropdown to click through for a
 * single entry. BNTXs with several textures expand as usual.
 */
export function registerSingleTextureAutoOpen<T extends { resourceUri: vscode.Uri }>(
    treeView: vscode.TreeView<T>,
): vscode.Disposable {
    return treeView.onDidExpandElement(async ({ element }) => {
        const uri = element.resourceUri;
        if (!isBntxFile(uri.fsPath)) {
            return;
        }
        try {
            const entries = await vscode.workspace.fs.readDirectory(uri);
            if (entries.length === 1 && entries[0][1] !== vscode.FileType.Directory) {
                const textureUri = vscode.Uri.joinPath(uri, entries[0][0]);
                await vscode.commands.executeCommand('totk-editor.openBntxTexture', textureUri);
            }
        } catch (error) {
            logger.debug(`BNTX auto-open skipped for ${uri.fsPath}: ${error instanceof Error ? error.message : String(error)}`);
        }
    });
}
