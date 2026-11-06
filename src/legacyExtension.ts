import * as vscode from 'vscode';
import { logger } from './logger';

/**
 * IDs TKVSC was published under before. VS Code sees each as a separate extension, so installing this one leaves the
 * old one running beside it, registering the same commands and views.
 */
const LEGACY_EXTENSION_IDS = ['TKVSC-Team.totk-vscode'];

const UNINSTALL_AND_RELOAD = 'Uninstall and Reload';

async function offerUninstall(id: string): Promise<void> {
    const choice = await vscode.window.showWarningMessage(
        `An older copy of TKVSC (${id}) is still installed and conflicts with this one. Uninstall it, along with any add-ons made for it.`,
        UNINSTALL_AND_RELOAD,
    );
    if (choice !== UNINSTALL_AND_RELOAD) {
        return;
    }
    try {
        await vscode.commands.executeCommand('workbench.extensions.uninstallExtension', id);
        await vscode.commands.executeCommand('workbench.action.reloadWindow');
    } catch (err) {
        // VS Code refuses while an installed add-on still depends on the old extension.
        logger.error(`Could not uninstall ${id}: ${String(err)}`);
        void vscode.window.showErrorMessage(
            `Could not uninstall ${id}. Uninstall its add-ons first from the Extensions view, then uninstall it.`,
        );
    }
}

/** Call first in `activate`: if the old copy registered the commands first, the rest of activation can fail. */
export function warnAboutLegacyExtensions(): void {
    for (const id of LEGACY_EXTENSION_IDS) {
        if (vscode.extensions.getExtension(id)) {
            logger.warn(`Older copy of TKVSC found: ${id}`);
            void offerUninstall(id);
        }
    }
}
