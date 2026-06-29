import * as path from 'path';
import * as vscode from 'vscode';
import { isArchiveFile, isBntxTextureUri, isPathInsideArchive, isTxtgFile, isBwavAudioFile, isBarsAudioArchive } from './archives';
import { registerArchiveFileCommands, ArchiveTreeDragDrop, setArchiveTreeView } from './archiveFsCommands';
import {
    detectProjectAdapter,
    getActiveProjectOption,
    importProjectsFromAdapters,
    resolveProjectRootFromTreeItem,
} from './projectAdapters/registry';
import type { ProjectAdapter } from './projectAdapters/types';

const STORAGE_KEY = 'totk-editor.archiveRoots';

let extensionUri: vscode.Uri | undefined;

function isTkprojFile(name: string): boolean {
    return name.toLowerCase().endsWith('.tkproj');
}

function isTkvscFile(name: string): boolean {
    return name.toLowerCase() === '.tkvsc';
}

export function toSarcUri(fileUri: vscode.Uri): vscode.Uri {
    return fileUri.with({ scheme: 'sarc' });
}

export class ArchiveTreeItem extends vscode.TreeItem {
    constructor(
        public readonly entryName: string,
        public readonly resourceUri: vscode.Uri,
        collapsibleState: vscode.TreeItemCollapsibleState,
        options?: { isRoot?: boolean; contextValue?: string; isActive?: boolean },
    ) {
        super(entryName, collapsibleState);
        this.resourceUri = resourceUri;
        this.id = resourceUri.toString();
        this.contextValue =
            options?.contextValue ?? (options?.isRoot ? 'archiveRoot' : undefined);
        if (options?.isRoot) {
            this.description = path.dirname(resourceUri.fsPath);
            this.tooltip = resourceUri.fsPath;
            if (options.isActive) {
                this.iconPath = new vscode.ThemeIcon('star-full');
            }
        } else if (collapsibleState === vscode.TreeItemCollapsibleState.None) {
            if (isBntxTextureUri(resourceUri) || isTxtgFile(resourceUri.fsPath)) {
                this.command = { command: 'totk-editor.openBntxTexture', title: 'View Texture', arguments: [resourceUri] };
            } else if (isBwavAudioFile(resourceUri.fsPath)) {
                this.command = { command: 'vscode.open', title: 'Open', arguments: [resourceUri, { preview: true }] };
            } else if (isBarsAudioArchive(resourceUri.fsPath)) {
                this.command = { command: 'totk-editor.openBarsArchive', title: 'Open BARS Archive', arguments: [resourceUri] };
            } else {
                this.command = { command: 'vscode.open', title: 'Open', arguments: [resourceUri] };
            }
        }
        
        if (!options?.isRoot && options?.isActive) {
            this.iconPath = new vscode.ThemeIcon('star-full');
        } else if (isArchiveFile(entryName)) {
            this.iconPath = new vscode.ThemeIcon('package');
        } else if ((isBntxTextureUri(resourceUri) || isTxtgFile(resourceUri.fsPath)) && extensionUri) {
            this.iconPath = vscode.Uri.joinPath(extensionUri, 'icons', 'texture.svg');
        } else if (isBwavAudioFile(resourceUri.fsPath) && extensionUri) {
            this.iconPath = vscode.Uri.joinPath(extensionUri, 'icons', 'bwav.svg');
        } else if (isBarsAudioArchive(resourceUri.fsPath) && extensionUri) {
            this.iconPath = vscode.Uri.joinPath(extensionUri, 'icons', 'bars.svg');
        } else if (isTkprojFile(entryName) && extensionUri) {
            this.iconPath = vscode.Uri.joinPath(extensionUri, 'icons', 'tkproj.svg');
        } else if (isTkvscFile(entryName) && extensionUri) {
            this.iconPath = vscode.Uri.joinPath(extensionUri, 'icons', 'tkvsc.svg');
        }
    }
}

