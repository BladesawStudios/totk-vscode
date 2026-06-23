/** SARC-based archives — patterns come from the active {@link GameProfile} via {@link archiveRegistry}. */

export {
    ARCHIVE_FILE_PATTERN,
    archiveCacheKey,
    getArchivePhysicalPath,
    getDiskArchivePath,
    getLocatorInsideDiskArchive,
    isArchiveBrowsePath,
    isArchiveFile,
    isArchiveFileName,
    isBarsAudioArchive,
    isBntxTextureUri,
    isBwavAudioFile,
    isPathInsideArchive,
    isTxtgFile,
    pathContainsArchive,
    registerArchivePattern,
} from './archiveRegistry';
