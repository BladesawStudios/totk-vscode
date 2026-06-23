import { resolveRomfsPathForGame } from './gameProfile';

/** Resolve the RomFS root for the active game profile (or an explicit `gameId`). */
export function resolveRomfsPath(gameId?: string): string {
    return resolveRomfsPathForGame(gameId);
}