export class ArchiveTreeProvider implements vscode.TreeDataProvider<ArchiveTreeItem> {
    private readonly onDidChangeTreeDataEmitter = new vscode.EventEmitter<ArchiveTreeItem | undefined>();
    readonly onDidChangeTreeData = this.onDidChangeTreeDataEmitter.event;
    private readonly onDidChangeRootsEmitter = new vscode.EventEmitter<void>();
    readonly onDidChangeRoots = this.onDidChangeRootsEmitter.event;

    private roots: vscode.Uri[] = [];
    private activeProjectRootUri: string | undefined;
    
    private logicalRoots: Map<string, string> = new Map();
    private hasMultipleMods: Set<string> = new Set();
    
    public get workspaceRoots(): vscode.Uri[] {
        return this.roots;
    }

    private sortRoots(): void {
        this.roots.sort((a, b) =>
            path.basename(a.fsPath).localeCompare(path.basename(b.fsPath), undefined, {
                sensitivity: 'base',
            }),
        );
    }

    constructor(private readonly context: vscode.ExtensionContext) {
        const stored = context.globalState.get<string[]>(STORAGE_KEY, []);
        this.roots = stored.map((fsPath) => toSarcUri(vscode.Uri.file(fsPath)));
        this.activeProjectRootUri = context.globalState.get<string>('totk-editor.activeProjectRoot');
        
        const storedLogicalRoots = context.globalState.get<Record<string, string>>('totk-editor.logicalRoots', {});
        for (const [workspacePath, logicalPath] of Object.entries(storedLogicalRoots)) {
            this.logicalRoots.set(workspacePath, logicalPath);
        }
        
        const storedMultipleMods = context.globalState.get<string[]>('totk-editor.hasMultipleMods', []);
        for (const workspacePath of storedMultipleMods) {
            this.hasMultipleMods.add(workspacePath);
        }
        
        this.sortRoots();
    }

    getTreeItem(element: ArchiveTreeItem): vscode.TreeItem {
        return element;
    }

