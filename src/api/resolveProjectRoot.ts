import * as path from 'path';
import type { TkvscTreeItemLike } from './types';
import { TKVSC_ARCHIVE_CONTEXT } from './constants';

function asTreeItem(item: unknown): TkvscTreeItemLike | undefined {
    if (!item || typeof item !== 'object') {
        return undefined;
    }
    const candidate = item as TkvscTreeItemLike;
    if (!candidate.resourceUri?.fsPath) {
        return undefined;
    }
    return candidate;
}

/**
 * Extract a project folder path from a projects-tree context-menu item.
 * Supports the context values addons typically hook (`archiveRoot`, etc.).
 */
export function resolveProjectRoot(item: unknown): string | undefined {
    const treeItem = asTreeItem(item);
    if (!treeItem?.resourceUri) {
        return undefined;
    }

    const fsPath = treeItem.resourceUri.fsPath;
    const contextValue = treeItem.contextValue;

    switch (contextValue) {
        case TKVSC_ARCHIVE_CONTEXT.archiveRoot:
            return fsPath;
        case TKVSC_ARCHIVE_CONTEXT.archiveProjectDir:
        case TKVSC_ARCHIVE_CONTEXT.archiveProjectDirActive:
            return fsPath;
        case TKVSC_ARCHIVE_CONTEXT.tkmmOptionsRoot:
            return path.dirname(fsPath);
        case TKVSC_ARCHIVE_CONTEXT.tkmmOption:
        case TKVSC_ARCHIVE_CONTEXT.tkmmOptionActive:
            return path.dirname(path.dirname(path.dirname(fsPath)));
        case TKVSC_ARCHIVE_CONTEXT.tkmmOptionGroup:
            return path.dirname(path.dirname(fsPath));
        default:
            return undefined;
    }
}
