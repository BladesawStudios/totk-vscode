import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { toSarcUri, type ArchiveTreeItem } from './archiveTree';
import { isAampExtension } from './aampExtensions';
import { isPathInsideArchive, isArchiveFile, getDiskArchivePath, isArchiveFileName, isBntxTextureUri, isTxtgFile } from './archives';
import { getDumpSelection, type DumpTreeItem } from './dumpTree';
import { resolveRomfsPath } from './romfs';
import { addDumpEntryToProject, resolveRomfsForProject } from './addToProject';
import { isPathInsideRomfsFolder } from './projectPaths';
import { askForProjectOption, getActiveProjectOption, isAdapterOptionFolderContextValue, isAdapterOptionsContextValue } from './projectAdapters/registry';
import { isFontFilePath } from './fontReplace';
import { renameDiskPath } from './diskFsOps';

let archiveTreeView: vscode.TreeView<ArchiveTreeItem> | undefined;

const CLIPBOARD_KEY = 'totk-editor.archiveClipboard';
const TREE_MIME = 'application/vnd.code.tree.totk-archives';

/**
 * Byte-exact IO backed by the `sarc` file system provider. File operations must never go
 * through `workspace.fs.readFile/writeFile` on `sarc:` URIs: those convert BYML/MSBT/AAMP/...
 * to and from editor text, which corrupts files that are copied, moved or restored.
 */
export interface ArchiveRawIo {
    readStoredBytes(uri: vscode.Uri): Promise<Uint8Array>;
    writeStoredBytes(uri: vscode.Uri, content: Uint8Array): Promise<void>;
    invalidateListings(fsPath?: string): void;
}

let rawIo: ArchiveRawIo | undefined;

export function setArchiveRawIo(io: ArchiveRawIo): void {
    rawIo = io;
}

/** Drop cached archive listings so the tree re-reads archive contents. */
export function invalidateArchiveListings(fsPath?: string): void {
    rawIo?.invalidateListings(fsPath);
}

function requireRawIo(): ArchiveRawIo {
    if (!rawIo) {
        throw new Error('The project file system is not ready yet.');
    }
    return rawIo;
}

export function setArchiveTreeView(view: vscode.TreeView<ArchiveTreeItem>): void {
    archiveTreeView = view;
}

export function getArchiveSelection(): ArchiveTreeItem[] {
    return [...(archiveTreeView?.selection ?? [])];
}

function parentDirectoryUri(uri: vscode.Uri): vscode.Uri {
    return toSarcUri(vscode.Uri.file(path.dirname(uri.fsPath)));
}

function toSarc(uri: vscode.Uri): vscode.Uri {
    return uri.scheme === 'sarc' ? uri : toSarcUri(vscode.Uri.file(uri.fsPath));
}

function refreshArchives(): void {
    void vscode.commands.executeCommand('totk-editor.refreshArchives');
}

function errorMessage(error: unknown): string {
    return error instanceof Error ? error.message : String(error);
}

function isTreeItemArg(value: unknown): value is ArchiveTreeItem {
    return !!value && (value as ArchiveTreeItem).resourceUri instanceof vscode.Uri;
}

/**
 * Items a command should act on. Context menus pass `(clicked, selection)`; keybindings may
 * pass nothing. Acting on the whole selection only when the clicked item is part of it
 * matches the built-in Explorer.
 */
function selectedItems(item?: unknown, selection?: unknown): ArchiveTreeItem[] {
    const clicked = isTreeItemArg(item) ? item : undefined;
    const passed = Array.isArray(selection) ? selection.filter(isTreeItemArg) : [];
    const current = passed.length > 0 ? passed : getArchiveSelection();
    if (!clicked) {
        return current;
    }
    const key = clicked.resourceUri.toString();
    return current.some((entry) => entry.resourceUri.toString() === key) ? current : [clicked];
}

function isMsbtFileName(name: string): boolean {
    return /\.msbt(\.zs)?$/i.test(name);
}

function isBymlFileName(name: string): boolean {
    return /\.(byml|byaml|bgyml)(\.zs)?$/i.test(name);
}

type TemplatePromptConfig = {
    kindLabel: string;
    filters: Record<string, string[]>;
};

function templatePromptConfigForName(name: string): TemplatePromptConfig | undefined {
    if (isMsbtFileName(name)) {
        return {
            kindLabel: 'MSBT',
            filters: { MSBT: ['msbt', 'zs'], All: ['*'] },
        };
    }
    if (isBymlFileName(name)) {
        return {
            kindLabel: 'BYML',
            filters: { BYML: ['byml', 'byaml', 'bgyml', 'zs'], All: ['*'] },
        };
    }
    if (isAampExtension(name)) {
        return {
            kindLabel: 'AAMP',
            filters: { AAMP: ['zs'], All: ['*'] },
        };
    }
    if (isArchiveFileName(name)) {
        const extMatch = name.match(/\.([a-z0-9]+)(\.zs)?$/i);
        const primaryExt = extMatch ? extMatch[1]!.toLowerCase() : 'sarc';
        const label = primaryExt.toUpperCase();
        return {
            kindLabel: label,
            filters: { [label]: [primaryExt, 'zs'], All: ['*'] },
        };
    }
    return undefined;
}