    async getChildren(element?: ArchiveTreeItem): Promise<ArchiveTreeItem[]> {
        if (!element) {
            return this.roots.map(
                (root) => {
                    const logicalPath = this.logicalRoots.get(root.fsPath) || root.fsPath;
                    return new ArchiveTreeItem(
                        path.basename(root.fsPath),
                        root,
                        vscode.TreeItemCollapsibleState.Collapsed,
                        { isRoot: true, isActive: logicalPath === this.activeProjectRootUri },
                    );
                }
            );
        }

        try {
            const entries = await vscode.workspace.fs.readDirectory(element.resourceUri);
            const isProjectRoot = element.contextValue === 'archiveRoot' || element.contextValue === 'archiveProjectDir' || element.contextValue === 'archiveProjectDirActive';
            
            // Find the workspace root path for hasMultipleMods
            let workspaceRootPath = element.resourceUri.fsPath;
            if (element.contextValue !== 'archiveRoot') {
                const matchedRoot = this.roots.find(r => element.resourceUri.fsPath.startsWith(r.fsPath));
                if (matchedRoot) {
                    workspaceRootPath = matchedRoot.fsPath;
                }
            }

            const projectAdapter = detectProjectAdapter(workspaceRootPath);

            const optionsProjectRoot = projectAdapter.resolveOptionsProjectRoot(
                element.contextValue,
                element.resourceUri.fsPath,
                workspaceRootPath,
                isProjectRoot,
            );

            const activeOption = getActiveProjectOption(this.context, optionsProjectRoot);
            const cv = projectAdapter.contextValues;

            const children = await Promise.all(entries
                .sort(compareEntriesFoldersFirstKeepingArchivesMixed)
                .map(async ([name, fileType]) => {
                    const childUri = vscode.Uri.joinPath(element.resourceUri, name);
                    const isDirectory = fileType === vscode.FileType.Directory || isArchiveFile(name);
                    
                    let contextValue = archiveContextValue(name, isDirectory, childUri.fsPath);
                    if (
                        isProjectRoot
                        && name.toLowerCase() === projectAdapter.optionsDirName
                        && isDirectory
                        && (await projectAdapter.supportsOptionsTree(workspaceRootPath))
                    ) {
                        contextValue = cv.optionsRoot;
                    } else if (element.contextValue === cv.optionsRoot && isDirectory) {
                        contextValue = cv.optionGroup;
                    } else if (element.contextValue === cv.optionGroup && isDirectory) {
                        contextValue = cv.option;
                    } else if (isDirectory && this.hasMultipleMods.has(workspaceRootPath) && contextValue === 'archiveDir') {
                        // Check if this directory is a valid mod folder (has romfs/exefs/.tkproj)
                        let isModFolder = false;
                        try {
                            const subEntries = await vscode.workspace.fs.readDirectory(childUri);
                            for (const [subName, subType] of subEntries) {
                                const lower = subName.toLowerCase();
                                if (lower === 'romfs' || lower === 'exefs' || lower.endsWith('.tkproj')) {
                                    isModFolder = true;
                                    break;
                                }
                            }
                        } catch {
                            // Ignore
                        }
                        if (isModFolder) {
                            const currentLogicalRoot = this.logicalRoots.get(workspaceRootPath);
                            if (currentLogicalRoot && currentLogicalRoot === childUri.fsPath) {
                                contextValue = 'archiveProjectDirActive';
                            } else {
                                contextValue = 'archiveProjectDir';
                            }
                        }
                    }

                    let isActiveOption = false;
                    if (activeOption) {
                        if (contextValue === cv.option) {
                            if (element.entryName === activeOption.group && name === activeOption.option) {
                                isActiveOption = true;
                                contextValue = cv.optionActive;
                            }
                        } else if (contextValue === cv.optionGroup) {
                            if (name === activeOption.group) {
                                isActiveOption = true;
                            }
                        }
                    }

                    const item = new ArchiveTreeItem(
                        name,
                        childUri,
                        isDirectory
                            ? vscode.TreeItemCollapsibleState.Collapsed
                            : vscode.TreeItemCollapsibleState.None,
                        { contextValue, isActive: isActiveOption },
                    );
                    
                    if (contextValue === 'archiveProjectDirActive') {
                        item.description = '(Project Root)';
                    }
                    
                    return item;
                }));
            return children;
        } catch (error) {
            const message = error instanceof Error ? error.message : String(error);
            void vscode.window.showErrorMessage(`Your Projects: ${message}`);
            return [];
        }
    }

    private async ensureRomfsFolder(rootUri: vscode.Uri): Promise<void> {
        const createRomfs = vscode.workspace.getConfiguration('TKVSC').get<boolean>('createRomfsOnImport', true);
        if (!createRomfs) {
            return;
        }

        try {
            const fileUri = rootUri.scheme === 'sarc' ? vscode.Uri.file(rootUri.fsPath) : rootUri;
            const entries = await vscode.workspace.fs.readDirectory(fileUri);
            
            let hasTkproj = false;
            let hasRomfsOrExefs = false;

            for (const [name, type] of entries) {
                const lowerName = name.toLowerCase();
                if (lowerName.endsWith('.tkproj')) {
                    hasTkproj = true;
                }
                if (type === vscode.FileType.Directory && (lowerName === 'romfs' || lowerName === 'exefs')) {
                    hasRomfsOrExefs = true;
                }
            }

            if (hasTkproj && !hasRomfsOrExefs) {
                const romfsUri = vscode.Uri.joinPath(fileUri, 'romfs');
                await vscode.workspace.fs.createDirectory(romfsUri);
            }
        } catch (e) {
            console.error('Failed to ensure romfs folder:', e);
        }
    }

