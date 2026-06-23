import * as fs from 'fs';
import * as vscode from 'vscode';
import { runBridgeJsonAsync } from '../bridge';
import {
    getDiskArchivePath,
    getLocatorInsideDiskArchive,
    isPathInsideArchive,
} from '../archives';

export interface RawFileIoContext {
    bridgePath: string;
    getPython: () => string;
    getBridgeEnv: () => NodeJS.ProcessEnv;
}

function requirePython(getPython: () => string): string {
    const python = getPython();
    if (!python) {
        throw new Error(
            'Python environment is not ready. Run "TKVSC: Set Up Python Environment" first.',
        );
    }
    return python;
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

    if (uri.scheme === 'file' && !isPathInsideArchive(fsPath)) {
        return await fs.promises.readFile(fsPath);
    }

    const diskArchive = getDiskArchivePath(fsPath);
    const locator = getLocatorInsideDiskArchive(fsPath, diskArchive);

    if (!locator || diskArchive === fsPath) {
        return await fs.promises.readFile(fsPath);
    }

    const python = requirePython(ctx.getPython);
    const result = await runBridgeJsonAsync<{ path: string }>(
        python,
        ctx.bridgePath,
        ['export-temp', diskArchive, locator],
        undefined,
        ctx.getBridgeEnv(),
    );

    const raw = await fs.promises.readFile(result.path);
    try {
        await fs.promises.unlink(result.path);
    } catch {
        // Best-effort temp cleanup.
    }
    return raw;
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

    const python = requirePython(ctx.getPython);
    const encoded = Buffer.from(data).toString('base64');
    await runBridgeJsonAsync<{ success: boolean }>(
        python,
        ctx.bridgePath,
        ['write-raw', diskArchive, locator],
        encoded,
        ctx.getBridgeEnv(),
    );
}