async function initialContentForNewFile(name: string): Promise<Uint8Array | undefined> {
    const promptConfig = templatePromptConfigForName(name);
    if (!promptConfig) {
        return new Uint8Array();
    }

    const isSarc = isArchiveFileName(name);
    const choices: vscode.QuickPickItem[] = [
        {
            label: `Use existing ${promptConfig.kindLabel} as template...`,
            description: `Recommended: creates a valid ${promptConfig.kindLabel} file immediately`,
        },
    ];

    if (!isSarc) {
        choices.push({
            label: 'Create empty file',
            description:
                promptConfig.kindLabel === 'MSBT'
                    ? 'MSBT may not be writable until replaced with a valid template'
                    : 'Creates an empty file and lets converter build from text on first save',
        });
    }

    const choice = await vscode.window.showQuickPick(
        choices,
        {
            title: `New ${promptConfig.kindLabel} file`,
            placeHolder: `Choose how to initialize this ${promptConfig.kindLabel}`,
        },
    );

    if (!choice) {
        return undefined;
    }

    if (choice.label === 'Create empty file') {
        return new Uint8Array();
    }

    const romfsPath = resolveRomfsPath();
    const defaultUri = romfsPath ? vscode.Uri.file(romfsPath) : undefined;

    const picked = await vscode.window.showOpenDialog({
        canSelectMany: false,
        canSelectFiles: true,
        canSelectFolders: false,
        title: `Pick ${promptConfig.kindLabel} template file`,
        filters: promptConfig.filters,
        defaultUri,
    });
    if (!picked?.[0]) {
        return undefined;
    }

    return await fs.promises.readFile(picked[0].fsPath);
}

const MOVABLE_CONTEXTS = new Set([
    'archiveFile',
    'archiveTkproj',
    'archiveVirtualFile',
    'archivePackage',
    'archiveDir',
    'archiveVirtualDir',
    'archiveProjectDir',
    'archiveProjectDirActive',
]);

const FILE_CONTEXTS = new Set(['archiveFile', 'archiveTkproj', 'archiveVirtualFile']);

/** Entries that may be renamed, deleted or cut. Project roots are excluded: use "Remove Project". */
function isMovableItem(item: ArchiveTreeItem): boolean {
    return MOVABLE_CONTEXTS.has(item.contextValue ?? '') || isAdapterOptionsContextValue(item.contextValue);
}

function isCopyableItem(item: ArchiveTreeItem): boolean {
    return isMovableItem(item) || item.contextValue === 'archiveRoot';
}

function isBntxOrTexToGo(uri: vscode.Uri): boolean {
    const fsPath = uri.fsPath;
    return (
        isBntxTextureUri(uri) ||
        isTxtgFile(fsPath) ||
        /\.bntx(\.zs)?$/i.test(fsPath)
    );
}

function normalizeFsPath(fsPath: string): string {
    return fsPath.replace(/\\/g, '/').replace(/\/+$/, '').toLowerCase();
}

function isSameOrInside(childFsPath: string, parentFsPath: string): boolean {
    const child = normalizeFsPath(childFsPath);
    const parent = normalizeFsPath(parentFsPath);
    return child === parent || child.startsWith(`${parent}/`);
}

function pruneNestedSelections(items: ArchiveTreeItem[]): ArchiveTreeItem[] {
    const sorted = [...items].sort(
        (a, b) => normalizeFsPath(a.resourceUri.fsPath).length - normalizeFsPath(b.resourceUri.fsPath).length,
    );
    const kept: ArchiveTreeItem[] = [];
    for (const candidate of sorted) {
        if (!kept.some((entry) => isSameOrInside(candidate.resourceUri.fsPath, entry.resourceUri.fsPath))) {
            kept.push(candidate);
        }
    }
    return kept;
}

function pruneNestedUris(uris: vscode.Uri[]): vscode.Uri[] {
    const sorted = [...uris].sort((a, b) => a.fsPath.length - b.fsPath.length);
    const kept: vscode.Uri[] = [];
    for (const candidate of sorted) {
        if (!kept.some((entry) => isSameOrInside(candidate.fsPath, entry.fsPath))) {
            kept.push(candidate);
        }
    }
    return kept;
}

function folderForItem(target: ArchiveTreeItem): vscode.Uri {
    if (FILE_CONTEXTS.has(target.contextValue ?? '')) {
        return parentDirectoryUri(target.resourceUri);
    }
    return target.resourceUri;
}

function resolveTargetFolder(item?: unknown, selection?: unknown, warn = true): vscode.Uri | undefined {
    const target = selectedItems(item, selection)[0];
    if (!target?.resourceUri) {
        if (warn) {
            void vscode.window.showWarningMessage('Select a folder in Your Projects first.');
        }
        return undefined;
    }
    return folderForItem(target);
}

