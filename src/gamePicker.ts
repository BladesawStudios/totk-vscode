import * as vscode from 'vscode';
import { getActiveGameId, getActiveGameProfile, getGameProfileRegistry } from './gameProfile';
import type { ArchiveTreeProvider } from './archiveTree';
import { getArchiveTreeView } from './archiveTree';
import { getDumpTreeView, type GameDumpTreeProvider } from './dumpTree';

export function updateActiveGameViewDescriptions(): void {
    const displayName = getActiveGameProfile().displayName;
    const archivesView = getArchiveTreeView();
    if (archivesView) {
        archivesView.description = displayName;
    }
    const dumpView = getDumpTreeView();
    if (dumpView) {
        dumpView.description = displayName;
    }
}

export async function selectActiveGame(): Promise<void> {
    const registry = getGameProfileRegistry();
    const activeId = getActiveGameId();
    const picks = registry.getAllProfiles().map((profile) => ({
        label: profile.displayName,
        description: profile.id,
        detail: profile.id === activeId ? 'Currently active' : undefined,
        profileId: profile.id,
    }));

    if (picks.length === 0) {
        void vscode.window.showWarningMessage('TKVSC: No game profiles are registered.');
        return;
    }

    const selected = await vscode.window.showQuickPick(picks, {
        title: 'Select Active Game',
        placeHolder: 'Switch the active game profile',
        ignoreFocusOut: true,
    });
    if (!selected || selected.profileId === activeId) {
        return;
    }

    await vscode.workspace
        .getConfiguration('TKVSC')
        .update('activeGameId', selected.profileId, vscode.ConfigurationTarget.Global);
}

export function registerGamePicker(
    context: vscode.ExtensionContext,
    archiveTree: ArchiveTreeProvider,
    gameDumpTree: GameDumpTreeProvider,
): void {
    context.subscriptions.push(
        vscode.commands.registerCommand('totk-editor.selectActiveGame', () => selectActiveGame()),
    );

    const refreshForActiveGame = (): void => {
        updateActiveGameViewDescriptions();
        archiveTree.onActiveGameChanged();
        // Force RomFS tree + search cache to the newly active game's dump path.
        gameDumpTree.onRomfsPathChanged();
    };

    updateActiveGameViewDescriptions();

    context.subscriptions.push(
        vscode.workspace.onDidChangeConfiguration((event) => {
            if (event.affectsConfiguration('TKVSC.activeGameId')) {
                refreshForActiveGame();
            }
        }),
    );
}
