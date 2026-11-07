import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import {
    findRomfsFolderUnder,
    isWithinRoot,
    normalizePath,
    resolveAddToCopyPaths,
    resolveProjectDestination,
} from './projectPaths';
import { getActiveProjectOption } from './projectAdapters/registry';
import { resolveRomfsPath } from './romfs';

export interface ProjectRoot {
    fsPath: string;
    label: string;
}

async function ensureParentDirectory(filePath: string): Promise<void> {
    await fs.promises.mkdir(path.dirname(filePath), { recursive: true });
}

async function confirmOverwrite(destination: string): Promise<boolean> {
    if (!fs.existsSync(destination)) {
        return true;
    }

    const choice = await vscode.window.showWarningMessage(
        `Overwrite existing file?\n${destination}`,
        { modal: true },
        'Overwrite',
    );
    return choice === 'Overwrite';
}

export function resolveRomfsForProject(projectRoot: string): string {
    const configured = resolveRomfsPath();
    if (configured) {
        return normalizePath(configured);
    }
    const underProject = findRomfsFolderUnder(normalizePath(projectRoot));
    return underProject ?? normalizePath(projectRoot);
}

export async function addDumpEntryToProject(
    sourceFsPath: string,
    projectRoot: string,
    romfsRoot?: string,
    options?: { suppressSuccessMessage?: boolean },
    tkmmOption?: { group: string; option: string },
): Promise<boolean> {
    const dumpRoot = romfsRoot?.trim()
        ? normalizePath(romfsRoot)
        : resolveRomfsPath() || resolveRomfsForProject(projectRoot);
    if (!dumpRoot) {
        void vscode.window.showErrorMessage(
            'TKVSC: Set **TKVSC.romfsPath** to your game dump folder first.',
        );
        return false;
    }

    let copyPaths: { source: string; destination: string };
    try {
        copyPaths = resolveAddToCopyPaths(sourceFsPath, projectRoot, dumpRoot, tkmmOption);
    } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        void vscode.window.showErrorMessage(`Add to project: ${message}`);
        return false;
    }

    if (!(await confirmOverwrite(copyPaths.destination))) {
        return false;
    }

    try {
        await ensureParentDirectory(copyPaths.destination);
        await fs.promises.copyFile(copyPaths.source, copyPaths.destination);
        try {
            await fs.promises.chmod(copyPaths.destination, 0o666);
        } catch (e) {
            // Ignore chmod errors
        }
        if (!options?.suppressSuccessMessage) {
            void vscode.window.showInformationMessage(
                `Added to project: ${path.relative(normalizePath(projectRoot), copyPaths.destination)}`,
            );
        }
        return true;
    } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        void vscode.window.showErrorMessage(`Add to project failed: ${message}`);
        return false;
    }
}

/**
 * The copy of a file that edits should go to. A file in the game dump is pulled into the active
 * project (and its active option) first, or the project's existing copy is used; files already in
 * a project, or outside both, stay where they are. Undefined when the user picked no project.
 */
export async function resolveEditableCopy(
    diskPath: string,
    context: vscode.ExtensionContext,
    projects: ProjectRoot[],
    activeProject: string | undefined,
): Promise<{ path: string; projectRoot?: string; pulled: boolean } | undefined> {
    const containing = projects.find((project) => isWithinRoot(project.fsPath, diskPath));
    if (containing) {
        return { path: diskPath, projectRoot: containing.fsPath, pulled: false };
    }

    const dumpRoot = resolveRomfsPath();
    if (!dumpRoot || !isWithinRoot(dumpRoot, diskPath)) {
        return { path: diskPath, pulled: false };
    }

    const projectRoot = activeProject ?? (await pickProjectRoot(projects));
    if (!projectRoot) {
        return undefined;
    }
    const option = getActiveProjectOption(context, projectRoot);
    const destination = resolveProjectDestination(diskPath, projectRoot, dumpRoot, option);
    if (fs.existsSync(destination)) {
        return { path: destination, projectRoot, pulled: false };
    }
    const copied = await addDumpEntryToProject(
        diskPath,
        projectRoot,
        dumpRoot,
        { suppressSuccessMessage: true },
        option,
    );
    return copied ? { path: destination, projectRoot, pulled: true } : undefined;
}

export async function pickProjectRoot(projects: ProjectRoot[]): Promise<string | undefined> {
    if (projects.length === 0) {
        void vscode.window.showWarningMessage(
            'No archive projects open. Add a project in **Your Projects** first.',
        );
        return undefined;
    }

    if (projects.length === 1) {
        return projects[0]!.fsPath;
    }

    const pick = await vscode.window.showQuickPick(
        projects.map((project) => ({
            label: project.label,
            description: project.fsPath,
            fsPath: project.fsPath,
        })),
        {
            placeHolder: 'Choose an archive project',
            title: 'Add to Project',
        },
    );

    return pick?.fsPath;
}

export { resolveProjectDestination, resolveAddToCopyPaths };
