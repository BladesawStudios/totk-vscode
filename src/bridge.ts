import { execFile, execFileSync } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import { logger } from './logger';

const MAX_BUFFER = 1024 * 1024 * 500;

/**
 * Every bridge command runs in the C# host (`tkvsc-host`). The first argument of the `runBridge*` functions is its path.
 */
let nativeHost: { executable: string; extensionRoot: string } | undefined;

export function configureNativeHost(executable: string | undefined, extensionRoot: string): void {
    nativeHost = executable ? { executable, extensionRoot } : undefined;
}

/** The C# host, if one was found for this platform. */
export function getNativeHostExecutable(): string | undefined {
    return nativeHost?.executable;
}

interface Launch {
    file: string;
    env: NodeJS.ProcessEnv;
}

function launchFor(executable: string, env?: NodeJS.ProcessEnv): Launch {
    const merged = env ? { ...process.env, ...env } : { ...process.env };
    return {
        file: executable,
        env: nativeHost ? { ...merged, TKVSC_EXTENSION_ROOT: nativeHost.extensionRoot } : merged,
    };
}

export function runBridge(
    hostExe: string,
    args: string[],
    stdin?: string,
    env?: NodeJS.ProcessEnv,
): string {
    const startTime = Date.now();
    logger.debug(`bridge: Running sync command [${args[0]}] with args: ${args.slice(1).join(' ')}`);
    try {
        const launch = launchFor(hostExe, env);
        const result = execFileSync(launch.file, args, {
            encoding: 'utf-8',
            maxBuffer: MAX_BUFFER,
            input: stdin,
            env: launch.env,
            cwd: nativeHost?.extensionRoot,
        });
        const elapsed = Date.now() - startTime;
        logger.debug(`bridge: Sync command [${args[0]}] finished successfully in ${elapsed}ms (output size: ${result.length} chars)`);
        return result;
    } catch (error) {
        const elapsed = Date.now() - startTime;
        logger.error(`bridge: Sync command [${args[0]}] failed after ${elapsed}ms. Error:`, error as Error);
        throw error;
    }
}

export function runBridgeAsync(
    hostExe: string,
    args: string[],
    stdin?: string,
    env?: NodeJS.ProcessEnv,
): Promise<string> {
    return runBridgeOnce(hostExe, args, stdin, env);
}

function runBridgeOnce(
    hostExe: string,
    args: string[],
    stdin?: string,
    env?: NodeJS.ProcessEnv,
): Promise<string> {
    const startTime = Date.now();
    logger.debug(`bridge: Running async command [${args[0]}] with args: ${args.slice(1).join(' ')}`);
    return new Promise((resolve, reject) => {
        const launch = launchFor(hostExe, env);
        const child = execFile(
            launch.file,
            args,
            {
                encoding: 'utf-8',
                maxBuffer: MAX_BUFFER,
                env: launch.env,
                cwd: nativeHost?.extensionRoot,
            },
            (error, stdout) => {
                const elapsed = Date.now() - startTime;
                if (error) {
                    logger.error(`bridge: Async command [${args[0]}] failed after ${elapsed}ms. Error:`, error);
                    reject(error);
                    return;
                }
                logger.debug(`bridge: Async command [${args[0]}] finished successfully in ${elapsed}ms (output size: ${stdout.length} chars)`);
                resolve(stdout);
            },
        );

        if (stdin !== undefined) {
            child.stdin?.write(stdin);
            child.stdin?.end();
        }
    });
}

export function runBridgeJson<T>(
    hostExe: string,
    args: string[],
    stdin?: string,
    env?: NodeJS.ProcessEnv,
): T {
    const output = runBridge(hostExe, args, stdin, env);
    const result = JSON.parse(output) as T & { error?: string };
    if (result && typeof result === 'object' && 'error' in result && result.error) {
        throw new Error(result.error);
    }
    return result;
}

export async function runBridgeJsonAsync<T>(
    hostExe: string,
    args: string[],
    stdin?: string,
    env?: NodeJS.ProcessEnv,
): Promise<T> {
    const output = await runBridgeAsync(hostExe, args, stdin, env);
    const result = JSON.parse(output) as T & { error?: string };
    if (result && typeof result === 'object' && 'error' in result && result.error) {
        throw new Error(result.error);
    }
    return result;
}

type BridgeReadPayload = { content?: string; contentPath?: string; error?: string };

