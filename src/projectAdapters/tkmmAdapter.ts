import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';
import { TKVSC_ARCHIVE_CONTEXT } from '../api/constants';
import { normalizePath } from '../projectPaths';
import { createDefaultTkprojData } from '../tkprojDefaults';
import type {
    ProjectAdapter,
    ProjectOptionPickResult,
    ProjectOptionRef,
} from './types';

const ACTIVE_OPTIONS_KEY = 'totk-editor.activeTkmmOptions';

export class TkmmProjectAdapter implements ProjectAdapter {
    readonly id = 'tkmm';
    readonly displayName = 'TKMM';
    readonly contextValues = {
        optionsRoot: TKVSC_ARCHIVE_CONTEXT.tkmmOptionsRoot,
        optionGroup: TKVSC_ARCHIVE_CONTEXT.tkmmOptionGroup,
        option: TKVSC_ARCHIVE_CONTEXT.tkmmOption,
        optionActive: TKVSC_ARCHIVE_CONTEXT.tkmmOptionActive,
    };
    readonly optionsDirName = 'options';
    readonly projectMarkerFile = '.tkproj';

    isProjectRoot(projectRootPath: string): boolean {
        const root = normalizePath(projectRootPath);
        return (
            fs.existsSync(path.join(root, this.projectMarkerFile))
            || fs.existsSync(path.join(root, this.optionsDirName))
        );
    }

    supportsOptionsTree(projectRootPath: string): boolean {
        return fs.existsSync(path.join(normalizePath(projectRootPath), this.optionsDirName));
    }

    resolveProjectRootFromTreeItem(
        contextValue: string | undefined,
        resourceFsPath: string,
    ): string | undefined {
        switch (contextValue) {
            case TKVSC_ARCHIVE_CONTEXT.archiveRoot:
            case TKVSC_ARCHIVE_CONTEXT.archiveProjectDir:
            case TKVSC_ARCHIVE_CONTEXT.archiveProjectDirActive:
                return resourceFsPath;
            case this.contextValues.optionsRoot:
                return path.dirname(resourceFsPath);
            case this.contextValues.option:
            case this.contextValues.optionActive:
                return path.dirname(path.dirname(path.dirname(resourceFsPath)));
            case this.contextValues.optionGroup:
                return path.dirname(path.dirname(resourceFsPath));
            case TKVSC_ARCHIVE_CONTEXT.archiveTkproj:
                return path.dirname(resourceFsPath);
            default:
                return undefined;
        }
    }

    resolveOptionsProjectRoot(
        contextValue: string | undefined,
        resourceFsPath: string,
        workspaceRootPath: string,
        isArchiveRoot: boolean,
    ): string {
        if (contextValue === this.contextValues.optionsRoot) {
            return path.dirname(resourceFsPath);
        }
        if (contextValue === this.contextValues.optionGroup) {
            return path.dirname(path.dirname(resourceFsPath));
        }
        if (
            contextValue === this.contextValues.option
            || contextValue === this.contextValues.optionActive
        ) {
            return path.dirname(path.dirname(path.dirname(resourceFsPath)));
        }
        if (!isArchiveRoot) {
            return workspaceRootPath;
        }
        return resourceFsPath;
    }

    getActiveOption(
        context: vscode.ExtensionContext,
        projectRoot: string,
    ): ProjectOptionRef | undefined {
        const activeOptions = context.workspaceState.get<Record<string, ProjectOptionRef>>(
            ACTIVE_OPTIONS_KEY,
            {},
        );
        return activeOptions[normalizePath(projectRoot)];
    }

    async setActiveOption(
        context: vscode.ExtensionContext,
        projectRoot: string,
        group?: string,
        option?: string,
    ): Promise<void> {
        const activeOptions = {
            ...context.workspaceState.get<Record<string, ProjectOptionRef>>(ACTIVE_OPTIONS_KEY, {}),
        };
        const key = normalizePath(projectRoot);

        if (group && option) {
            activeOptions[key] = { group, option };
        } else {
            delete activeOptions[key];
        }

        await context.workspaceState.update(ACTIVE_OPTIONS_KEY, activeOptions);
    }

