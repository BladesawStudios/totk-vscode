import * as vscode from 'vscode';
import {
    getActiveGameProfile,
    getActiveMsbtConfigPath,
    getGameProfileRegistry,
    resolveRomfsPathForGame,
} from '../gameProfile';
import { getHandlerManifestPath } from '../handlerManifest';
import { getAampHashNamesPath } from '../aampHashNames';

/** Bridge environment variables passed to `totk_bridge.py`. */
export function getBridgeEnv(): NodeJS.ProcessEnv {
    const config = vscode.workspace.getConfiguration('TKVSC');
    const profile = getActiveGameProfile();
    const romfsPath = resolveRomfsPathForGame(profile.id);
    const extraAamp = config.get<string[]>('extraAampExtensions', []);
    const manifestPath = getHandlerManifestPath() ?? '';
    const aampHashNamesPath = getAampHashNamesPath() ?? '';
    const archiveExtensions = profile.indexing?.archiveExtensions ?? [];
    const msbtConfigPath = getActiveMsbtConfigPath();
    return {
        ...process.env,
        TOTK_EDITOR_ROMFS: romfsPath,
        TKVSC_ROMFS: romfsPath,
        TKVSC_GAME_ID: profile.id,
        TKVSC_COMPRESSION_BACKEND: profile.compressionBackend,
        TKVSC_ARCHIVE_EXTENSIONS: archiveExtensions.join(','),
        TKVSC_HANDLER_MANIFEST: manifestPath,
        TKVSC_AAMP_HASH_NAMES: aampHashNamesPath,
        ...(msbtConfigPath ? { TKVSC_MSBT_CONFIG: msbtConfigPath } : {}),
        TOTK_TAG_PRODUCT_FORMAT: config.get<string>('tagProductFormat', 'json'),
        TOTK_EXTRA_AAMP_EXTS: extraAamp.map((ext) => ext.replace(/^\./, '')).join(','),
        TOTK_BYML_INLINE_CONTAINER_MAX_COUNT: String(
            config.get<number>('bymlInlineContainerMaxCount', 1),
        ),
    };
}
