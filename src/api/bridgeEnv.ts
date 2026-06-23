import * as vscode from 'vscode';
import { resolveRomfsPath } from '../romfs';
import { getHandlerManifestPath } from '../handlerManifest';

/** Bridge environment variables passed to `totk_bridge.py`. */
export function getBridgeEnv(): NodeJS.ProcessEnv {
    const config = vscode.workspace.getConfiguration('TKVSC');
    const romfsPath = resolveRomfsPath();
    const extraAamp = config.get<string[]>('extraAampExtensions', []);
    const manifestPath = getHandlerManifestPath() ?? '';
    return {
        ...process.env,
        TOTK_EDITOR_ROMFS: romfsPath,
        TKVSC_HANDLER_MANIFEST: manifestPath,
        TOTK_TAG_PRODUCT_FORMAT: config.get<string>('tagProductFormat', 'json'),
        TOTK_EXTRA_AAMP_EXTS: extraAamp.map((ext) => ext.replace(/^\./, '')).join(','),
        TOTK_BYML_INLINE_CONTAINER_MAX_COUNT: String(
            config.get<number>('bymlInlineContainerMaxCount', 1),
        ),
    };
}
