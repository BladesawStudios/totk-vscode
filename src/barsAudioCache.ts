import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { logger } from './logger';

// Decoded BARS audio kept between sessions. The host names each WAV after a hash of the BWAV it came from
// (`read-bars-audio-many`), so an edited BARS or replaced stream never serves old audio; this side only
// decides where the folder is and keeps it under the size the user allows.

let cacheRoot: string | undefined;
let pruning: Promise<void> | undefined;

export function initBarsAudioCache(context: vscode.ExtensionContext): void {
    cacheRoot = path.join(context.globalStorageUri.fsPath, 'bars-audio-cache');
    context.subscriptions.push(
        vscode.commands.registerCommand('totk-editor.clearBarsAudioCache', async () => {
            await clearBarsAudioCache();
            void vscode.window.showInformationMessage('Cleared the BARS audio cache.');
        }),
    );
    // Trims what earlier sessions left, off the activation path.
    setTimeout(() => void pruneBarsAudioCache(), 30_000);
}

function maxBytes(): number {
    const mb = vscode.workspace.getConfiguration('TKVSC').get<number>('barsAudioCacheSizeMB', 2048);
    return Math.max(0, mb) * 1024 * 1024;
}

/** The folder the host keeps decoded audio in, or '' when the cache is off (the host then writes temp files). */
export function barsAudioCacheDir(): string {
    return cacheRoot && maxBytes() > 0 ? cacheRoot : '';
}

/** The folder to let a webview read from, whether or not the cache is on right now. */
export function barsAudioCacheRoot(): string | undefined {
    return cacheRoot;
}

/** Drops the least recently played sounds until the cache fits its limit. */
export function pruneBarsAudioCache(): Promise<void> {
    pruning ??= prune().finally(() => { pruning = undefined; });
    return pruning;
}

async function prune(): Promise<void> {
    if (!cacheRoot) {
        return;
    }
    let names: string[];
    try {
        names = await fs.promises.readdir(cacheRoot);
    } catch {
        return;
    }
    const limit = maxBytes();
    type Item = { key: string; bytes: number; used: number };
    const items = new Map<string, Item>();
    for (const name of names) {
        const file = path.join(cacheRoot, name);
        const stat = await fs.promises.stat(file).catch(() => undefined);
        if (!stat?.isFile()) {
            continue;
        }
        // A temp file a host left behind when it was killed mid-write.
        if (name.endsWith('.tmp')) {
            if (Date.now() - stat.mtimeMs > 60 * 60 * 1000) {
                await fs.promises.unlink(file).catch(() => { /* in use */ });
            }
            continue;
        }
        const key = name.replace(/\.(wav|json)$/, '');
        const item = items.get(key) ?? { key, bytes: 0, used: 0 };
        item.bytes += stat.size;
        item.used = Math.max(item.used, stat.mtimeMs);
        items.set(key, item);
    }

    let total = [...items.values()].reduce((sum, item) => sum + item.bytes, 0);
    if (total <= limit) {
        return;
    }
    const oldestFirst = [...items.values()].sort((a, b) => a.used - b.used);
    let removed = 0;
    for (const item of oldestFirst) {
        if (total <= limit) {
            break;
        }
        // The info file goes first: without it the host decodes the sound again rather than trusting the WAV.
        await fs.promises.unlink(path.join(cacheRoot, item.key + '.json')).catch(() => { /* gone or in use */ });
        await fs.promises.unlink(path.join(cacheRoot, item.key + '.wav')).catch(() => { /* gone or in use */ });
        total -= item.bytes;
        removed++;
    }
    logger.debug(`barsAudioCache: dropped ${removed} sounds, ${Math.round(total / 1048576)} MB left`);
}

export async function clearBarsAudioCache(): Promise<void> {
    if (cacheRoot) {
        await fs.promises.rm(cacheRoot, { recursive: true, force: true });
    }
}
