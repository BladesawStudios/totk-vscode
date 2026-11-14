import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { getDiskArchivePath, isArchiveFile, isPathInsideArchive } from './archives';
import { runBridgeJsonAsync } from './bridge';
import { logger } from './logger';
import { resolveProjectRomfsMount } from './projectPaths';

/** The theme colour (contributed in package.json) that modified files are drawn in. */
export const MODIFIED_COLOR_ID = 'TKVSC.modifiedResourceForeground';

/** What the marker needs of a tree item. */
export interface MarkableItem {
    resourceUri: vscode.Uri;
    collapsibleState?: vscode.TreeItemCollapsibleState;
}

export interface ModifiedFileMarkerDeps {
    getHost(): string;
    getEnv(): NodeJS.ProcessEnv;
    /** The active game's RomFS dump. */
    getRomfs(): string;
    /** Where the active game's file hashes are kept. */
    getCachePath(): string;
    isEnabled(): boolean;
}

interface ModifiedFilesResult {
    files?: string[];
    archives?: Record<string, string[]>;
}

function stampOf(filePath: string): string | undefined {
    try {
        const stat = fs.statSync(filePath);
        return `${stat.mtimeMs}:${stat.size}`;
    } catch {
        return undefined;
    }
}

/** True for things under a `romfs` folder (not the folder itself): only those are ever marked. */
function isWithinRomfsFolder(fsPath: string): boolean {
    const parts = fsPath.split(/[\\/]/).filter((part) => part.length > 0);
    const romfsIndex = parts.findIndex((part) => part.toLowerCase() === 'romfs');
    return romfsIndex >= 0 && romfsIndex < parts.length - 1;
}

/** Where the game's RomFS has a file of the project, relative to it, or undefined when the file is not under the project's RomFS. */
function relativeToMount(filePath: string, fallbackProjectRoot: string, romfs: string): string | undefined {
    const parts = filePath.split(/[\\/]/);
    const romfsIndex = parts.findIndex((part) => part.toLowerCase() === 'romfs');
    const mount = romfsIndex >= 0
        ? parts.slice(0, romfsIndex + 1).join(path.sep)
        : resolveProjectRomfsMount(fallbackProjectRoot, romfs);
    const rel = path.relative(mount, filePath);
    if (!rel || rel.startsWith('..') || path.isAbsolute(rel)) {
        return undefined;
    }
    return rel.replace(/\\/g, '/');
}

/**
 * The game's copy of a project file (which may be inside an archive, so the path may go through one), or undefined when the
 * file isn't under the project's RomFS or is the game's own file.
 */
export function gameCopyPath(filePath: string, fallbackProjectRoot: string, romfs: string): string | undefined {
    const rel = relativeToMount(filePath, fallbackProjectRoot, romfs);
    if (rel === undefined) {
        return undefined;
    }
    const game = path.join(romfs, rel);
    return path.normalize(game).toLowerCase() === path.normalize(filePath).toLowerCase() ? undefined : game;
}

function cacheKey(filePath: string): string {
    return path.normalize(filePath).toLowerCase();
}

/**
 * Marks the files of a mod project whose content differs from the game's: loose files, and files inside archives (nested ones
 * too). The host compares with the game's own copy of each file, so only the game's files that a project has are ever read, and their
 * hashes are kept per game for next time. The marker follows the active game.
 *
 * Marked files are shown like git shows changed files: their name in a colour of their own, with a dot on the right. Folders
 * inside archives are marked the same way when anything under them is; folders on disk never are.
 */
export class ModifiedFileMarker implements vscode.FileDecorationProvider {
    // Results are kept by file and checked against the file's modified time and size.
    private readonly files = new Map<string, { stamp: string; modified: boolean }>();
    private readonly archives = new Map<string, { stamp: string; modified: Set<string> }>();
    /** The tree items' URIs currently drawn as modified, and whether each is a folder. */
    private readonly flagged = new Map<string, { uri: vscode.Uri; folder: boolean }>();
    private readonly onDidChangeFileDecorationsEmitter = new vscode.EventEmitter<vscode.Uri[]>();
    readonly onDidChangeFileDecorations = this.onDidChangeFileDecorationsEmitter.event;
    constructor(private readonly deps: ModifiedFileMarkerDeps) {}

    provideFileDecoration(uri: vscode.Uri): vscode.FileDecoration | undefined {
        const flag = this.flagged.get(uri.toString());
        if (!flag) {
            return undefined;
        }
        return new vscode.FileDecoration(
            '●',
            flag.folder ? "Contains files that differ from the game's" : "Differs from the game's copy",
            new vscode.ThemeColor(MODIFIED_COLOR_ID),
        );
    }

    /** Forgets every result, for when the game or its dump changes. */
    invalidate(): void {
        this.files.clear();
        this.archives.clear();
        const cleared = [...this.flagged.values()].map((flag) => flag.uri);
        this.flagged.clear();
        if (cleared.length > 0) {
            this.onDidChangeFileDecorationsEmitter.fire(cleared);
        }
    }

    /** Draws (or stops drawing) items as modified, and tells VS Code about the ones that changed. */
    private applyFlags(updates: ReadonlyArray<{ item: MarkableItem; modified: boolean; folder: boolean }>): void {
        const changed: vscode.Uri[] = [];
        for (const { item, modified, folder } of updates) {
            const key = item.resourceUri.toString();
            const existing = this.flagged.get(key);
            if (modified && (!existing || existing.folder !== folder)) {
                this.flagged.set(key, { uri: item.resourceUri, folder });
                changed.push(item.resourceUri);
            } else if (!modified && existing) {
                this.flagged.delete(key);
                changed.push(item.resourceUri);
            }
        }
        if (changed.length > 0) {
            this.onDidChangeFileDecorationsEmitter.fire(changed);
        }
    }