const INVALID_NAME_CHARS = /[\\/:*?"<>|]/;

function validateEntryName(value: string): string | undefined {
    const name = value.trim();
    if (!name) {
        return 'Name cannot be empty';
    }
    if (INVALID_NAME_CHARS.test(name)) {
        return 'Name cannot contain \\ / : * ? " < > |';
    }
    if (name === '.' || name === '..') {
        return 'Invalid name';
    }
    if (name.toLowerCase().endsWith('.tkproj') && name.toLowerCase() !== '.tkproj') {
        return 'Project file must be named exactly ".tkproj"';
    }
    return undefined;
}

async function pathExists(uri: vscode.Uri): Promise<boolean> {
    return vscode.workspace.fs.stat(toSarc(uri)).then(
        () => true,
        () => false,
    );
}

/** True for real folders. Archive files show as folders in the tree but are copied as files. */
async function isFolderEntry(uri: vscode.Uri): Promise<boolean> {
    const stat = await vscode.workspace.fs.stat(toSarc(uri));
    return stat.type === vscode.FileType.Directory && !isArchiveFile(uri.fsPath);
}

async function findChildCaseInsensitive(folderUri: vscode.Uri, name: string): Promise<string | undefined> {
    try {
        const entries = await vscode.workspace.fs.readDirectory(toSarc(folderUri));
        const lower = name.toLowerCase();
        return entries.find(([entryName]) => entryName.toLowerCase() === lower)?.[0];
    } catch {
        return undefined;
    }
}

async function getUniqueTargetUri(folderUri: vscode.Uri, name: string): Promise<vscode.Uri> {
    const folder = toSarc(folderUri);
    let target = vscode.Uri.joinPath(folder, name);
    if (!(await findChildCaseInsensitive(folder, name))) {
        return target;
    }

    let base = name;
    let ext = '';
    const compoundMatch = name.match(/^(.+?)(\.(?:pack|sarc|genvb|blarc|bfarc|bkres|bntx|byml|byaml|bgyml|msbt|txtg|bfres|ainb|bars|bwav)(?:\.zs)?)$/i);
    if (compoundMatch) {
        base = compoundMatch[1]!;
        ext = compoundMatch[2]!;
    } else {
        const lastDot = name.lastIndexOf('.');
        if (lastDot > 0) {
            base = name.substring(0, lastDot);
            ext = name.substring(lastDot);
        }
    }

    for (let counter = 1; ; counter++) {
        const newName = `${base}_${counter}${ext}`;
        target = vscode.Uri.joinPath(folder, newName);
        if (!(await findChildCaseInsensitive(folder, newName))) {
            return target;
        }
    }
}

async function parallelMap<T, R>(items: T[], fn: (item: T) => Promise<R>, limit = 8): Promise<R[]> {
    const results: R[] = new Array(items.length);
    let index = 0;
    const workers = Array(Math.min(limit, items.length)).fill(0).map(async () => {
        while (index < items.length) {
            const i = index++;
            results[i] = await fn(items[i]!);
        }
    });
    await Promise.all(workers);
    return results;
}

async function createFolder(uri: vscode.Uri): Promise<void> {
    await vscode.workspace.fs.createDirectory(toSarc(uri));
}

async function deleteEntry(uri: vscode.Uri): Promise<void> {
    if (!(await pathExists(uri))) {
        return;
    }
    await vscode.workspace.fs.delete(toSarc(uri), { recursive: true, useTrash: false });
}

/** Byte-exact recursive copy. Works between disk folders, archives and nested archives. */
async function copyEntry(src: vscode.Uri, dest: vscode.Uri, knownFolder?: boolean): Promise<void> {
    const isFolder = knownFolder ?? (await isFolderEntry(src));
    if (isFolder) {
        await createFolder(dest);
        const entries = await vscode.workspace.fs.readDirectory(toSarc(src));
        await parallelMap(entries, ([name, type]) =>
            copyEntry(
                vscode.Uri.joinPath(src, name),
                vscode.Uri.joinPath(dest, name),
                type === vscode.FileType.Directory && !isArchiveFile(name),
            ),
        );
        return;
    }

    const io = requireRawIo();
    if (!isPathInsideArchive(src.fsPath) && !isPathInsideArchive(dest.fsPath)) {
        await fs.promises.mkdir(path.dirname(dest.fsPath), { recursive: true });
        await fs.promises.copyFile(src.fsPath, dest.fsPath);
        // Game dump files are often read-only; the copy should be editable.
        await fs.promises.chmod(dest.fsPath, 0o666).catch(() => undefined);
        io.invalidateListings(dest.fsPath);
        return;
    }
    await io.writeStoredBytes(dest, await io.readStoredBytes(src));
}

async function moveEntry(src: vscode.Uri, dest: vscode.Uri): Promise<void> {
    const srcInside = isPathInsideArchive(src.fsPath);
    const destInside = isPathInsideArchive(dest.fsPath);

    if (!srcInside && !destInside) {
        try {
            await renameDiskPath(src.fsPath, dest.fsPath, false);
            requireRawIo().invalidateListings(src.fsPath);
            requireRawIo().invalidateListings(dest.fsPath);
            return;
        } catch (error) {
            // Moving between drives cannot be a rename; fall back to copy + delete.
            if ((error as { code?: string }).code !== 'EXDEV') {
                throw error;
            }
        }
    } else if (
        srcInside &&
        destInside &&
        getDiskArchivePath(src.fsPath).toLowerCase() === getDiskArchivePath(dest.fsPath).toLowerCase()
    ) {
        try {
            await vscode.workspace.fs.rename(toSarc(src), toSarc(dest), { overwrite: false });
            return;
        } catch {
            // e.g. moving between nested archive levels; fall back to copy + delete.
        }
    }

    await copyEntry(src, dest);
    await deleteEntry(src);
}

/** In-memory copy of an entry's exact bytes, used to undo deletes and overwrites. */
type EntrySnapshot =
    | { kind: 'file'; content: Uint8Array }
    | { kind: 'dir'; children: [string, EntrySnapshot][] };

async function snapshotEntry(uri: vscode.Uri, knownFolder?: boolean): Promise<EntrySnapshot> {
    const isFolder = knownFolder ?? (await isFolderEntry(uri));
    if (!isFolder) {
        return { kind: 'file', content: await requireRawIo().readStoredBytes(uri) };
    }
    const entries = await vscode.workspace.fs.readDirectory(toSarc(uri));
    const children = await parallelMap(entries, async ([name, type]) => [
        name,
        await snapshotEntry(
            vscode.Uri.joinPath(uri, name),
            type === vscode.FileType.Directory && !isArchiveFile(name),
        ),
    ] as [string, EntrySnapshot]);
    return { kind: 'dir', children };
}

async function writeSnapshot(uri: vscode.Uri, snapshot: EntrySnapshot): Promise<void> {
    if (snapshot.kind === 'file') {
        await requireRawIo().writeStoredBytes(uri, snapshot.content);
        return;
    }
    await createFolder(uri);
    await parallelMap(snapshot.children, ([name, child]) => writeSnapshot(vscode.Uri.joinPath(uri, name), child));
}

interface TransferResult {
    done: { src: vscode.Uri; dest: vscode.Uri }[];
    errors: string[];
}

async function transferEntries(sources: vscode.Uri[], destinationFolder: vscode.Uri, move: boolean): Promise<TransferResult> {
    const result: TransferResult = { done: [], errors: [] };
    for (const src of pruneNestedUris(sources)) {
        const name = path.basename(src.fsPath);
        if (isSameOrInside(destinationFolder.fsPath, src.fsPath)) {
            result.errors.push(`${name}: cannot ${move ? 'move' : 'copy'} a folder into itself`);
            continue;
        }
        if (move && normalizeFsPath(path.dirname(src.fsPath)) === normalizeFsPath(destinationFolder.fsPath)) {
            continue; // Already there.
        }
        if (!(await pathExists(src))) {
            result.errors.push(`${name}: no longer exists`);
            continue;
        }
        try {
            const dest = await getUniqueTargetUri(destinationFolder, name);
            if (move) {
                await moveEntry(src, dest);
            } else {
                await copyEntry(src, dest);
            }
            result.done.push({ src, dest });
        } catch (error) {
            result.errors.push(`${name}: ${errorMessage(error)}`);
        }
    }
    return result;
}

interface HistoryEntry {
    description: string;
    undo: () => Promise<void>;
    redo: () => Promise<void>;
}

class ArchiveHistoryManager {
    private undoStack: HistoryEntry[] = [];
    private redoStack: HistoryEntry[] = [];
    private busy = false;

    push(entry: HistoryEntry) {
        this.undoStack.push(entry);
        this.redoStack = [];
    }

    private async run(from: HistoryEntry[], to: HistoryEntry[], kind: 'undo' | 'redo'): Promise<void> {
        if (this.busy) {
            return;
        }
        const entry = from.pop();
        if (!entry) {
            void vscode.window.showInformationMessage(kind === 'undo' ? 'Nothing to undo' : 'Nothing to redo');
            return;
        }
        this.busy = true;
        try {
            await vscode.window.withProgress(
                { location: vscode.ProgressLocation.Window, title: `${kind === 'undo' ? 'Undoing' : 'Redoing'} ${entry.description}...` },
                () => (kind === 'undo' ? entry.undo() : entry.redo()),
            );
            to.push(entry);
            void vscode.window.showInformationMessage(`${kind === 'undo' ? 'Undid' : 'Redid'}: ${entry.description}`);
        } catch (error) {
            void vscode.window.showErrorMessage(`${kind === 'undo' ? 'Undo' : 'Redo'} failed: ${errorMessage(error)}`);
            from.push(entry);
        } finally {
            this.busy = false;
            refreshArchives();
        }
    }

    undo() {
        return this.run(this.undoStack, this.redoStack, 'undo');
    }

    redo() {
        return this.run(this.redoStack, this.undoStack, 'redo');
    }
}

const historyManager = new ArchiveHistoryManager();

function describeCount(names: string[]): string {
    return names.length === 1 ? names[0]! : `${names.length} items`;
}

function reportTransferErrors(verb: string, errors: string[]): void {
    if (errors.length === 0) {
        return;
    }
    void vscode.window.showErrorMessage(
        `${verb} failed for ${errors.length} item(s): ${errors.slice(0, 3).join('; ')}${errors.length > 3 ? '; ...' : ''}`,
    );
}

/** Copy or move entries into a folder, recording undo history. */
async function pasteInto(sources: vscode.Uri[], folderUri: vscode.Uri, move: boolean, verb: string): Promise<TransferResult> {
    const label = describeCount(sources.map((uri) => path.basename(uri.fsPath)));
    const result = await vscode.window.withProgress(
        {
            location: vscode.ProgressLocation.Notification,
            title: `${move ? 'Moving' : 'Copying'} ${label}...`,
            cancellable: false,
        },
        () => transferEntries(sources, folderUri, move),
    );
    const done = result.done;
    if (done.length > 0) {
        const description = `${verb} ${describeCount(done.map((entry) => path.basename(entry.src.fsPath)))}`;
        historyManager.push(
            move
                ? {
                    description,
                    undo: async () => {
                        for (const entry of [...done].reverse()) {
                            await moveEntry(entry.dest, entry.src);
                        }
                    },
                    redo: async () => {
                        for (const entry of done) {
                            await moveEntry(entry.src, entry.dest);
                        }
                    },
                }
                : {
                    description,
                    undo: async () => {
                        for (const entry of done) {
                            await deleteEntry(entry.dest);
                        }
                    },
                    redo: async () => {
                        for (const entry of done) {
                            await copyEntry(entry.src, entry.dest);
                        }
                    },
                },
        );
    }
    reportTransferErrors(verb, result.errors);
    refreshArchives();
    return result;
}

export function registerArchiveFileCommands(context: vscode.ExtensionContext): void {
    const initialClipboard = context.workspaceState.get<{ uri: string; move: boolean }[]>(CLIPBOARD_KEY, []);
    void vscode.commands.executeCommand(
        'setContext',
        'totk-editor.archiveClipboardNotEmpty',
        initialClipboard.length > 0,
    );

    const setClipboard = async (uris: vscode.Uri[], move: boolean): Promise<void> => {
        await context.workspaceState.update(
            CLIPBOARD_KEY,
            uris.map((uri) => ({ uri: uri.toString(), move })),
        );
        await vscode.commands.executeCommand('setContext', 'totk-editor.archiveClipboardNotEmpty', uris.length > 0);
    };

    context.subscriptions.push(
        vscode.commands.registerCommand(
            'totk-editor.archiveDelete',
            async (item?: unknown, selection?: unknown) => {
                const items = pruneNestedSelections(selectedItems(item, selection).filter(isMovableItem));
                if (items.length === 0) {
                    return;
                }
                const label = describeCount(items.map((entry) => entry.entryName));
                const confirm = await vscode.window.showWarningMessage(
                    `Delete ${label}?`,
                    { modal: true, detail: 'You can undo this with Ctrl+Z in Your Projects while VS Code stays open.' },
                    'Delete',
                );
                if (confirm !== 'Delete') {
                    return;
                }
                const deleted: { uri: vscode.Uri; snapshot: EntrySnapshot }[] = [];
                const errors: string[] = [];
                await vscode.window.withProgress(
                    {
                        location: vscode.ProgressLocation.Notification,
                        title: `Deleting ${label}...`,
                        cancellable: false,
                    },
                    async () => {
                        for (const entry of items) {
                            const uri = entry.resourceUri;
                            try {
                                if (!(await pathExists(uri))) {
                                    continue;
                                }
                                // Never delete something we could not back up for undo.
                                const snapshot = await snapshotEntry(uri);
                                await deleteEntry(uri);
                                deleted.push({ uri, snapshot });
                            } catch (error) {
                                errors.push(`${entry.entryName}: ${errorMessage(error)}`);
                            }
                        }
                    },
                );
                if (deleted.length > 0) {
                    historyManager.push({
                        description: `Delete ${describeCount(deleted.map((entry) => path.basename(entry.uri.fsPath)))}`,
                        undo: async () => {
                            for (const entry of deleted) {
                                await writeSnapshot(entry.uri, entry.snapshot);
                            }
                        },
                        redo: async () => {
                            for (const entry of deleted) {
                                await deleteEntry(entry.uri);
                            }
                        },
                    });
                }
                reportTransferErrors('Delete', errors);
                refreshArchives();
            },
        ),
    );

    context.subscriptions.push(
        vscode.commands.registerCommand(
            'totk-editor.archiveRename',
            async (item?: unknown, selection?: unknown) => {
                const entry = selectedItems(item, selection)[0];
                if (!entry?.resourceUri || !isMovableItem(entry)) {
                    return;
                }
                if (entry.entryName.toLowerCase().endsWith('.tkproj')) {
                    void vscode.window.showErrorMessage('Project files must be named exactly ".tkproj" and cannot be renamed.');
                    return;
                }
                const sourceUri = toSarc(entry.resourceUri);
                const parentUri = parentDirectoryUri(sourceUri);
                const dotIndex = entry.entryName.indexOf('.', 1);
                const rawName = await vscode.window.showInputBox({
                    prompt: 'New name',
                    value: entry.entryName,
                    valueSelection: [0, dotIndex > 0 ? dotIndex : entry.entryName.length],
                    validateInput: validateEntryName,
                });
                const newName = rawName?.trim();
                if (!newName || newName === entry.entryName) {
                    return;
                }
                const isCaseOnly = newName.toLowerCase() === entry.entryName.toLowerCase();
                if (!isCaseOnly && (await findChildCaseInsensitive(parentUri, newName))) {
                    void vscode.window.showErrorMessage(`"${newName}" already exists in this folder.`);
                    return;
                }
                const target = vscode.Uri.joinPath(parentUri, newName);
                const updateInfoJson = async (folderUri: vscode.Uri, name: string) => {
                    if (!isAdapterOptionFolderContextValue(entry.contextValue)) {
                        return;
                    }
                    const infoPath = path.join(folderUri.fsPath, 'info.json');
                    try {
                        const infoText = await fs.promises.readFile(infoPath, 'utf8');
                        const infoData = JSON.parse(infoText);
                        if (infoData.Name !== undefined) {
                            infoData.Name = name;
                            const indent = /^\{\s*\n([ \t]+)/.exec(infoText)?.[1] ?? '';
                            await fs.promises.writeFile(infoPath, JSON.stringify(infoData, null, indent || undefined));
                        }
                    } catch {
                        // Missing or unreadable info.json: nothing to keep in sync.
                    }
                };
                const doRename = async (from: vscode.Uri, to: vscode.Uri, name: string) => {
                    await moveEntry(from, to);
                    await updateInfoJson(to, name);
                };
                try {
                    await doRename(sourceUri, target, newName);
                    historyManager.push({
                        description: `Rename ${entry.entryName} to ${newName}`,
                        undo: () => doRename(target, sourceUri, entry.entryName),
                        redo: () => doRename(sourceUri, target, newName),
                    });
                } catch (error) {
                    void vscode.window.showErrorMessage(`Rename failed: ${errorMessage(error)}`);
                }
                refreshArchives();
            },
        ),
    );

    context.subscriptions.push(
        vscode.commands.registerCommand(
            'totk-editor.archiveNewFile',
            async (item?: unknown, selection?: unknown) => {
                const folderUri = resolveTargetFolder(item, selection);
                if (!folderUri) {
                    return;
                }
                const rawName = await vscode.window.showInputBox({
                    prompt: 'New file name',
                    validateInput: validateEntryName,
                });
                const name = rawName?.trim();
                if (!name) {
                    return;
                }
                if (await findChildCaseInsensitive(folderUri, name)) {
                    void vscode.window.showErrorMessage(`"${name}" already exists in this folder.`);
                    return;
                }
                const target = vscode.Uri.joinPath(toSarc(folderUri), name);
                try {
                    const initial = await initialContentForNewFile(name);
                    if (initial === undefined) {
                        return;
                    }
                    await requireRawIo().writeStoredBytes(target, initial);
                    historyManager.push({
                        description: `Create file ${name}`,
                        undo: () => deleteEntry(target),
                        redo: () => requireRawIo().writeStoredBytes(target, initial),
                    });
                    refreshArchives();
                    if (!isArchiveFileName(name)) {
                        await vscode.commands.executeCommand('vscode.open', target);
                    }
                } catch (error) {
                    void vscode.window.showErrorMessage(`Create file failed: ${errorMessage(error)}`);
                }
            },
        ),
    );

    context.subscriptions.push(
        vscode.commands.registerCommand(
            'totk-editor.archiveNewFolder',
            async (item?: unknown, selection?: unknown) => {
                const folderUri = resolveTargetFolder(item, selection);
                if (!folderUri) {
                    return;
                }
                const rawName = await vscode.window.showInputBox({
                    prompt: 'New folder name',
                    validateInput: validateEntryName,
                });
                const name = rawName?.trim();
                if (!name) {
                    return;
                }
                if (await findChildCaseInsensitive(folderUri, name)) {
                    void vscode.window.showErrorMessage(`"${name}" already exists in this folder.`);
                    return;
                }
                const target = vscode.Uri.joinPath(toSarc(folderUri), name);
                try {
                    await createFolder(target);
                    if (isPathInsideArchive(target.fsPath)) {
                        void vscode.window.showInformationMessage(
                            'Archives cannot store empty folders. Add a file to this folder or it will disappear when VS Code restarts.',
                        );
                    }
                    historyManager.push({
                        description: `Create folder ${name}`,
                        undo: () => deleteEntry(target),
                        redo: () => createFolder(target),
                    });
                    refreshArchives();
                } catch (error) {
                    void vscode.window.showErrorMessage(`Create folder failed: ${errorMessage(error)}`);
                }
            },
        ),
    );

    context.subscriptions.push(
        vscode.commands.registerCommand('totk-editor.archiveCopy', async (item?: unknown, selection?: unknown) => {
            let items: (ArchiveTreeItem | DumpTreeItem)[];
            const fromDump =
                (item as { source?: string } | undefined)?.source === 'gameDump' ||
                (isTreeItemArg(item) && (item.contextValue ?? '').startsWith('dump'));
            if (fromDump) {
                const dumpSelection = getDumpSelection();
                const clicked = isTreeItemArg(item) ? (item as unknown as DumpTreeItem) : undefined;
                items = clicked && !dumpSelection.some((entry) => entry.resourceUri.toString() === clicked.resourceUri.toString())
                    ? [clicked]
                    : dumpSelection;
            } else {
                items = selectedItems(item, selection).filter(isCopyableItem);
            }
            const uris = items.filter((entry) => entry?.resourceUri).map((entry) => entry.resourceUri);
            if (uris.length === 0) {
                return;
            }
            await setClipboard(uris, false);
            vscode.window.setStatusBarMessage(`Copied ${describeCount(uris.map((uri) => path.basename(uri.fsPath)))}`, 3000);
        }),
    );

    context.subscriptions.push(
        vscode.commands.registerCommand('totk-editor.archiveCut', async (item?: unknown, selection?: unknown) => {
            const uris = selectedItems(item, selection).filter(isMovableItem).map((entry) => entry.resourceUri);
            if (uris.length === 0) {
                return;
            }
            await setClipboard(uris, true);
            vscode.window.setStatusBarMessage(`Cut ${describeCount(uris.map((uri) => path.basename(uri.fsPath)))}`, 3000);
        }),
    );

    context.subscriptions.push(
        vscode.commands.registerCommand('totk-editor.archivePaste', async (item?: unknown, selection?: unknown) => {
            const clipboard = context.workspaceState.get<{ uri: string; move: boolean }[]>(CLIPBOARD_KEY, []);
            if (clipboard.length === 0) {
                return;
            }
            let folderUri: vscode.Uri | undefined;
            if (item instanceof vscode.Uri) {
                // Invoked from the built-in Explorer.
                folderUri = (await isFolderEntry(item).catch(() => false))
                    ? item
                    : vscode.Uri.file(path.dirname(item.fsPath));
            } else {
                folderUri = resolveTargetFolder(item, selection, false);
            }
            if (!folderUri) {
                const activeEditor = vscode.window.activeTextEditor;
                if (activeEditor && activeEditor.document.uri.scheme === 'file') {
                    folderUri = vscode.Uri.file(path.dirname(activeEditor.document.uri.fsPath));
                } else if (vscode.workspace.workspaceFolders && vscode.workspace.workspaceFolders.length > 0) {
                    folderUri = vscode.workspace.workspaceFolders[0]!.uri;
                }
            }
            if (!folderUri) {
                void vscode.window.showWarningMessage('Select a folder to paste into.');
                return;
            }

            const sources = clipboard.map((entry) => vscode.Uri.parse(entry.uri));
            const isMove = clipboard[0]!.move;
            const result = await pasteInto(sources, folderUri, isMove, isMove ? 'Move' : 'Copy');
            if (isMove && result.done.length > 0) {
                await setClipboard([], false);
            }
        }),
    );

    context.subscriptions.push(
        vscode.commands.registerCommand('totk-editor.archiveUndo', () => historyManager.undo()),
        vscode.commands.registerCommand('totk-editor.archiveRedo', () => historyManager.redo()),
    );

    context.subscriptions.push(
        vscode.commands.registerCommand(
            'totk-editor.archiveImportFile',
            async (item?: unknown, selection?: unknown) => {
                const folderUri = resolveTargetFolder(item, selection);
                if (!folderUri) {
                    return;
                }
                if (isBntxOrTexToGo(folderUri)) {
                    void vscode.window.showWarningMessage('Import is not supported for BNTX or TexToGo containers.');
                    return;
                }
                const picked = await vscode.window.showOpenDialog({
                    canSelectMany: true,
                    canSelectFiles: true,
                    canSelectFolders: false,
                    title: 'Select Files to Import',
                });
                if (!picked?.length) {
                    return;
                }
                await importEntries(picked, folderUri);
            },
        ),
    );

    context.subscriptions.push(
        vscode.commands.registerCommand(
            'totk-editor.archiveImportFolder',
            async (item?: unknown, selection?: unknown) => {
                const folderUri = resolveTargetFolder(item, selection);
                if (!folderUri) {
                    return;
                }
                if (isBntxOrTexToGo(folderUri)) {
                    void vscode.window.showWarningMessage('Import is not supported for BNTX or TexToGo containers.');
                    return;
                }
                const picked = await vscode.window.showOpenDialog({
                    canSelectMany: true,
                    canSelectFiles: false,
                    canSelectFolders: true,
                    title: 'Select Folders to Import',
                });
                if (!picked?.length) {
                    return;
                }
                await importEntries(picked, folderUri);
            },
        ),
    );

    context.subscriptions.push(
        vscode.commands.registerCommand(
            'totk-editor.archiveReplaceFile',
            async (item?: unknown, selection?: unknown) => {
                const entry = selectedItems(item, selection)[0];
                if (!entry?.resourceUri || !isMovableItem(entry)) {
                    return;
                }
                const targetUri = toSarc(entry.resourceUri);
                if (isBntxOrTexToGo(targetUri)) {
                    await vscode.commands.executeCommand('totk-editor.importTextureDds', targetUri);
                    return;
                }
                if (isFontFilePath(targetUri.fsPath)) {
                    await vscode.commands.executeCommand('totk-editor.importFontReplacement', targetUri);
                    return;
                }

                const picked = await vscode.window.showOpenDialog({
                    canSelectMany: false,
                    canSelectFiles: true,
                    canSelectFolders: false,
                    title: `Select Replacement for ${entry.entryName}`,
                });
                if (!picked?.[0]) {
                    return;
                }
                const srcPath = picked[0].fsPath;

                try {
                    await vscode.window.withProgress(
                        {
                            location: vscode.ProgressLocation.Notification,
                            title: `Replacing file ${entry.entryName}...`,
                            cancellable: false,
                        },
                        async () => {
                            const io = requireRawIo();
                            const oldContent = await io.readStoredBytes(targetUri);
                            const newContent = await fs.promises.readFile(srcPath);
                            await io.writeStoredBytes(targetUri, newContent);
                            historyManager.push({
                                description: `Replace file ${entry.entryName}`,
                                undo: () => io.writeStoredBytes(targetUri, oldContent),
                                redo: () => io.writeStoredBytes(targetUri, newContent),
                            });
                        },
                    );
                    void vscode.window.showInformationMessage(`Replaced ${entry.entryName}.`);
                } catch (error) {
                    void vscode.window.showErrorMessage(`Replace file failed: ${errorMessage(error)}`);
                }
                refreshArchives();
            },
        ),
    );

    context.subscriptions.push(
        vscode.commands.registerCommand(
            'totk-editor.archiveReplaceFolder',
            async (item?: unknown, selection?: unknown) => {
                const entry = selectedItems(item, selection)[0];
                if (!entry?.resourceUri || !isMovableItem(entry)) {
                    return;
                }
                const targetUri = toSarc(entry.resourceUri);
                if (isBntxOrTexToGo(targetUri)) {
                    void vscode.window.showWarningMessage('Replace is not supported for BNTX or TexToGo folders.');
                    return;
                }

                const picked = await vscode.window.showOpenDialog({
                    canSelectMany: false,
                    canSelectFiles: false,
                    canSelectFolders: true,
                    title: `Select Replacement Folder for ${entry.entryName}`,
                });
                if (!picked?.[0]) {
                    return;
                }
                const srcUri = picked[0];
                if (isSameOrInside(srcUri.fsPath, targetUri.fsPath) || isSameOrInside(targetUri.fsPath, srcUri.fsPath)) {
                    void vscode.window.showErrorMessage('The replacement folder cannot be inside (or contain) the folder being replaced.');
                    return;
                }

                try {
                    await vscode.window.withProgress(
                        {
                            location: vscode.ProgressLocation.Notification,
                            title: `Replacing folder ${entry.entryName}...`,
                            cancellable: false,
                        },
                        async () => {
                            const oldSnapshot = await snapshotEntry(targetUri, true);
                            const newSnapshot = await snapshotEntry(srcUri, true);
                            const replaceWith = async (snapshot: EntrySnapshot) => {
                                await deleteEntry(targetUri);
                                await writeSnapshot(targetUri, snapshot);
                            };
                            await replaceWith(newSnapshot);
                            historyManager.push({
                                description: `Replace folder ${entry.entryName}`,
                                undo: () => replaceWith(oldSnapshot),
                                redo: () => replaceWith(newSnapshot),
                            });
                        },
                    );
                    void vscode.window.showInformationMessage(`Replaced folder ${entry.entryName}.`);
                } catch (error) {
                    void vscode.window.showErrorMessage(`Replace folder failed: ${errorMessage(error)}`);
                }
                refreshArchives();
            },
        ),
    );

    const doArchiveAddToOption = async (item: unknown, selection: unknown, useActive: boolean) => {
        const items = selectedItems(item, selection).filter((entry) =>
            isPathInsideRomfsFolder(entry.resourceUri.fsPath),
        );
        if (items.length === 0) {
            return;
        }

        const firstItem = items[0]!;
        const diskArchive = getDiskArchivePath(firstItem.resourceUri.fsPath);

        let projectRoot = diskArchive;
        let foundRoot = false;
        let current = diskArchive;
        while (current) {
            try {
                if (fs.existsSync(path.join(current, 'options')) || fs.existsSync(path.join(current, '.tkproj')) || fs.existsSync(path.join(current, 'romfs')) || fs.existsSync(path.join(current, 'exefs'))) {
                    projectRoot = current;
                    foundRoot = true;
                    break;
                }
            } catch {
                // Ignore permissions errors etc.
            }
            const parent = path.dirname(current);
            if (parent === current) {break;}
            current = parent;
        }

        if (!foundRoot) {
            void vscode.window.showErrorMessage('Add to Option requires the project to be a valid mod folder (must contain romfs, exefs, or .tkproj).');
            return;
        }

        const romfsRoot = resolveRomfsForProject(projectRoot);

        let tkmmOption: { group: string; option: string } | undefined;
        if (useActive) {
            tkmmOption = getActiveProjectOption(context, projectRoot);
            if (!tkmmOption) {
                void vscode.window.showWarningMessage('No active option selected. Select an active option first.');
                return;
            }
        } else {
            const result = await askForProjectOption(projectRoot);
            if (!result || result === 'BACK') {
                return; // Cancelled
            }
            if (result !== 'BASE_PROJECT') {
                tkmmOption = result;
            } else {
                void vscode.window.showWarningMessage('Cannot add to base project from this menu.');
                return;
            }
        }

        if (!tkmmOption) {
            return;
        }

        let addedCount = 0;
        await vscode.window.withProgress(
            {
                location: vscode.ProgressLocation.Notification,
                title: `Adding ${items.length} items to Option...`,
                cancellable: false,
            },
            async () => {
                for (const entry of items) {
                    const success = await addDumpEntryToProject(
                        entry.resourceUri.fsPath,
                        projectRoot,
                        romfsRoot,
                        { suppressSuccessMessage: true },
                        tkmmOption
                    );
                    if (success) {addedCount++;}
                }
            }
        );

        if (addedCount > 0) {
            void vscode.window.showInformationMessage(`Added ${addedCount} item(s) to option ${tkmmOption.group}/${tkmmOption.option}.`);
            refreshArchives();
        }
    };

    context.subscriptions.push(
        vscode.commands.registerCommand('totk-editor.archiveAddToOption', (item?: unknown, selection?: unknown) => doArchiveAddToOption(item, selection, false)),
        vscode.commands.registerCommand('totk-editor.archiveAddToActiveOption', (item?: unknown, selection?: unknown) => doArchiveAddToOption(item, selection, true))
    );
}

/** Import files/folders from outside the project, asking before overwriting existing entries. */
async function importEntries(sources: vscode.Uri[], folderUri: vscode.Uri): Promise<void> {
    const folder = toSarc(folderUri);
    const plans: { src: vscode.Uri; dest: vscode.Uri; previous?: EntrySnapshot }[] = [];
    for (const src of pruneNestedUris(sources)) {
        const name = path.basename(src.fsPath);
        const dest = vscode.Uri.joinPath(folder, name);
        if (isBntxTextureUri(dest)) {
            void vscode.window.showWarningMessage(`Cannot import ${name} into a BNTX or TexToGo container.`);
            continue;
        }
        if (isSameOrInside(folder.fsPath, src.fsPath)) {
            void vscode.window.showErrorMessage(`Cannot import ${name} into itself.`);
            continue;
        }
        const existingName = await findChildCaseInsensitive(folder, name);
        if (existingName) {
            const choice = await vscode.window.showWarningMessage(
                `"${existingName}" already exists. Replace it?`,
                { modal: true },
                'Replace',
                'Keep Both',
            );
            if (choice === 'Keep Both') {
                plans.push({ src, dest: await getUniqueTargetUri(folder, name) });
            } else if (choice === 'Replace') {
                plans.push({ src, dest: vscode.Uri.joinPath(folder, existingName), previous: undefined });
            }
            continue;
        }
        plans.push({ src, dest });
    }
    if (plans.length === 0) {
        return;
    }

    const label = describeCount(plans.map((plan) => path.basename(plan.src.fsPath)));
    const done: typeof plans = [];
    const errors: string[] = [];
    await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: `Importing ${label}...`, cancellable: false },
        async () => {
            for (const plan of plans) {
                try {
                    if (await pathExists(plan.dest)) {
                        plan.previous = await snapshotEntry(plan.dest);
                        await deleteEntry(plan.dest);
                    }
                    await copyEntry(plan.src, plan.dest);
                    done.push(plan);
                } catch (error) {
                    errors.push(`${path.basename(plan.src.fsPath)}: ${errorMessage(error)}`);
                }
            }
        },
    );
    if (done.length > 0) {
        historyManager.push({
            description: `Import ${describeCount(done.map((plan) => path.basename(plan.src.fsPath)))}`,
            undo: async () => {
                for (const plan of done) {
                    await deleteEntry(plan.dest);
                    if (plan.previous) {
                        await writeSnapshot(plan.dest, plan.previous);
                    }
                }
            },
            redo: async () => {
                for (const plan of done) {
                    await deleteEntry(plan.dest);
                    await copyEntry(plan.src, plan.dest);
                }
            },
        });
        void vscode.window.showInformationMessage(`Imported ${describeCount(done.map((plan) => path.basename(plan.src.fsPath)))}.`);
    }
    reportTransferErrors('Import', errors);
    refreshArchives();
}