    async addRoot(fileUri: vscode.Uri): Promise<void> {
        const sarcUri = fileUri.scheme === 'sarc' ? fileUri : toSarcUri(fileUri);
        const key = sarcUri.fsPath;
        if (this.roots.some((root) => root.fsPath === key)) {
            return;
        }
        this.roots.push(sarcUri);
        this.sortRoots();
        void this.persistRoots();
        
        const fileRootPath = sarcUri.fsPath;
        const validMods = await findValidModFolders(fileUri);
        if (validMods.length > 1) {
            this.hasMultipleMods.add(fileRootPath);
            const items = validMods.map(uri => ({
                label: path.basename(uri.fsPath) || uri.fsPath,
                description: path.relative(fileRootPath, uri.fsPath) || 'Root',
                uri
            }));
            const selection = await vscode.window.showQuickPick(items, {
                title: `Multiple mods found in ${path.basename(fileRootPath)}. Select the active project root.`,
                ignoreFocusOut: true,
                placeHolder: 'Select the active project root for this workspace'
            });
            
            if (selection) {
                this.logicalRoots.set(fileRootPath, selection.uri.fsPath);
            } else {
                this.logicalRoots.set(fileRootPath, validMods[0]!.fsPath);
            }
        } else if (validMods.length === 1) {
            this.hasMultipleMods.delete(fileRootPath);
            this.logicalRoots.set(fileRootPath, validMods[0]!.fsPath);
        } else {
            this.hasMultipleMods.delete(fileRootPath);
            this.logicalRoots.delete(fileRootPath);
        }
        
        await this.persistLogicalRoots();
        
        this.onDidChangeTreeDataEmitter.fire(undefined);
        this.onDidChangeRootsEmitter.fire();
        void this.ensureRomfsFolder(fileUri);
    }

    removeRoot(fileUri: vscode.Uri): void {
        const key = (fileUri.scheme === 'sarc' ? fileUri : toSarcUri(fileUri)).fsPath;
        const next = this.roots.filter((root) => root.fsPath !== key);
        if (next.length === this.roots.length) {
            return;
        }
        this.roots = next;
        this.logicalRoots.delete(key);
        this.hasMultipleMods.delete(key);
        void this.persistRoots();
        void this.persistLogicalRoots();
        this.onDidChangeTreeDataEmitter.fire(undefined);
        this.onDidChangeRootsEmitter.fire();
    }

    async refresh(): Promise<void> {
        for (const root of this.roots) {
            const fileRootPath = root.fsPath;
            const validMods = await findValidModFolders(root);
            if (validMods.length > 1) {
                this.hasMultipleMods.add(fileRootPath);
            } else {
                this.hasMultipleMods.delete(fileRootPath);
                if (validMods.length === 1) {
                    this.logicalRoots.set(fileRootPath, validMods[0]!.fsPath);
                } else {
                    this.logicalRoots.delete(fileRootPath);
                }
            }
        }
        await this.persistLogicalRoots();
        this.onDidChangeTreeDataEmitter.fire(undefined);
    }

    getProjectRoots(): { fsPath: string; label: string }[] {
        return this.roots.map((root) => {
            const logicalPath = this.logicalRoots.get(root.fsPath);
            const activePath = logicalPath || root.fsPath;
            return {
                fsPath: activePath,
                label: path.basename(activePath),
            };
        });
    }

    setActiveProject(fsPath: string | undefined): void {
        if (fsPath) {
            const logicalPath = this.logicalRoots.get(fsPath);
            this.activeProjectRootUri = logicalPath || fsPath;
            void this.context.globalState.update('totk-editor.activeProjectRoot', this.activeProjectRootUri);
        } else {
            this.activeProjectRootUri = undefined;
            void this.context.globalState.update('totk-editor.activeProjectRoot', undefined);
        }
        this.onDidChangeTreeDataEmitter.fire(undefined);
    }