export interface BntxChannelInfo {
    red: string;
    green: string;
    blue: string;
    alpha: string;
}

export interface BntxImageInfo {
    width: number;
    height: number;
    mipCount: number;
    format: string;
    formatId: string;
    useSRGB: string;
    name: string;
    path: string;
    accessFlags: string;
}

export interface BntxMiscInfo {
    depth: number;
    tileMode: string;
    swizzle: number;
    alignment: number;
    pitch: number;
    dims: string;
    surfaceShape: string;
    flags: number;
    imageSize: number;
    sampleCount: number;
}

export interface BridgeResult {
    success?: boolean;
    error?: string;
    [key: string]: any;
}

export interface BwavAudioResult extends BridgeResult {
    wavPath?: string;
}

export interface BarsEntry {
    name: string;
    name_hash: number;
    amta_offset: number;
    bwav_offset: number;
    has_prefetch: boolean;
    has_romfs_bwav: boolean;
    metadata?: any;
}

export interface BarsListResult extends BridgeResult {
    entries?: BarsEntry[];
}

export interface BarsAudioResult extends BridgeResult {
    wavPath?: string;
    name?: string;
    isPrefetch?: boolean;
    loopStart?: number;
    loopEnd?: number;
}

export interface BarsReplaceResult extends BridgeResult {
    success?: boolean;
    name?: string;
    /** What was embedded into the BARS: 'prefetch', 'full', or null (stream-only entry). */
    embedded?: 'prefetch' | 'full' | null;
    /** True when the entry streams from romfs and the full BWAV must also be placed there. */
    needsStreamFile?: boolean;
    /** Romfs-relative path of the entry's existing full BWAV (mod or game), when it has one. */
    streamRelativePath?: string | null;
    fullBwavTempPath?: string;
    numSamples?: number;
    channels?: number;
    loopStart?: number | null;
    loopEnd?: number | null;
    /** Source channel count when the audio was remixed to match the entry's AMTA, else null. */
    channelsConvertedFrom?: number | null;
    /** True when the entry's AMTA loudness stats (peak / R128 loudness) were rewritten. */
    loudnessUpdated?: boolean;
    /** Integrated loudness of the new audio in LUFS, when measured. */
    integratedLoudness?: number | null;
    /** One result per entry, for an edit that replaced a group of them. */
    sections?: BarsReplaceResult[];
    /** Where the music table was saved, when the edit changed it. */
    tableUpdatedAt?: string | null;
    tableNodesChanged?: number;
}

/** What the section editor knows about one entry of a group. */
export interface BarsSectionInfo {
    index: number;
    name: string;
    /** The length of its current audio file. */
    seconds: number;
    looped: boolean;
    /** Whether the music table plays this entry. */
    inTable: boolean;
    /** The play length the table sets, for an entry that doesn't follow its file's length. */
    fixedSeconds?: number | null;
    tailSeconds?: number | null;
    /** The entry the table normally plays after this one. */
    next?: string | null;
}

export interface BarsSectionInfoResult extends BridgeResult {
    sections?: BarsSectionInfo[];
    hasTable?: boolean;
}

export interface BntxTextureResult {
    bntxTexture: true;
    error?: string;
    /** The layer of an array texture that the picture shows (0 for a single texture). */
    layer?: number;
    metadata?: {
        name: string;
        channels: BntxChannelInfo;
        imageInfo: BntxImageInfo;
        misc: BntxMiscInfo;
        width: number;
        height: number;
        format: string;
        formatId: string;
        mipCount: number;
        dataSize: number;
        tileMode: string;
        blockH: number;
        blockHLog2: number;
        /** How many layers the texture has; more than one makes it an array. */
        arrayCount?: number;
    };
    pngBase64?: string;
    pngPath?: string;
}

type BridgeReadResult = BridgeReadPayload | BntxTextureResult;

export function isBntxTextureResult(result: BridgeReadResult): result is BntxTextureResult {
    return 'bntxTexture' in result && result.bntxTexture === true;
}

/** Read file from bridge. Returns either text content or a BNTX texture result. */
export function runBridgeRead(
    hostExe: string,
    args: string[],
    env?: NodeJS.ProcessEnv,
): BridgeReadResult {
    return runBridgeJson<BridgeReadResult>(hostExe, args, undefined, env);
}

export async function runBridgeReadAsync(
    hostExe: string,
    args: string[],
    env?: NodeJS.ProcessEnv,
): Promise<BridgeReadResult> {
    return runBridgeJsonAsync<BridgeReadResult>(hostExe, args, undefined, env);
}

