import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { getDiskArchivePath, getLocatorInsideDiskArchive } from './archives';
import {
    BarsBatchItemResult,
    BarsEntry,
    BarsLoopSpec,
    runBridgeReadAsync,
    runBridgeReplaceBarsBatchAsync,
} from './bridge';
import { reloadBarsViewer } from './barsViewer';
import { logger } from './logger';

/** Sources a BARS entry can be replaced from; the same list the single-entry picker offers. */
const AUDIO_EXTENSIONS = new Set(['.wav', '.bwav', '.mp3', '.ogg', '.oga', '.flac', '.m4a', '.aac', '.opus', '.wma', '.aiff', '.aif']);

/** How many names to spell out in a message before saying "and N more". */
const NAMES_SHOWN = 8;

export interface BatchHost {
    hostExe: string;
    env: NodeJS.ProcessEnv;
}

export function isAudioFile(filePath: string): boolean {
    return AUDIO_EXTENSIONS.has(path.extname(filePath).toLowerCase());
}

/** The audio files among the dropped paths, looking inside any folders that were dropped. */
export async function collectAudioFiles(dropped: vscode.Uri[]): Promise<string[]> {
    const found: string[] = [];
    const visit = async (fsPath: string): Promise<void> => {
        let stat: fs.Stats;
        try {
            stat = await fs.promises.stat(fsPath);
        } catch {
            return;
        }
        if (stat.isDirectory()) {
            const names = await fs.promises.readdir(fsPath);
            for (const name of names.sort()) {
                await visit(path.join(fsPath, name));
            }
        } else if (isAudioFile(fsPath)) {
            found.push(fsPath);
        }
    };
    for (const uri of dropped) {
        await visit(uri.fsPath);
    }
    return found;
}

export interface BatchMatch {
    index: number;
    entryName: string;
    source: string;
}

export interface BatchPlan {
    matches: BatchMatch[];
    /** Files whose name is no entry's name. */
    unmatched: string[];
    /** Files that name an entry another file already claimed. */
    duplicates: string[];
}

/** Pairs each file with the entry of the same name, ignoring case and the file's extension. */
export function planBatch(entries: Pick<BarsEntry, 'name'>[], files: string[]): BatchPlan {
    const byName = new Map<string, number>();
    entries.forEach((entry, index) => {
        const key = entry.name.toLowerCase();
        if (!byName.has(key)) {
            byName.set(key, index);
        }
    });

    const claimed = new Set<number>();
    const plan: BatchPlan = { matches: [], unmatched: [], duplicates: [] };
    for (const source of files) {
        const index = byName.get(path.parse(source).name.toLowerCase());
        if (index === undefined) {
            plan.unmatched.push(path.basename(source));
        } else if (claimed.has(index)) {
            plan.duplicates.push(path.basename(source));
        } else {
            claimed.add(index);
            plan.matches.push({ index, entryName: entries[index]!.name, source });
        }
    }
    return plan;
}

function nameList(names: string[]): string {
    const shown = names.slice(0, NAMES_SHOWN).join(', ');
    return names.length > NAMES_SHOWN ? `${shown} and ${names.length - NAMES_SHOWN} more` : shown;
}

async function pickLoop(): Promise<{ start: BarsLoopSpec; end: BarsLoopSpec } | undefined> {
    const choice = await vscode.window.showQuickPick(
        [
            { label: 'Use loop from each file', description: 'Honor loop metadata in the source (WAV smpl chunk / BWAV header), if any', value: 'auto' as const },
            { label: 'No loop', description: 'Play once, no looping', value: 'none' as const },
            { label: 'Loop entire clip', description: 'Loop each clip from its first to its last sample', value: 'whole' as const },
        ],
        { title: 'Loop points for all the replacement audio', placeHolder: 'How should these sounds loop? Fix single ones afterwards in the loop editor.' },
    );
    if (!choice) {
        return undefined;
    }
    return choice.value === 'whole' ? { start: 0, end: 2147483647 } : { start: choice.value, end: choice.value };
}

/** Where an entry's full BWAV goes in the mod: its game path under the mod's romfs, or Stream/ beside the BARS. */
function streamTarget(archiveDir: string, result: BarsBatchItemResult): string {
    const modRomfs = /^(.*)[\\/](?:sound|voice)[\\/]resource$/i.exec(archiveDir)?.[1];
    if (modRomfs !== undefined && result.streamRelativePath) {
        return path.join(modRomfs, result.streamRelativePath);
    }
    return path.join(archiveDir, 'Stream', `${result.name}.bwav`);
}

