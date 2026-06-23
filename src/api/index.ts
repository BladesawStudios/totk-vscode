import { runBridgeJsonAsync } from '../bridge';
import {
    TKVSC_API_VERSION,
    TKVSC_ARCHIVE_CONTEXT,
    TKVSC_EXTENSION_ID,
    TKVSC_VIEWS,
} from './constants';
import { readRawBytes, writeRawBytes } from './rawFileIo';
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
export { getBridgeEnv } from './bridgeEnv';
export { readRawBytes, writeRawBytes } from './rawFileIo';
export { resolveProjectRoot } from './resolveProjectRoot';
export type { TkvscApi, TkvscBridgeAccess, TkvscTreeItemLike } from './types';
export type { FormatRegistration, BridgeHandlerRegistration } from '../formatRegistry';
export type { GameProfile, GameProfileRegistration } from '../gameProfile';
export type { ProjectAdapter, ProjectOptionRef } from '../projectAdapters/types';

export function createTkvscApi(options: CreateTkvscApiOptions): TkvscApi {
    const ioContext = {
        bridgePath: options.bridgePath,
        getPython: options.getPython,
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
            bridgePath: options.bridgePath,
            getPython: options.getPython,
            getBridgeEnv: options.getBridgeEnv,
            runBridgeJsonAsync,
        }),
        getProjectRoots: options.getProjectRoots,
        registerFormatHandler: options.registerFormatHandler,
        registerBridgeHandler: options.registerBridgeHandler,
        registerGameProfile: options.registerGameProfile,
        getActiveGameProfile: options.getActiveGameProfile,
        getGameProfile: options.getGameProfile,
        registerProjectAdapter: options.registerProjectAdapter,
        detectProjectAdapter: options.detectProjectAdapter,
        getProjectAdapters: options.getProjectAdapters,
    };
}
