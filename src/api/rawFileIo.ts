import * as fs from 'fs';
import * as vscode from 'vscode';
import { isTotkFontPath, prepareFontBytes } from '../bfttfDecrypt';
import { runBridgeJsonAsync } from '../bridge';
import {
    getDiskArchivePath,
    getLocatorInsideDiskArchive,
    isPathInsideArchive,
} from '../archives';

export interface RawFileIoContext {
    getHost: () => string;
    getBridgeEnv: () => NodeJS.ProcessEnv;
}

function requireHost(getHost: () => string): string {
    const hostExe = getHost();
    if (!hostExe) {
        throw new Error(
            'The TKVSC host is not available for this platform. Reinstall the extension for your platform or build it from host/ (see host/README.md).',
        );
    }
    return hostExe;
}

async function readTempAndCleanup(tempPath: string): Promise<Uint8Array> {
    const raw = await fs.promises.readFile(tempPath);
    try {
        await fs.promises.unlink(tempPath);
    } catch {
        // Best-effort temp cleanup.
    }
    return raw;
}

async function readStandaloneDiskBytes(
    fsPath: string,
    ctx: RawFileIoContext,
): Promise<Uint8Array> {
    if (isTotkFontPath(fsPath)) {
        const hostExe = ctx.getHost();
        if (hostExe) {
            try {
                const result = await runBridgeJsonAsync<{ path: string }>(
                    hostExe,
                    ['read-font-disk', fsPath],
                    undefined,
                    ctx.getBridgeEnv(),
                );
                if (result.path) {
                    return await readTempAndCleanup(result.path);
                }
            } catch {
                // Fall back to in-process decrypt below.
            }
        }
    }

    const raw = await fs.promises.readFile(fsPath);
    return isTotkFontPath(fsPath) ? prepareFontBytes(raw, fsPath) : raw;
}

export async function readFontBytes(
    uri: vscode.Uri,
    ctx: RawFileIoContext,
): Promise<Uint8Array> {
    const fsPath = uri.fsPath;

    if (!isPathInsideArchive(fsPath)) {
        return readStandaloneDiskBytes(fsPath, ctx);
    }

    return readRawBytes(uri, ctx);
}

/**
 * Read decompressed binary bytes for a project/dump/archive URI.
 * Used by addon custom editors and core viewers.
 */
export async function readRawBytes(
    uri: vscode.Uri,
    ctx: RawFileIoContext,
): Promise<Uint8Array> {
    const fsPath = uri.fsPath;

    if (!isPathInsideArchive(fsPath)) {
        return readStandaloneDiskBytes(fsPath, ctx);
    }

    const diskArchive = getDiskArchivePath(fsPath);
    const locator = getLocatorInsideDiskArchive(fsPath, diskArchive);

    if (!locator || diskArchive === fsPath) {
        return readStandaloneDiskBytes(fsPath, ctx);
    }

    const hostExe = requireHost(ctx.getHost);
    const result = await runBridgeJsonAsync<{ path: string }>(
        hostExe,
        ['export-temp', diskArchive, locator],
        undefined,
        ctx.getBridgeEnv(),
    );

    return readTempAndCleanup(result.path);
}

/**
 * Write binary bytes back to disk or into a nested SARC entry.
 */
export async function writeRawBytes(
    uri: vscode.Uri,
    data: Uint8Array,
    ctx: RawFileIoContext,
): Promise<void> {
    const fsPath = uri.fsPath;

    if (!isPathInsideArchive(fsPath)) {
        await fs.promises.writeFile(fsPath, Buffer.from(data));
        return;
    }

    const diskArchive = getDiskArchivePath(fsPath);
    const locator = getLocatorInsideDiskArchive(fsPath, diskArchive);
    if (!locator) {
        throw new Error('Cannot write binary data to an archive root.');
    }

    const hostExe = requireHost(ctx.getHost);
    const encoded = Buffer.from(data).toString('base64');
    await runBridgeJsonAsync<{ success: boolean }>(
        hostExe,
        ['write-raw', diskArchive, locator],
        encoded,
        ctx.getBridgeEnv(),
    );
}
