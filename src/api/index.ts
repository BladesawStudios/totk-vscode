import { runBridgeJsonAsync } from '../bridge';
import {
    TKVSC_API_VERSION,
    TKVSC_ARCHIVE_CONTEXT,
    TKVSC_EXTENSION_ID,
    TKVSC_VIEWS,
} from './constants';
import { readFontBytes, readRawBytes, writeRawBytes } from './rawFileIo';
import { resolveProjectRoot } from './resolveProjectRoot';
import type { CreateTkvscApiOptions, TkvscApi } from './types';

/**
 * TKVSC addon extension API entry point.
 *
 * Addon extensions obtain this via:
 * `await vscode.extensions.getExtension(TKVSC_EXTENSION_ID)?.activate()`
 *
 * @see docs/addon-development.md
 * @see docs/api/v1.md
 */
export { TKVSC_API_VERSION, TKVSC_ARCHIVE_CONTEXT, TKVSC_EXTENSION_ID, TKVSC_VIEWS } from './constants';
export { TkvscReadyEmitter } from './readyEvent';
export { getBridgeEnv } from './bridgeEnv';
export { readFontBytes, readRawBytes, writeRawBytes } from './rawFileIo';
export { resolveProjectRoot } from './resolveProjectRoot';
export type { TkvscApi, TkvscBridgeAccess, TkvscTreeItemLike } from './types';
export type { FormatRegistration } from '../formatRegistry';
export type { GameProfile, GameProfileRegistration, GameIndexingConfig } from '../gameProfile';
export type { ProjectAdapter, ProjectOptionRef, ProjectOptionPickResult } from '../projectAdapters/types';

export function createTkvscApi(options: CreateTkvscApiOptions): TkvscApi {
    const ioContext = {
        getHost: options.getHost,
        getBridgeEnv: options.getBridgeEnv,
    };

    return {
        apiVersion: TKVSC_API_VERSION,
        extensionId: options.extensionId,
        views: TKVSC_VIEWS,
        contextValues: TKVSC_ARCHIVE_CONTEXT,
        onDidReady: options.onDidReadyEmitter.event,
        resolveProjectRoot,
        readRawBytes: (uri) => readRawBytes(uri, ioContext),
        writeRawBytes: (uri, data) => writeRawBytes(uri, data, ioContext),
        getBridge: () => ({
            bridgePath: '',
            getHost: options.getHost,
            getPython: options.getHost,
            getBridgeEnv: options.getBridgeEnv,
            // The second argument used to be the bridge script's path; it is ignored.
            runBridgeJsonAsync: <T>(host: string, _bridgePath: string, args: string[], stdin?: string, env?: NodeJS.ProcessEnv) =>
                runBridgeJsonAsync<T>(host, args, stdin, env),
        }),
        getProjectRoots: options.getProjectRoots,
        registerFormatHandler: options.registerFormatHandler,
        registerGameProfile: options.registerGameProfile,
        getActiveGameProfile: options.getActiveGameProfile,
        getGameProfile: options.getGameProfile,
        registerProjectAdapter: options.registerProjectAdapter,
        detectProjectAdapter: options.detectProjectAdapter,
        detectProjectAdapterAsync: options.detectProjectAdapterAsync,
        getProjectAdapters: options.getProjectAdapters,
    };
}