    getActiveProject(): string | undefined {
        const currentProjectRoots = this.getProjectRoots().map(r => r.fsPath);
        if (this.activeProjectRootUri && !currentProjectRoots.includes(this.activeProjectRootUri)) {
            this.setActiveProject(undefined);
        }
        return this.activeProjectRootUri;
    }

    private async persistRoots(): Promise<void> {
        await this.context.globalState.update(
            STORAGE_KEY,
            this.roots.map((root) => root.fsPath),
        );
    }

    private async persistLogicalRoots(): Promise<void> {
        const mapping: Record<string, string> = {};
        for (const [k, v] of this.logicalRoots.entries()) {
            mapping[k] = v;
        }
        await this.context.globalState.update('totk-editor.logicalRoots', mapping);
        await this.context.globalState.update('totk-editor.hasMultipleMods', Array.from(this.hasMultipleMods));
    }
    
    public async setLogicalProjectRoot(workspacePath: string, logicalPath: string): Promise<void> {
        const oldLogicalRoot = this.logicalRoots.get(workspacePath) || workspacePath;
        this.logicalRoots.set(workspacePath, logicalPath);
        await this.persistLogicalRoots();
        
        if (this.activeProjectRootUri === oldLogicalRoot) {
            this.setActiveProject(workspacePath); // setActiveProject handles saving and refreshing
        } else {
            this.refresh();
        }
    }
}

function compareEntriesFoldersFirstKeepingArchivesMixed(
    [nameA, fileTypeA]: [string, vscode.FileType],
    [nameB, fileTypeB]: [string, vscode.FileType],
): number {
    const isNormalDirectoryA = fileTypeA === vscode.FileType.Directory && !isArchiveFile(nameA);
    const isNormalDirectoryB = fileTypeB === vscode.FileType.Directory && !isArchiveFile(nameB);

    if (isNormalDirectoryA !== isNormalDirectoryB) {
        return isNormalDirectoryA ? -1 : 1;
    }

    return nameA.localeCompare(nameB, undefined, { sensitivity: 'base' });
}

export async function focusArchiveSidebar(): Promise<void> {
    await vscode.commands.executeCommand('workbench.view.extension.totk-editor');
    await vscode.commands.executeCommand('totk-editor.archives.focus');
}

export { getTkmmRecentJsonPath } from './projectAdapters/tkmmAdapter';