/**
 * Replaces the entries of a BARS from dropped audio files, matching each file to the entry of the same name.
 * Shows what matched before changing anything, asks once how the clips should loop, and saves the BARS once.
 */
export async function replaceBarsFromFiles(host: BatchHost, barsUri: vscode.Uri, dropped: vscode.Uri[]): Promise<void> {
    const diskArchive = getDiskArchivePath(barsUri.fsPath);
    const filePath = getLocatorInsideDiskArchive(barsUri.fsPath, diskArchive);
    const barsName = path.basename(barsUri.fsPath) || 'audio.bars';

    const files = await collectAudioFiles(dropped);
    if (files.length === 0) {
        void vscode.window.showWarningMessage('None of the dropped files are audio files a BARS entry can be replaced from.');
        return;
    }

    const listed = await runBridgeReadAsync(host.hostExe, ['list-bars', diskArchive, filePath], host.env);
    const entries: BarsEntry[] = (listed as { entries?: BarsEntry[] }).entries ?? [];
    const plan = planBatch(entries, files);
    if (plan.matches.length === 0) {
        void vscode.window.showWarningMessage(
            `None of the ${files.length} dropped file(s) match an entry in ${barsName}. A file must be named like its entry (Entry_Name.wav). ` +
            `Not matched: ${nameList(plan.unmatched)}.`,
        );
        return;
    }

    const lines = [`Replace ${plan.matches.length} of the ${entries.length} entries in ${barsName} from ${files.length} dropped file(s)?`];
    if (plan.unmatched.length > 0) {
        lines.push(`No entry is named like: ${nameList(plan.unmatched)}.`);
    }
    if (plan.duplicates.length > 0) {
        lines.push(`Skipped, because another file already replaces that entry: ${nameList(plan.duplicates)}.`);
    }
    const go = await vscode.window.showInformationMessage(lines.join('\n\n'), { modal: true }, 'Continue');
    if (go !== 'Continue') {
        return;
    }

    const loop = await pickLoop();
    if (!loop) {
        return;
    }

    const archiveDir = path.dirname(diskArchive);
    const failed: string[] = [];
    let replaced = 0;
    let streamsSaved = 0;
    await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: `Replacing ${plan.matches.length} entries in ${barsName}…` },
        async () => {
            const batch = await runBridgeReplaceBarsBatchAsync(
                host.hostExe,
                diskArchive,
                filePath,
                plan.matches.map((m) => ({ index: m.index, source: m.source, loopStart: loop.start, loopEnd: loop.end })),
                host.env,
            );
            replaced = batch.replaced;
            for (const result of batch.results) {
                try {
                    if (!result.success) {
                        const name = plan.matches.find((m) => m.index === result.index)?.entryName ?? `#${result.index}`;
                        failed.push(`${name} (${path.basename(result.source)}): ${result.error ?? 'unknown error'}`);
                        continue;
                    }
                    if (result.needsStreamFile && result.fullBwavTempPath) {
                        const target = streamTarget(archiveDir, result);
                        await fs.promises.mkdir(path.dirname(target), { recursive: true });
                        await fs.promises.copyFile(result.fullBwavTempPath, target);
                        streamsSaved++;
                    }
                } catch (e) {
                    failed.push(`${result.name ?? `#${result.index}`}: ${e instanceof Error ? e.message : String(e)}`);
                } finally {
                    if (result.fullBwavTempPath) {
                        fs.promises.unlink(result.fullBwavTempPath).catch(() => { /* best effort */ });
                    }
                }
            }
        },
    );

    if (replaced > 0) {
        try {
            const refreshed = await runBridgeReadAsync(host.hostExe, ['list-bars', diskArchive, filePath], host.env);
            const fresh = (refreshed as { entries?: BarsEntry[] }).entries;
            if (fresh) {
                reloadBarsViewer(barsUri.toString(), barsName, fresh);
            }
        } catch (e) {
            logger.warn(`BARS batch replace: could not refresh the viewer: ${e instanceof Error ? e.message : String(e)}`);
        }
    }

    const streamNote = streamsSaved > 0 ? ` ${streamsSaved} full BWAV${streamsSaved === 1 ? '' : 's'} saved to the mod's Stream folder.` : '';
    const summary = `Replaced ${replaced} of ${plan.matches.length} entries in ${barsName}.${streamNote}`;
    if (failed.length > 0) {
        for (const line of failed) {
            logger.warn(`BARS batch replace: ${line}`);
        }
        const choice = await vscode.window.showWarningMessage(`${summary} ${failed.length} failed: ${nameList(failed)}`, 'Show Log');
        if (choice === 'Show Log') {
            logger.show();
        }
    } else {
        void vscode.window.showInformationMessage(summary);
    }
}