/** Read editable file text from the bridge (supports spill files for large XLNK text). */
export function runBridgeReadContent(
    hostExe: string,
    args: string[],
    env?: NodeJS.ProcessEnv,
): string {
    const result = runBridgeJson<BridgeReadPayload>(hostExe, args, undefined, env);
    if (result.contentPath) {
        try {
            return fs.readFileSync(result.contentPath, 'utf-8');
        } finally {
            try {
                fs.unlinkSync(result.contentPath);
            } catch {
                /* best-effort cleanup */
            }
        }
    }
    return result.content ?? '';
}

/** Async version of runBridgeReadContent. */
export async function runBridgeReadContentAsync(
    hostExe: string,
    args: string[],
    env?: NodeJS.ProcessEnv,
): Promise<string> {
    const result = await runBridgeJsonAsync<BridgeReadPayload>(
        hostExe,
        args,
        undefined,
        env,
    );
    if (result.contentPath) {
        try {
            return await fs.promises.readFile(result.contentPath, 'utf-8');
        } finally {
            try {
                await fs.promises.unlink(result.contentPath);
            } catch {
                /* best-effort cleanup */
            }
        }
    }
    return result.content ?? '';
}

export async function runBridgeUpdateBntxMetadataAsync(
    hostExe: string,
    archivePath: string,
    internalPath: string,
    metadata: Record<string, any>,
    env?: NodeJS.ProcessEnv,
): Promise<void> {
    await runBridgeJsonAsync(
        hostExe,
        ['update-bntx-metadata', archivePath, internalPath],
        JSON.stringify(metadata),
        env,
    );
}

export async function runBridgeUpdateTxtgMetadataAsync(
    hostExe: string,
    archivePath: string,
    internalPath: string,
    metadata: Record<string, any>,
    env?: NodeJS.ProcessEnv,
): Promise<void> {
    await runBridgeJsonAsync(
        hostExe,
        ['update-txtg-metadata', archivePath, internalPath],
        JSON.stringify(metadata),
        env,
    );
}

export async function runBridgeRenameBntxTextureAsync(
    hostExe: string,
    archivePath: string,
    internalPath: string,
    newName: string,
    env?: NodeJS.ProcessEnv,
): Promise<void> {
    await runBridgeJsonAsync(
        hostExe,
        ['rename-bntx-texture', archivePath, internalPath, newName],
        undefined,
        env,
    );
}

export async function runBridgeDeleteBntxTextureAsync(
    hostExe: string,
    archivePath: string,
    internalPath: string,
    env?: NodeJS.ProcessEnv,
): Promise<void> {
    await runBridgeJsonAsync(
        hostExe,
        ['delete-bntx-texture', archivePath, internalPath],
        undefined,
        env,
    );
}

/** What a DDS import reports back. `note` is set when the DDS had to be converted to fit an array layer. */
export interface TexturePayloadReplaceResult {
    note?: string;
    converted?: string[];
}

export async function runBridgeReplaceBntxPayloadAsync(
    hostExe: string,
    archivePath: string,
    internalPath: string,
    rawPayload: Buffer,
    env?: NodeJS.ProcessEnv,
    layer?: number,
): Promise<TexturePayloadReplaceResult> {
    return await runBridgeJsonAsync<TexturePayloadReplaceResult>(
        hostExe,
        ['replace-bntx-payload', archivePath, internalPath, ...(layer ? [String(layer)] : [])],
        rawPayload.toString('base64'),
        env,
    );
}

export async function runBridgeReplaceTxtgPayloadAsync(
    hostExe: string,
    archivePath: string,
    internalPath: string,
    rawPayload: Buffer,
    env?: NodeJS.ProcessEnv,
    layer?: number,
): Promise<TexturePayloadReplaceResult> {
    return await runBridgeJsonAsync<TexturePayloadReplaceResult>(
        hostExe,
        ['replace-txtg-payload', archivePath, internalPath, ...(layer ? [String(layer)] : [])],
        rawPayload.toString('base64'),
        env,
    );
}

/** Loop specification for a BARS audio replacement:
 *  'auto' honors the source file's own loop metadata, 'none' strips loops,
 *  a number is an explicit sample position. */
export type BarsLoopSpec = 'auto' | 'none' | number;