export function registerArchiveTree(context: vscode.ExtensionContext): ArchiveTreeProvider {
    extensionUri = context.extensionUri;
    const provider = new ArchiveTreeProvider(context);
    registerArchiveFileCommands(context);

    const treeView = vscode.window.createTreeView('totk-editor.archives', {
        treeDataProvider: provider,
        showCollapseAll: true,
        dragAndDropController: new ArchiveTreeDragDrop(),
        canSelectMany: true,
    });
    setArchiveTreeView(treeView);
    context.subscriptions.push(treeView);

    context.subscriptions.push(
        vscode.commands.registerCommand('totk-editor.refreshArchives', () => {
            provider.refresh();
        }),
    );

    const addWorkspaceToArchives = async (): Promise<void> => {
            const folder = vscode.workspace.workspaceFolders?.[0];
            if (!folder) {
                void vscode.window.showWarningMessage(
                    'Your Projects: Open a folder in the workspace first.',
                );
                return;
            }
            if (folder.uri.scheme !== 'file') {
                void vscode.window.showWarningMessage(
                    'Your Projects: Workspace is not a normal folder on disk.',
                );
                return;
            }
            await provider.addRoot(folder.uri);
            void focusArchiveSidebar();
    };

    const addProjectFolders = async (): Promise<void> => {
        const uris = await vscode.window.showOpenDialog({
            canSelectFiles: false,
            canSelectFolders: true,
            canSelectMany: true,
            title: 'Select Projects to Add',
            openLabel: 'Add Projects'
        });
        if (uris && uris.length > 0) {
            for (const uri of uris) {
                await provider.addRoot(uri);
            }
            void focusArchiveSidebar();
        }
    };

    const createProject = async (): Promise<void> => {
        const config = vscode.workspace.getConfiguration('TKVSC');
        const projectsPath = config.get<string>('projectsPath');
        if (!projectsPath) {
            void vscode.window.showErrorMessage('TKVSC: Please set a default projects path in settings (TKVSC.projectsPath) first.');
            return;
        }

        const projectName = await vscode.window.showInputBox({
            prompt: 'Enter the name for the new project',
            placeHolder: 'My New Project'
        });

        if (!projectName) {
            return;
        }

        const projectFolderUri = vscode.Uri.file(path.join(projectsPath, projectName));
        
        try {
            // Check if exists
            try {
                const stat = await vscode.workspace.fs.stat(projectFolderUri);
                if (stat) {
                    void vscode.window.showErrorMessage(`TKVSC: Project folder '${projectName}' already exists at the specified path.`);
                    return;
                }
            } catch {
                // Doesn't exist, which is good
            }

            await vscode.workspace.fs.createDirectory(projectFolderUri);

            const adapter = detectProjectAdapter(projectFolderUri.fsPath);
            if (adapter.scaffoldNewProject) {
                await adapter.scaffoldNewProject(projectFolderUri);
            } else {
                await vscode.workspace.fs.createDirectory(vscode.Uri.joinPath(projectFolderUri, 'romfs'));
            }

            // Add to workspace/archive tree
            await provider.addRoot(projectFolderUri);
            void focusArchiveSidebar();

            void vscode.window.showInformationMessage(`TKVSC: Created new project '${projectName}'.`);

        } catch (error) {
            const message = error instanceof Error ? error.message : String(error);
            void vscode.window.showErrorMessage(`TKVSC: Failed to create project: ${message}`);
        }
    };

    const importTKMMProjects = async (): Promise<void> => {
        let batches: Array<{ adapter: ProjectAdapter; paths: string[] }>;
        try {
            batches = await importProjectsFromAdapters();
        } catch (error) {
            const message = error instanceof Error ? error.message : String(error);
            void vscode.window.showErrorMessage(`Your Projects: Failed to import projects: ${message}`);
            return;
        }

        if (batches.length === 0) {
            void vscode.window.showWarningMessage(
                'Your Projects: No external project lists found. Open projects in TKMM (or register a project adapter) first.',
            );
            return;
        }

        let addedCount = 0;
        for (const { adapter, paths } of batches) {
            for (const projectPath of paths) {
                await provider.addRoot(vscode.Uri.file(projectPath));
                addedCount++;
            }
            if (paths.length > 0) {
                void vscode.window.showInformationMessage(
                    `Your Projects: Imported ${paths.length} project(s) from ${adapter.getImportSourceLabel?.() ?? adapter.displayName}.`,
                );
            }
        }
        if (addedCount > 0) {
            void focusArchiveSidebar();
        }
    };

    context.subscriptions.push(
        vscode.commands.registerCommand('totk-editor.addWorkspaceToArchives', addWorkspaceToArchives),
        // Backwards-compat alias for an older mistyped command id.
        vscode.commands.registerCommand('totk-edit.addWorkspaceToArchives', addWorkspaceToArchives),
        vscode.commands.registerCommand('totk-editor.addProjectFolders', addProjectFolders),
        vscode.commands.registerCommand('totk-editor.createProject', createProject),
        vscode.commands.registerCommand('totk-editor.importTKMMProjects', importTKMMProjects),
    );

    context.subscriptions.push(
        vscode.commands.registerCommand(
            'totk-editor.removeArchiveRoot',
            (item: ArchiveTreeItem | undefined, selectedItems?: ArchiveTreeItem[]) => {
                const itemsToRemove = selectedItems && selectedItems.length > 0 ? selectedItems : (item ? [item] : []);
                for (const i of itemsToRemove) {
                    if (i.resourceUri) {
                        provider.removeRoot(i.resourceUri);
                    }
                }
            },
        ),
        vscode.commands.registerCommand(
            'totk-editor.setActiveProject',
            (item: ArchiveTreeItem | undefined) => {
                if (item && item.resourceUri) {
                    provider.setActiveProject(item.resourceUri.fsPath);
                }
            }
        ),
        vscode.commands.registerCommand(
            'totk-editor.setLogicalProjectRoot',
            async (item: ArchiveTreeItem | undefined) => {
                if (!item) {return;}
                const workspaceRoot = provider.workspaceRoots.find(r => item.resourceUri.fsPath.startsWith(r.fsPath));
                if (workspaceRoot) {
                    await provider.setLogicalProjectRoot(workspaceRoot.fsPath, item.resourceUri.fsPath);
                }
            }
        ),
        vscode.commands.registerCommand(
            'totk-editor.createOptionGroup',
            async (item: ArchiveTreeItem | undefined) => {
                if (!item?.resourceUri) {
                    return;
                }
                const rootUri = resolveProjectRootFromTreeItem(
                    item.contextValue,
                    item.resourceUri.fsPath,
                );
                if (!rootUri) {
                    return;
                }
                const adapter = detectProjectAdapter(rootUri);
                if (
                    item.contextValue !== 'archiveRoot'
                    && item.contextValue !== adapter.contextValues.optionsRoot
                ) {
                    return;
                }

                const groupName = await vscode.window.showInputBox({ prompt: 'Enter Option Group Name' });
                if (groupName) {
                    await adapter.createOptionGroup(rootUri, groupName);
                    provider.refresh();
                }
            }
        ),
        vscode.commands.registerCommand(
            'totk-editor.createOption',
            async (item: ArchiveTreeItem | undefined) => {
                if (!item?.resourceUri) {
                    return;
                }
                const rootUri = resolveProjectRootFromTreeItem(
                    item.contextValue,
                    item.resourceUri.fsPath,
                );
                if (!rootUri) {
                    return;
                }
                const adapter = detectProjectAdapter(rootUri);
                if (item.contextValue !== adapter.contextValues.optionGroup) {
                    return;
                }

                const groupName = item.entryName;
                const optionName = await vscode.window.showInputBox({
                    prompt: `Enter Option Name for group '${groupName}'`,
                });
                if (optionName) {
                    await adapter.createOption(rootUri, groupName, optionName);
                    provider.refresh();
                }
            }
        ),
        vscode.commands.registerCommand(
            'totk-editor.setActiveOption',
            async (item: ArchiveTreeItem | undefined) => {
                if (!item?.resourceUri) {
                    return;
                }
                const rootUri = resolveProjectRootFromTreeItem(
                    item.contextValue,
                    item.resourceUri.fsPath,
                );
                if (!rootUri) {
                    return;
                }
                const adapter = detectProjectAdapter(rootUri);
                if (
                    item.contextValue !== adapter.contextValues.option
                    && item.contextValue !== adapter.contextValues.optionActive
                ) {
                    return;
                }

                const optionName = item.entryName;
                const groupName = path.basename(path.dirname(item.resourceUri.fsPath));
                await adapter.setActiveOption(context, rootUri, groupName, optionName);
                provider.refresh();
            }
        ),
        vscode.commands.registerCommand(
            'totk-editor.clearActiveOption',
            async (item: ArchiveTreeItem | undefined) => {
                let rootUri: string | undefined;
                if (!item) {
                    rootUri = provider.getActiveProject();
                    if (!rootUri && provider.getProjectRoots().length > 0) {
                        rootUri = provider.getProjectRoots()[0]?.fsPath;
                    }
                } else if (item.resourceUri) {
                    rootUri = resolveProjectRootFromTreeItem(
                        item.contextValue,
                        item.resourceUri.fsPath,
                    );
                    if (
                        !rootUri
                        && (item.contextValue === 'archiveRoot'
                            || item.contextValue === 'archiveProjectDir'
                            || item.contextValue === 'archiveProjectDirActive')
                    ) {
                        rootUri = item.resourceUri.fsPath;
                    }
                }

                if (rootUri) {
                    const adapter = detectProjectAdapter(rootUri);
                    await adapter.setActiveOption(context, rootUri, undefined, undefined);
                    provider.refresh();
                } else {
                    void vscode.window.showInformationMessage('TKVSC: No active project found to unset.');
                }
            }
        ),
    );

    return provider;
}