    async listOptionGroups(projectRoot: string): Promise<string[]> {
        const optionsDir = path.join(projectRoot, this.optionsDirName);
        if (!fs.existsSync(optionsDir)) {
            return [];
        }

        try {
            const entries = await fs.promises.readdir(optionsDir, { withFileTypes: true });
            return entries.filter((entry) => entry.isDirectory()).map((entry) => entry.name);
        } catch {
            return [];
        }
    }

    async listOptions(projectRoot: string, group: string): Promise<string[]> {
        const groupDir = path.join(projectRoot, this.optionsDirName, group);
        if (!fs.existsSync(groupDir)) {
            return [];
        }

        try {
            const entries = await fs.promises.readdir(groupDir, { withFileTypes: true });
            return entries.filter((entry) => entry.isDirectory()).map((entry) => entry.name);
        } catch {
            return [];
        }
    }

    async createOptionGroup(projectRoot: string, groupName: string): Promise<void> {
        const groupDir = path.join(projectRoot, this.optionsDirName, groupName);
        await fs.promises.mkdir(groupDir, { recursive: true });

        const infoJsonPath = path.join(groupDir, 'info.json');
        if (!fs.existsSync(infoJsonPath)) {
            await fs.promises.writeFile(
                infoJsonPath,
                JSON.stringify({
                    Dependencies: [],
                    Type: 0,
                    IconName: null,
                    Priority: -1,
                    IsEditing: false,
                    Name: groupName,
                    Description: '',
                    Thumbnail: null,
                }),
            );
        }
    }

    async createOption(
        projectRoot: string,
        groupName: string,
        optionName: string,
    ): Promise<void> {
        const optionDir = path.join(projectRoot, this.optionsDirName, groupName, optionName);
        await fs.promises.mkdir(optionDir, { recursive: true });
        await fs.promises.mkdir(path.join(optionDir, 'romfs'), { recursive: true });

        const infoJsonPath = path.join(optionDir, 'info.json');
        if (!fs.existsSync(infoJsonPath)) {
            await fs.promises.writeFile(
                infoJsonPath,
                JSON.stringify({
                    Dependencies: [],
                    Type: 0,
                    IconName: null,
                    Priority: -1,
                    IsEditing: false,
                    Name: optionName,
                    Description: '',
                    Thumbnail: null,
                }),
            );
        }
    }

    async askForOption(projectRoot: string): Promise<ProjectOptionPickResult | undefined> {
        while (true) {
            const groups = await this.listOptionGroups(projectRoot);
            const groupItems: vscode.QuickPickItem[] = [
                { label: '$(folder) Base Project', description: 'Add to the root project romfs' },
                ...groups.map((g) => ({ label: `$(folder) ${g}` })),
                { label: '$(add) Create New Option Group...' },
                { label: '$(arrow-left) Back' },
            ];

            const pickedGroup = await vscode.window.showQuickPick(groupItems, {
                title: 'Select Option Group',
                placeHolder: 'Choose an option group, Base Project, or create a new one',
            });

            if (!pickedGroup) {
                return undefined;
            }

            if (pickedGroup.label === '$(arrow-left) Back') {
                return 'BACK';
            }

            if (pickedGroup.label === '$(folder) Base Project') {
                return 'BASE_PROJECT';
            }

            let selectedGroupName = pickedGroup.label.replace('$(folder) ', '');
            if (pickedGroup.label === '$(add) Create New Option Group...') {
                const newName = await vscode.window.showInputBox({
                    prompt: 'Enter new Option Group name',
                });
                if (!newName) {
                    continue;
                }
                await this.createOptionGroup(projectRoot, newName);
                selectedGroupName = newName;
            }

            while (true) {
                const options = await this.listOptions(projectRoot, selectedGroupName);
                const optionItems: vscode.QuickPickItem[] = [
                    { label: '$(arrow-left) Back to Option Groups' },
                    ...options.map((o) => ({ label: `$(folder) ${o}` })),
                    { label: '$(add) Create New Option...' },
                ];

                const pickedOption = await vscode.window.showQuickPick(optionItems, {
                    title: `Select Option in '${selectedGroupName}'`,
                    placeHolder: 'Choose an option or create a new one',
                });

                if (!pickedOption) {
                    return undefined;
                }

                if (pickedOption.label === '$(arrow-left) Back to Option Groups') {
                    break;
                }

                let selectedOptionName = pickedOption.label.replace('$(folder) ', '');
                if (pickedOption.label === '$(add) Create New Option...') {
                    const newName = await vscode.window.showInputBox({
                        prompt: `Enter new Option name for '${selectedGroupName}'`,
                    });
                    if (!newName) {
                        continue;
                    }
                    await this.createOption(projectRoot, selectedGroupName, newName);
                    selectedOptionName = newName;
                }

                return { group: selectedGroupName, option: selectedOptionName };
            }
        }
    }

