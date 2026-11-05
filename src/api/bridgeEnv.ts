import * as vscode from 'vscode';
import {
    getActiveGameProfile,
    getActiveMsbtConfigPath,
    getCompressedSuffixes,
    getGameProfileRegistry,
    resolveRomfsPathForGame,
} from '../gameProfile';
import { getHandlerManifestPath } from '../handlerManifest';
import { getAampHashNamesPath } from '../aampHashNames';
import { getIndexPathsForGame } from '../indexPaths';

/** Bridge environment variables passed to the host. */
export function getBridgeEnv(): NodeJS.ProcessEnv {
    const config = vscode.workspace.getConfiguration('TKVSC');
    const profile = getActiveGameProfile();
    const romfsPath = resolveRomfsPathForGame(profile.id);
    const extraAamp = config.get<string[]>('extraAampExtensions', []);
    const manifestPath = getHandlerManifestPath() ?? '';
    const aampHashNamesPath = getAampHashNamesPath() ?? '';
    const archiveExtensions = profile.indexing?.archiveExtensions ?? [];
    const msbtConfigPath = getActiveMsbtConfigPath();
    const indexPaths = getIndexPathsForGame(profile.id);
    return {
        ...process.env,
        TOTK_EDITOR_ROMFS: romfsPath,
        TKVSC_ROMFS: romfsPath,
        TKVSC_GAME_ID: profile.id,
        TKVSC_COMPRESSION_BACKEND: profile.compressionBackend,
        TKVSC_ARCHIVE_EXTENSIONS: archiveExtensions.join(','),
        TKVSC_ARCHIVE_PATTERNS: JSON.stringify(profile.archivePatterns ?? []),
        TKVSC_COMPRESSED_SUFFIXES: getCompressedSuffixes(profile.id).join(','),
        TKVSC_ZSDIC_PATH: profile.zsDicPath ?? '',
        TKVSC_HASH_NAMES: profile.hashNamesResolvedPath ?? '',
        TKVSC_AAMP_NAME_TABLE: profile.aampNameTable ?? '',
        TKVSC_PROFILE_AAMP_NAMES: profile.aampHashNamesResolvedPath ?? '',
        TKVSC_XLINK_ELINK_VERSION: profile.xlinkVersions?.elink?.toString() ?? '',
        TKVSC_XLINK_SLINK_VERSION: profile.xlinkVersions?.slink?.toString() ?? '',
        TKVSC_HOST_PLUGIN: profile.hostPluginResolvedPath ?? '',
        TKVSC_HANDLER_MANIFEST: manifestPath,
        TKVSC_AAMP_HASH_NAMES: aampHashNamesPath,
        TKVSC_ROMFS_INDEX: indexPaths?.romfsIndex ?? '',
        TKVSC_CANONICAL_INDEX: indexPaths?.canonicalIndex ?? '',
        ...(msbtConfigPath ? { TKVSC_MSBT_CONFIG: msbtConfigPath } : {}),
        TOTK_TAG_PRODUCT_FORMAT: config.get<string>('tagProductFormat', 'json'),
        TOTK_EXTRA_AAMP_EXTS: extraAamp.map((ext) => ext.replace(/^\./, '')).join(','),
        TOTK_BYML_INLINE_CONTAINER_MAX_COUNT: String(
            config.get<number>('bymlInlineContainerMaxCount', 1),
        ),
        TOTK_PTCL_VECTOR_FORMAT: config.get<string>('ptclVectorFormat', 'compact'),
    };
}