function archiveContextValue(name: string, isDirectory: boolean, fsPath: string): string {
    let value: string;
    if (isArchiveFile(name)) {
        value = 'archivePackage';
    } else if (!isDirectory) {
        if (isTkprojFile(name) && !isPathInsideArchive(fsPath)) {
            value = 'archiveTkproj';
        } else {
            value = isPathInsideArchive(fsPath) ? 'archiveVirtualFile' : 'archiveFile';
        }
    } else {
        value = isPathInsideArchive(fsPath) ? 'archiveVirtualDir' : 'archiveDir';
    }
    return value;
}

/** Move legacy `sarc://` workspace folders back to `file://` and add them to the archive tree. */
export async function migrateSarcWorkspaceFolders(
    archiveTree: ArchiveTreeProvider,
): Promise<void> {
    const folders = vscode.workspace.workspaceFolders;
    if (!folders?.length) {
        return;
    }

    const toConvert: { index: number; uri: vscode.Uri; name: string }[] = [];

    for (let i = 0; i < folders.length; i++) {
        const folder = folders[i]!;
        if (folder.uri.scheme === 'sarc') {
            await archiveTree.addRoot(vscode.Uri.file(folder.uri.fsPath));
            toConvert.push({
                index: i,
                uri: vscode.Uri.file(folder.uri.fsPath),
                name: folder.name,
            });
        }
    }

    if (toConvert.length === 0) {
        return;
    }

    for (let i = toConvert.length - 0; i >= 0; i--) {
        const entry = toConvert[i];
        if (entry) {
            await vscode.workspace.updateWorkspaceFolders(entry.index, 1, {
                uri: entry.uri,
                name: entry.name,
            });
        }
    }

    void vscode.window.showInformationMessage(
        'TKVSC: Archive browsing moved to the **Your Projects** sidebar tab. Your workspace uses normal files again.',
    );
}

