import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { getDiskArchivePath, isArchiveFile, isPathInsideArchive } from './archives';
import { runBridgeJsonAsync } from './bridge';
import { logger } from './logger';
import { resolveProjectRomfsMount } from './projectPaths';

export const MODIFIED_MARKER = '- modified';

/** What the marker needs of a tree item. */
export interface MarkableItem {
    resourceUri: vscode.Uri;
    description?: string | boolean;
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
 */
export class ModifiedFileMarker {
    // Results are kept by file and checked against the file's modified time and size.
    private readonly files = new Map<string, { stamp: string; modified: boolean }>();
    private readonly archives = new Map<string, { stamp: string; modified: Set<string> }>();

    constructor(private readonly deps: ModifiedFileMarkerDeps) {}

    /** Forgets every result, for when the game or its dump changes. */
    invalidate(): void {
        this.files.clear();
        this.archives.clear();
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

        const missingFiles: Array<{ path: string; rel: string }> = [];
        for (const [file, { item, rel }] of wantedFiles) {
            const cached = this.files.get(cacheKey(file));
            if (cached && cached.stamp === stampOf(file)) {
                if (cached.modified) {
                    this.flag(item);
                }
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
            if (cached && cached.stamp === stampOf(disk) && cached.modified.has(inner.toLowerCase())) {
                this.flag(item);
            }
        }

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

    private flag(item: MarkableItem): void {
        const existing = typeof item.description === 'string' ? item.description : '';
        if (!existing.includes(MODIFIED_MARKER)) {
            item.description = existing ? `${existing} ${MODIFIED_MARKER}` : MODIFIED_MARKER;
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
