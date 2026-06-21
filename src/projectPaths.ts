import * as fs from 'fs';
import * as path from 'path';
import { getDiskArchivePath, isArchiveFile, isPathInsideArchive } from './archives';

export function normalizePath(filePath: string): string {
    return path.normalize(filePath);
}

export function pathsEqual(a: string, b: string): boolean {
    const left = normalizePath(a);
    const right = normalizePath(b);
    if (process.platform === 'win32') {
        return left.toLowerCase() === right.toLowerCase();
    }
    return left === right;
}

export function isWithinRoot(root: string, target: string): boolean {
    const rootNorm = normalizePath(root);
    const targetNorm = normalizePath(target);
    if (pathsEqual(rootNorm, targetNorm)) {
        return true;
    }
    const rel = path.relative(rootNorm, targetNorm);
    return rel !== '' && !rel.startsWith('..') && !path.isAbsolute(rel);
}

const ZSDIC = path.join('Pack', 'ZsDic.pack.zs');

const ROMFS_DIR_NAMES = ['RomFS', 'romfs', 'Romfs', 'ROMFS'];

function isDirectory(dirPath: string): boolean {
    try {
        return fs.statSync(dirPath).isDirectory();
    } catch {
        return false;
    }
}

function listSubdirectories(dirPath: string): string[] {
    try {
        return fs
            .readdirSync(dirPath, { withFileTypes: true })
            .filter((entry) => entry.isDirectory())
            .map((entry) => path.join(dirPath, entry.name));
    } catch {
        return [];
    }
}

function hasArchiveFiles(dirPath: string): boolean {
    try {
        return fs
            .readdirSync(dirPath, { withFileTypes: true })
            .some((entry) => entry.isFile() && isArchiveFile(entry.name));
    } catch {
        return false;
    }
}

function findNamedRomfsFolder(base: string): string | undefined {
    for (const name of ROMFS_DIR_NAMES) {
        const candidate = path.join(base, name);
        if (isDirectory(candidate)) {
            return candidate;
        }
    }
    return undefined;
}

/** Locate the RomFS folder under a project (ZsDic dump, RomFS/romfs folder, or nested). */
export function findRomfsFolderUnder(projectRoot: string): string | undefined {
    const project = normalizePath(projectRoot);
    if (fs.existsSync(path.join(project, ZSDIC))) {
        return project;
    }

    if (hasArchiveFiles(project)) {
        return project;
    }

    const direct = findNamedRomfsFolder(project);
    if (direct) {
        return direct;
    }

    for (const child of listSubdirectories(project)) {
        if (fs.existsSync(path.join(child, ZSDIC))) {
            return child;
        }
        const nested = findNamedRomfsFolder(child);
        if (nested) {
            return nested;
        }
    }

    return undefined;
}

/** Where dump-relative paths should land inside a mod project. */
export function resolveProjectRomfsMount(projectRoot: string, gameRomfsRoot: string): string {
    const project = normalizePath(projectRoot);
    const gameRomfs = normalizePath(gameRomfsRoot);

    if (pathsEqual(project, gameRomfs)) {
        return project;
    }

    if (isWithinRoot(project, gameRomfs)) {
        return gameRomfs;
    }

    const found = findRomfsFolderUnder(project);
    if (found) {
        return found;
    }

    return path.join(project, 'RomFS');
}

/** Map a dump file path to the destination path inside a project root. */
export function resolveProjectDestination(
    copySource: string,
    projectRoot: string,
    romfsRoot: string,
    tkmmOption?: { group: string; option: string },
): string {
    const project = normalizePath(projectRoot);
    const source = normalizePath(copySource);
    const gameRomfs = normalizePath(romfsRoot);

    if (!isWithinRoot(gameRomfs, source) && !isWithinRoot(project, source)) {
        throw new Error('Selected file is not inside the configured game dump or project.');
    }

    if (!tkmmOption && isWithinRoot(project, source)) {
        return path.join(project, path.relative(project, source));
    }

    let relPath = '';
    let isExefs = false;
    let foundFolder = false;

    if (isWithinRoot(gameRomfs, source)) {
        relPath = path.relative(gameRomfs, source);
        if (path.basename(gameRomfs).toLowerCase() === 'exefs') {
            isExefs = true;
        }
        foundFolder = true;
    } else {
        // Source is in the project (or a different dump structure). Find its relative path.
        const projectRel = path.relative(project, source);
        const parts = projectRel.split(path.sep);
        if (parts.length >= 4 && parts[0]?.toLowerCase() === 'options') {
            const folder = parts[3]?.toLowerCase();
            if (folder === 'romfs' || folder === 'exefs') {
                isExefs = folder === 'exefs';
                parts.splice(0, 4); // Strip options/Group/Option/romfs
                relPath = parts.join(path.sep);
                foundFolder = true;
            }
        }
        
        if (!foundFolder && parts.length > 0) {
            const folder = parts[0]?.toLowerCase();
            if (folder === 'romfs' || folder === 'exefs') {
                isExefs = folder === 'exefs';
                parts.splice(0, 1);
                relPath = parts.join(path.sep);
                foundFolder = true;
            }
        }
        
        if (!foundFolder) {
            // It's not in a standard romfs/exefs folder, just use its project-relative path
            relPath = projectRel;
        }
    }

    if (tkmmOption) {
        const targetSubdir = isExefs ? 'exefs' : 'romfs';
        if (!relPath.startsWith('romfs') && !relPath.startsWith('exefs') && !foundFolder) {
            // It's a root-level file like info.json, place it at the root of the option
            return path.join(project, 'options', tkmmOption.group, tkmmOption.option, relPath);
        }
        return path.join(project, 'options', tkmmOption.group, tkmmOption.option, targetSubdir, relPath);
    }

    const projectRomfs = resolveProjectRomfsMount(project, gameRomfs);
    const baseDir = isExefs ? path.join(path.dirname(projectRomfs), 'exefs') : projectRomfs;
    return path.join(baseDir, relPath);
}

export function resolveAddToCopyPaths(
    sourceFsPath: string,
    projectRoot: string,
    romfsRoot: string,
    tkmmOption?: { group: string; option: string },
): { source: string; destination: string } {
    const source = normalizePath(sourceFsPath);
    const copySource = isPathInsideArchive(source)
        ? normalizePath(getDiskArchivePath(source))
        : source;

    return {
        source: copySource,
        destination: resolveProjectDestination(copySource, projectRoot, romfsRoot, tkmmOption),
    };
}

/** 
 * Returns all isolated mod roots within a given workspace folder.
 * This includes the workspace root itself and all TKMM options (options/<Group>/<Option>).
 */
export function getProjectModRoots(projectRoot: string): string[] {
    const project = normalizePath(projectRoot);
    const roots: string[] = [project];
    const optionsDir = path.join(project, 'options');
    
    if (fs.existsSync(optionsDir) && isDirectory(optionsDir)) {
        const groups = listSubdirectories(optionsDir);
        for (const group of groups) {
            const options = listSubdirectories(group);
            for (const option of options) {
                roots.push(normalizePath(option));
            }
        }
    }
    
    return roots;
}