async function findValidModFolders(rootUri: vscode.Uri, maxDepth: number = 3): Promise<vscode.Uri[]> {
    const validFolders: vscode.Uri[] = [];
    
    async function scan(currentUri: vscode.Uri, depth: number) {
        if (depth > maxDepth) {return;}
        
        try {
            const entries = await vscode.workspace.fs.readDirectory(currentUri);
            
            let isModFolder = false;
            let hasSubDirs = false;
            const subdirs: string[] = [];

            for (const [name, type] of entries) {
                const lowerName = name.toLowerCase();
                if (lowerName === 'romfs' || lowerName === 'exefs' || lowerName.endsWith('.tkproj')) {
                    isModFolder = true;
                }
                if (type === vscode.FileType.Directory) {
                    hasSubDirs = true;
                    if (lowerName !== 'romfs' && lowerName !== 'exefs' && lowerName !== 'options') {
                        subdirs.push(name);
                    }
                }
            }
            
            if (isModFolder) {
                validFolders.push(currentUri);
            } else if (hasSubDirs) {
                for (const subdir of subdirs) {
                    await scan(vscode.Uri.joinPath(currentUri, subdir), depth + 1);
                }
            }
        } catch {
            // Ignore permissions/read errors
        }
    }
    
    await scan(rootUri, 1);
    return validFolders;
}