    dispose(): void {
        this.onDidChangeFileDecorationsEmitter.dispose();
    }

    /**
     * Marks the items that are known to be modified now. The ones not yet compared are compared in the background,
     * and `onChanged` is called once if that found modified files.
     */
    mark(items: readonly MarkableItem[], fallbackProjectRoot: string, onChanged: () => void): void {
        const romfs = this.deps.getRomfs();
        const cachePath = this.deps.getCachePath();
        if (!this.deps.isEnabled() || !romfs || !this.deps.getHost()) {
            return;
        }

        const wantedFiles = new Map<string, { item: MarkableItem; rel: string }>();
        const wantedArchives = new Map<string, { rel: string }>();
        const inside: Array<{ item: MarkableItem; disk: string; inner: string }> = [];

        for (const item of items) {
            const fsPath = item.resourceUri.fsPath;
            if (!isWithinRomfsFolder(fsPath)) {
                continue;
            }
            if (isPathInsideArchive(fsPath)) {
                const disk = getDiskArchivePath(fsPath);
                const rel = this.relativeToMount(disk, fallbackProjectRoot, romfs);
                if (rel === undefined) {
                    continue;
                }
                inside.push({ item, disk, inner: fsPath.slice(disk.length).replace(/\\/g, '/').replace(/^\/+/, '') });
                wantedArchives.set(disk, { rel });
            } else if (isArchiveFile(path.basename(fsPath)) || item.collapsibleState === undefined || item.collapsibleState === vscode.TreeItemCollapsibleState.None) {
                const rel = this.relativeToMount(fsPath, fallbackProjectRoot, romfs);
                if (rel !== undefined) {
                    wantedFiles.set(fsPath, { item, rel });
                }
            }
        }

        const updates: Array<{ item: MarkableItem; modified: boolean; folder: boolean }> = [];

        const missingFiles: Array<{ path: string; rel: string }> = [];
        for (const [file, { item, rel }] of wantedFiles) {
            const cached = this.files.get(cacheKey(file));
            if (cached && cached.stamp === stampOf(file)) {
                updates.push({ item, modified: cached.modified, folder: false });
            } else {
                missingFiles.push({ path: file, rel });
            }
        }

        const missingArchives: Array<{ path: string; rel: string }> = [];
        for (const [disk, { rel }] of wantedArchives) {
            const cached = this.archives.get(cacheKey(disk));
            if (!cached || cached.stamp !== stampOf(disk)) {
                missingArchives.push({ path: disk, rel });
            }
        }
        for (const { item, disk, inner } of inside) {
            const cached = this.archives.get(cacheKey(disk));
            if (cached && cached.stamp === stampOf(disk)) {
                // A folder (or nested archive) inside the archive is modified when anything under it is.
                const key = inner.toLowerCase();
                const prefix = `${key}/`;
                const isFolder = item.collapsibleState !== undefined && item.collapsibleState !== vscode.TreeItemCollapsibleState.None;
                const modified = cached.modified.has(key) || (isFolder && [...cached.modified].some((entry) => entry.startsWith(prefix)));
                updates.push({ item, modified, folder: isFolder && !cached.modified.has(key) });
            }
        }
        this.applyFlags(updates);

        if (missingFiles.length > 0 || missingArchives.length > 0) {
            void this.compare(cachePath, missingFiles, missingArchives)
                .then((foundModified) => {
                    if (foundModified) {
                        onChanged();
                    }
                })
                .catch((error: unknown) => {
                    logger.debug(`Could not look for modified files: ${error instanceof Error ? error.message : String(error)}`);
                });
        }
    }

    private relativeToMount(filePath: string, fallbackProjectRoot: string, romfs: string): string | undefined {
        return relativeToMount(filePath, fallbackProjectRoot, romfs);
    }

    private async compare(
        cachePath: string,
        files: Array<{ path: string; rel: string }>,
        archives: Array<{ path: string; rel: string }>,
    ): Promise<boolean> {
        const stamps = new Map<string, string>();
        for (const entry of [...files, ...archives]) {
            const stamp = stampOf(entry.path);
            if (stamp) {
                stamps.set(entry.path, stamp);
            }
        }

        const result = await runBridgeJsonAsync<ModifiedFilesResult>(
            this.deps.getHost(),
            ['modified-files'],
            JSON.stringify({ db: cachePath, files, archives }),
            this.deps.getEnv(),
        );
        let foundModified = false;
        const modifiedFiles = new Set(result.files ?? []);
        for (const file of files) {
            const stamp = stamps.get(file.path);
            if (stamp) {
                const modified = modifiedFiles.has(file.path);
                this.files.set(cacheKey(file.path), { stamp, modified });
                foundModified ||= modified;
            }
        }
        for (const archive of archives) {
            const stamp = stamps.get(archive.path);
            const modified = result.archives?.[archive.path];
            if (stamp && modified) {
                this.archives.set(cacheKey(archive.path), {
                    stamp,
                    modified: new Set(modified.map((inner) => inner.toLowerCase())),
                });
                foundModified ||= modified.length > 0;
            }
        }
        return foundModified;
    }
}