export async function runBridgeReplaceBarsAudioAsync(
    hostExe: string,
    archivePath: string,
    internalPath: string,
    entryIndex: number,
    audioPayload: Buffer,
    loopStart: BarsLoopSpec = 'auto',
    loopEnd: BarsLoopSpec = 'auto',
    sourceName = 'audio.bin',
    env?: NodeJS.ProcessEnv,
    trimAfterLoop = false,
): Promise<BarsReplaceResult> {
    return runBridgeJsonAsync<BarsReplaceResult>(
        hostExe,
        [
            'replace-bars-audio',
            archivePath,
            internalPath,
            entryIndex.toString(),
            loopStart.toString(),
            loopEnd.toString(),
            sourceName,
            trimAfterLoop ? 'true' : 'false',
        ],
        audioPayload.toString('base64'),
        env,
    );
}

/** A source audio file decoded to a temporary 16-bit WAV for previewing. The caller deletes `wavPath`. */
export interface DecodedAudioFile extends BridgeResult {
    wavPath?: string;
    sampleRate?: number;
    channels?: number;
    samples?: number;
    loopStart?: number | null;
    loopEnd?: number | null;
}

export async function runBridgeDecodeAudioFileAsync(
    hostExe: string,
    filePath: string,
    env?: NodeJS.ProcessEnv,
): Promise<DecodedAudioFile> {
    return runBridgeJsonAsync<DecodedAudioFile>(hostExe, ['decode-audio-file', filePath], undefined, env);
}

/** A BARS entry's whole sound, decoded for the loop editor. The caller deletes `wavPath`. */
export async function runBridgeDecodeBarsEntryAsync(
    hostExe: string,
    archivePath: string,
    internalPath: string,
    entryIndex: number,
    env?: NodeJS.ProcessEnv,
): Promise<DecodedAudioFile> {
    return runBridgeJsonAsync<DecodedAudioFile>(
        hostExe,
        ['decode-bars-entry', archivePath, internalPath, entryIndex.toString()],
        undefined,
        env,
    );
}

/** A group of BARS entries in playing order, with what the music table says about each. */
export async function runBridgeBarsSectionInfoAsync(
    hostExe: string,
    archivePath: string,
    internalPath: string,
    entryIndices: number[],
    env?: NodeJS.ProcessEnv,
): Promise<BarsSectionInfoResult> {
    return runBridgeJsonAsync<BarsSectionInfoResult>(
        hostExe,
        ['bars-section-info', archivePath, internalPath, entryIndices.join(',')],
        undefined,
        env,
    );
}

/** Cuts one source file across a group of BARS entries, all or nothing. */
export async function runBridgeReplaceBarsSectionsAsync(
    hostExe: string,
    archivePath: string,
    internalPath: string,
    spec: {
        source: string;
        updateTable: boolean;
        sections: { index: number; start: number; end: number; loop: boolean }[];
    },
    env?: NodeJS.ProcessEnv,
): Promise<BarsReplaceResult> {
    return runBridgeJsonAsync<BarsReplaceResult>(
        hostExe,
        ['replace-bars-sections', archivePath, internalPath],
        JSON.stringify(spec),
        env,
    );
}

/** Sets a BARS entry's loop without re-encoding, unless `trimAfterLoop` has to cut audio. */
export async function runBridgeSetBarsLoopAsync(
    hostExe: string,
    archivePath: string,
    internalPath: string,
    entryIndex: number,
    loopStart: number | 'none',
    loopEnd: number | 'none',
    trimAfterLoop = false,
    env?: NodeJS.ProcessEnv,
): Promise<BarsReplaceResult> {
    return runBridgeJsonAsync<BarsReplaceResult>(
        hostExe,
        [
            'set-bars-loop',
            archivePath,
            internalPath,
            entryIndex.toString(),
            loopStart.toString(),
            loopEnd.toString(),
            trimAfterLoop ? 'true' : 'false',
        ],
        undefined,
        env,
    );
}

export async function runBridgePrepareFontReplacementAsync(
    hostExe: string,
    importPath: string,
    targetPath: string,
    env?: NodeJS.ProcessEnv,
): Promise<Buffer> {
    const result = await runBridgeJsonAsync<{ path: string }>(
        hostExe,
        ['prepare-font-replacement', importPath, targetPath],
        undefined,
        env,
    );
    const raw = await fs.promises.readFile(result.path);
    try {
        await fs.promises.unlink(result.path);
    } catch {
        // Best-effort temp cleanup.
    }
    return raw;
}