export class ArchiveTreeDragDrop
    implements vscode.TreeDragAndDropController<ArchiveTreeItem>
{
    readonly dropMimeTypes = [TREE_MIME, 'text/uri-list'];
    readonly dragMimeTypes = [TREE_MIME, 'text/uri-list'];

    async handleDrag(
        source: readonly ArchiveTreeItem[],
        dataTransfer: vscode.DataTransfer,
        _token: vscode.CancellationToken,
    ): Promise<void> {
        const movable = source.filter(isMovableItem);
        if (movable.length === 0) {
            return;
        }
        dataTransfer.set(
            TREE_MIME,
            new vscode.DataTransferItem(movable.map((item) => item.resourceUri.toString())),
        );
    }

    async handleDrop(
        target: ArchiveTreeItem | undefined,
        dataTransfer: vscode.DataTransfer,
        _token: vscode.CancellationToken,
    ): Promise<void> {
        if (!target?.resourceUri) {
            return;
        }
        const folderUri = folderForItem(target);

        const internal = dataTransfer.get(TREE_MIME);
        if (internal) {
            const uris = (internal.value as string[]).map((value) => vscode.Uri.parse(value));
            await pasteInto(uris, folderUri, true, 'Move');
            return;
        }

        // Files dropped from the OS file manager are copied in.
        const external = dataTransfer.get('text/uri-list');
        if (!external) {
            return;
        }
        const text = await external.asString();
        const uris = text
            .split(/\r?\n/)
            .map((line) => line.trim())
            .filter((line) => line && !line.startsWith('#'))
            .map((line) => vscode.Uri.parse(line))
            .filter((uri) => uri.scheme === 'file');
        if (uris.length > 0) {
            await importEntries(uris, folderUri);
        }
    }
}
