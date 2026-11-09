import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { getDiskArchivePath, getLocatorInsideDiskArchive } from './archives';
import { BarsAudioResult, BarsEntry, runBridgeReadAsync, runBridgeReadBarsAudioManyAsync } from './bridge';
import type { BatchHost } from './barsBatchReplace';
import { logger } from './logger';

/** A file name for an entry, which is a game asset name but may still hold something a file system refuses. */
function fileNameFor(entryName: string): string {
    return `${entryName.replace(/[<>:"/\\|?*\u0000-\u001f]/g, '_')}.wav`;
}

/**
 * Writes every playable track of a BARS to a folder as WAV files named after their entries, so the folder can go back
 * into "Replace tracks by name from folder" after editing them.
 */
export async function exportBarsTracks(host: BatchHost, barsUri: vscode.Uri): Promise<void> {
    const diskArchive = getDiskArchivePath(barsUri.fsPath);
    const filePath = getLocatorInsideDiskArchive(barsUri.fsPath, diskArchive);
    const barsName = path.basename(barsUri.fsPath) || 'audio.bars';

    const listed = await runBridgeReadAsync(host.hostExe, ['list-bars', diskArchive, filePath], host.env);
    const entries: BarsEntry[] = (listed as { entries?: BarsEntry[] }).entries ?? [];
    const playable = entries.flatMap((entry, index) => (entry.has_prefetch || entry.has_romfs_bwav ? [index] : []));
    if (playable.length === 0) {
        void vscode.window.showWarningMessage(`${barsName} has no tracks with audio to export.`);
        return;
    }

    const picked = await vscode.window.showOpenDialog({
        canSelectFiles: false,
        canSelectFolders: true,
        canSelectMany: false,
        openLabel: 'Export Here',
        title: `Export ${playable.length} tracks of ${barsName} to a folder`,
    });
    const folder = picked?.[0]?.fsPath;
    if (!folder) {
        return;
    }

    // One name per track; two entries can't share a file, so a clash gets its index.
    const targets = new Map<number, string>();
    const taken = new Set<string>();
    for (const index of playable) {
        let name = fileNameFor(entries[index]!.name);
        if (taken.has(name.toLowerCase())) {
            name = fileNameFor(`${entries[index]!.name}_${index}`);
        }
        taken.add(name.toLowerCase());
        targets.set(index, path.join(folder, name));
    }

    const existing = [...targets.values()].filter((target) => fs.existsSync(target));
    if (existing.length > 0) {
        const choice = await vscode.window.showWarningMessage(
            `${existing.length} file(s) in that folder would be overwritten, such as ${path.basename(existing[0]!)}.`,
            { modal: true },
            'Overwrite',
        );
        if (choice !== 'Overwrite') {
            return;
        }
    }

    let written = 0;
    let prefetchOnly = 0;
    const failed: string[] = [];
    await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: `Exporting ${playable.length} tracks of ${barsName}…` },
        async () => {
            // '' keeps the host from caching: these are temp WAVs, copied out below and removed.
            const results: BarsAudioResult[] = await runBridgeReadBarsAudioManyAsync(
                host.hostExe,
                diskArchive,
                filePath,
                '',
                playable.map((index) => ({ index, usePrefetch: false })),
                host.env,
            );
            for (const result of results) {
                const target = result.index === undefined ? undefined : targets.get(result.index);
                try {
                    if (!target || !result.wavPath) {
                        failed.push(`${entries[result.index ?? -1]?.name ?? `#${result.index}`}: ${result.error ?? 'no audio returned'}`);
                        continue;
                    }
                    await fs.promises.copyFile(result.wavPath, target);
                    written++;
                    if (result.isPrefetch) {
                        prefetchOnly++;
                    }
                } catch (e) {
                    failed.push(`${path.basename(target ?? '?')}: ${e instanceof Error ? e.message : String(e)}`);
                } finally {
                    if (result.wavPath) {
                        fs.promises.unlink(result.wavPath).catch(() => { /* best effort */ });
                    }
                }
            }
        },
    );

    const notes = [`Exported ${written} of ${playable.length} tracks to ${folder}.`];
    if (prefetchOnly > 0) {
        notes.push(`${prefetchOnly} only have a short prefetch clip in the BARS (the full sound wasn't found), so that is what was exported.`);
    }
    if (failed.length > 0) {
        for (const line of failed) {
            logger.warn(`BARS export: ${line}`);
        }
        const choice = await vscode.window.showWarningMessage(`${notes.join(' ')} ${failed.length} failed.`, 'Show Log');
        if (choice === 'Show Log') {
            logger.show();
        }
    } else {
        void vscode.window.showInformationMessage(notes.join(' '));
    }
}