    listModRoots(projectRoot: string): string[] {
        const project = normalizePath(projectRoot);
        const roots: string[] = [project];
        const optionsDir = path.join(project, this.optionsDirName);

        if (!fs.existsSync(optionsDir)) {
            return roots;
        }

        try {
            for (const group of fs.readdirSync(optionsDir, { withFileTypes: true })) {
                if (!group.isDirectory()) {
                    continue;
                }
                const groupPath = path.join(optionsDir, group.name);
                for (const option of fs.readdirSync(groupPath, { withFileTypes: true })) {
                    if (option.isDirectory()) {
                        roots.push(normalizePath(path.join(groupPath, option.name)));
                    }
                }
            }
        } catch {
            // ignore
        }

        return roots;
    }

    async importProjects(): Promise<string[]> {
        const foundPath = await getTkmmRecentJsonPath();
        if (!foundPath) {
            return [];
        }

        const data = await vscode.workspace.fs.readFile(vscode.Uri.file(foundPath));
        const projects = JSON.parse(Buffer.from(data).toString('utf-8')) as string[];
        if (!Array.isArray(projects)) {
            throw new Error('Invalid format in TKMM recent.json.');
        }

        const valid: string[] = [];
        for (const projectPath of projects) {
            try {
                const stat = await vscode.workspace.fs.stat(vscode.Uri.file(projectPath));
                if (stat.type === vscode.FileType.Directory) {
                    valid.push(projectPath);
                }
            } catch {
                // skip missing paths
            }
        }
        return valid;
    }

    getImportSourceLabel(): string {
        return 'TKMM recent.json';
    }

    async scaffoldNewProject(projectFolderUri: vscode.Uri): Promise<void> {
        await vscode.workspace.fs.createDirectory(vscode.Uri.joinPath(projectFolderUri, 'romfs'));
        const projectName = path.basename(normalizePath(projectFolderUri.fsPath));
        const defaultData = createDefaultTkprojData(projectName || undefined);
        await vscode.workspace.fs.writeFile(
            vscode.Uri.joinPath(projectFolderUri, this.projectMarkerFile!),
            Buffer.from(JSON.stringify(defaultData, null, 2), 'utf-8'),
        );
    }
}

export async function getTkmmRecentJsonPath(): Promise<string | undefined> {
    const recentJsonPaths: string[] = [];
    const homeDir = os.homedir();
    if (process.platform === 'win32' && process.env.LOCALAPPDATA) {
        recentJsonPaths.push(path.join(process.env.LOCALAPPDATA, '.tk-studio', 'recent.json'));
    } else if (process.platform === 'darwin') {
        recentJsonPaths.push(
            path.join(homeDir, 'Library', 'Application Support', '.tk-studio', 'recent.json'),
        );
    } else if (process.env.XDG_DATA_HOME) {
        recentJsonPaths.push(path.join(process.env.XDG_DATA_HOME, '.tk-studio', 'recent.json'));
    } else {
        recentJsonPaths.push(path.join(homeDir, '.local', 'share', '.tk-studio', 'recent.json'));
    }

    for (const candidate of recentJsonPaths) {
        try {
            const stat = await vscode.workspace.fs.stat(vscode.Uri.file(candidate));
            if (stat.type === vscode.FileType.File) {
                return candidate;
            }
        } catch {
            // try next
        }
    }
    return undefined;
}

let builtinTkmmAdapter: TkmmProjectAdapter | undefined;

export function getBuiltinTkmmAdapter(): TkmmProjectAdapter {
    builtinTkmmAdapter ??= new TkmmProjectAdapter();
    return builtinTkmmAdapter;
}
