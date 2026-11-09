import * as path from 'path';
import * as vscode from 'vscode';

type ProjectFolderMap = Record<string, string[]>;

const SETTING = 'projectFolders';

function readMap(): ProjectFolderMap {
    const raw = vscode.workspace.getConfiguration('TKVSC').get<ProjectFolderMap>(SETTING, {});
    const map: ProjectFolderMap = {};
    for (const [gameId, folders] of Object.entries(raw ?? {})) {
        if (Array.isArray(folders)) {
            map[gameId] = folders.filter((folder): folder is string => typeof folder === 'string' && folder.trim() !== '');
        }
    }
    return map;
}

/** The folders a game's projects live in, as set by the user. The first is where new projects go. */
export function getProjectFolders(gameId: string): string[] {
    return readMap()[gameId] ?? [];
}

/**
 * Where a game's new projects go when it has no folder of its own: a folder named after
 * the game inside the older `TKVSC.projectsPath`, or undefined if that is not set either.
 */
export function getFallbackProjectFolder(gameId: string): string | undefined {
    const base = vscode.workspace.getConfiguration('TKVSC').get<string>('projectsPath', '').trim();
    if (!base) {
        return undefined;
    }
    return path.join(base, gameId.replace(/[<>:"/\\|?*\x00-\x1f]/g, '_'));
}

async function writeFolders(gameId: string, folders: string[]): Promise<void> {
    const map = readMap();
    if (folders.length > 0) {
        map[gameId] = folders;
    } else {
        delete map[gameId];
    }
    await vscode.workspace
        .getConfiguration('TKVSC')
        .update(SETTING, map, vscode.ConfigurationTarget.Global);
}

function samePath(a: string, b: string): boolean {
    return path.normalize(a).toLowerCase() === path.normalize(b).toLowerCase();
}

export async function addProjectFolder(gameId: string, folder: string): Promise<void> {
    const folders = getProjectFolders(gameId);
    if (folders.some((existing) => samePath(existing, folder))) {
        return;
    }
    await writeFolders(gameId, [...folders, path.normalize(folder)]);
}

/** Asks for a folder and adds it to the game's project folders. */
export async function pickAndAddProjectFolder(gameId: string, gameName: string): Promise<string | undefined> {
    const picked = await vscode.window.showOpenDialog({
        canSelectFiles: false,
        canSelectFolders: true,
        canSelectMany: false,
        title: `Projects Folder for ${gameName}`,
        openLabel: 'Use This Folder',
    });
    const folder = picked?.[0]?.fsPath;
    if (!folder) {
        return undefined;
    }
    await addProjectFolder(gameId, folder);
    return folder;
}
