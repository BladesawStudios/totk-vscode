/** Published extension identifier for addon `extensionDependencies`. */
export const TKVSC_EXTENSION_ID = 'TKVSC-Team.totk-vscode';

export const TKVSC_API_VERSION = 1 as const;

/** Stable view IDs for addon menu `when` clauses. */
export const TKVSC_VIEWS = {
    archives: 'totk-editor.archives',
    gameDump: 'totk-editor.gameDump',
    gameDumpSearch: 'totk-editor.gameDumpSearch',
} as const;

/** `viewItem` context values on the projects (archives) tree. */
export const TKVSC_ARCHIVE_CONTEXT = {
    archiveRoot: 'archiveRoot',
    archiveProjectDir: 'archiveProjectDir',
    archiveProjectDirActive: 'archiveProjectDirActive',
    archiveDir: 'archiveDir',
    archiveVirtualDir: 'archiveVirtualDir',
    archiveFile: 'archiveFile',
    archiveTkproj: 'archiveTkproj',
    archiveVirtualFile: 'archiveVirtualFile',
    archivePackage: 'archivePackage',
    archiveRomfsDir: 'archiveRomfsDir',
    archiveRomfsVirtualDir: 'archiveRomfsVirtualDir',
    archiveRomfsFile: 'archiveRomfsFile',
    archiveRomfsVirtualFile: 'archiveRomfsVirtualFile',
    archiveRomfsPackage: 'archiveRomfsPackage',
    tkmmOptionsRoot: 'tkmmOptionsRoot',
    tkmmOptionGroup: 'tkmmOptionGroup',
    tkmmOption: 'tkmmOption',
    tkmmOptionActive: 'tkmmOptionActive',
} as const;
