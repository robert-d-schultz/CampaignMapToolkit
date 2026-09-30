using System;
using System.Collections.Generic;
using System.Linq;

namespace CAIME.Upscaler
{
    /// <summary>
    /// Structure-preserving upscale, built on top of a nearest-neighbour block-fill
    /// (<see cref="MapHexFile.ReplicateHexBlocks"/>, shared with the nearest-neighbour upscaler
    /// <see cref="MapHexFile.UpscaleMapHex"/>), then corrected in passes:
    /// <list type="bullet">
    /// <item>A settlement-adjacent beach's impassable flag is cleared from the source before sampling
    /// and recomputed from scratch afterwards, once the settlement is pinned at its final position -
    /// see <see cref="ClearSettlementAdjacentBeachImpassability"/>.</item>
    /// <item>Settlements (town slot + sprawl) are cut back out of the block-fill and re-stamped at
    /// their original, unscaled footprint, so a town doesn't balloon by the upscale factor. Each is
    /// anchored on whichever of its hexes has a relationship to the surrounding terrain worth keeping -
    /// a port slot, or a slot pressed against impassable terrain - see
    /// <see cref="ChooseSettlementAnchor"/>.</item>
    /// <item>"Nice" Areas of Interest - a filled hexagonal disk of some radius (1, 7, 19, 37... hexes) -
    /// are re-stamped as a clean disk of the (rounded-up) scaled radius instead of the jagged
    /// approximation nearest-neighbour sampling alone would produce. Any other-shaped AoI is left to
    /// the ordinary block-fill.</item>
    /// <item>Roads and rivers are cut back out and re-drawn as a one-hex-wide line between each pair of
    /// connected source hexes' scaled anchors, instead of flood-filling their whole block. A residual
    /// "1 hex apart at a gentle bend" artefact (see <see cref="RemoveIncidentalTouches"/>) is then
    /// cleaned up wherever it can be without disconnecting anything.</item>
    /// <item>Coastlines are repaired afterwards: fragmented sea pockets are merged into a single run
    /// along each land hex's edge, and land connections thinner than 3 hexes (a "pinch point") are
    /// widened - both are checked repeatedly since fixing one hex's shoreline can affect its
    /// neighbours'. Extending a river's mouth onto former sea as part of this can recreate the same
    /// incidental-triangle artefact as above, so that cleanup pass runs a second time afterwards,
    /// specifically for rivers, before anything downstream reads the river's shape.</item>
    /// <item>Beach hexes left with no remaining sea neighbour (a side effect of the coastline repair
    /// above) are demoted back to plain land - a beach that doesn't touch the coast is invalid.</item>
    /// <item>Region assignments that bled across a river acting as a region border - a thin sliver of
    /// bank hexes left on the wrong side, however long it runs - are corrected back, see
    /// <see cref="RepairRegionBleedNearRivers"/>. Runs after every pass above that can still change the
    /// river's shape, since this reads the river layout as ground truth for where a region border
    /// should fall.</item>
    /// <item>Region borders (<see cref="Hex.IsBorder"/>/<see cref="Hex.RegionEdgeMask"/>) are recomputed
    /// from scratch at the end, once every other pass has settled on final region assignments.</item>
    /// </list>
    /// </summary>
    public static class MapUpscaler
    {
        private const int MAX_COASTLINE_REPAIR_PASSES = 6;
        private const int MIN_LAND_RUN                = 3;

        public static bool Upscale(Project project, double factor)
        {
            if (factor <= 1.0)
            {
                return false;
            }

            var map = project.MapHexFile;

            var oldData   = map.HexData;
            var oldWidth  = map.MapWidth;
            var oldHeight = map.MapHeight;

            // A settlement-adjacent beach's impassable flag is a *derived* property of the settlement's
            // position, not independent terrain data - and the settlement itself is about to be pinned
            // to a possibly-different spot than where ordinary nearest-neighbour sampling would put it.
            // Sampling the flag now would leave it stranded wherever the old beach hex happened to land,
            // unrelated to the settlement's actual new position. Clear it from the source before
            // sampling and recompute it from scratch once the settlement is pinned (see the end of this
            // method) instead.
            var clearedImpassableBeaches = ClearSettlementAdjacentBeachImpassability(oldData, oldWidth, oldHeight);

            var newData = MapHexFile.ReplicateHexBlocks(oldData, oldWidth, oldHeight, factor, out uint newWidth, out uint newHeight, out _);

            RestoreImpassability(oldData, clearedImpassableBeaches);

            // Settlements, AoIs and roads/rivers are re-placed explicitly below at their true shape -
            // strip the flood-filled copies the block-fill left behind first.
            foreach (var hex in newData)
            {
                hex.TownSlotIndex = Hex.INVALID_SLOT_INDEX;
                hex.IsTownSprawl  = false;
                hex.IsRoad        = false;
                hex.IsRiver       = false;
            }

            map.ReplaceGrid(newWidth, newHeight, newData);

            var pinnedPositions = PinSettlements(map, oldData, oldWidth, oldHeight, newWidth, newHeight, factor);
            PinNiceAreasOfInterest(map, oldData, oldWidth, oldHeight, newWidth, newHeight, factor);
            var roadAnchors  = RetraceLines(map, oldData, oldWidth, oldHeight, newWidth, newHeight, factor, isRoad: true, pinnedPositions);
            var riverAnchors = RetraceLines(map, oldData, oldWidth, oldHeight, newWidth, newHeight, factor, isRoad: false, pinnedPositions);

            // A gentle (60 degree) bend still occasionally lands its two path edges 1 hex apart from
            // each other right at the shared vertex (see the class-level remarks) - clean up whatever
            // non-anchor hex is causing that, as long as doing so doesn't disconnect anything.
            RemoveIncidentalTouches(map, roadAnchors, isRoad: true);
            RemoveIncidentalTouches(map, riverAnchors, isRoad: false);

            map.UpdateHexTypes();
            map.CalculateRoadEdgeMasks();
            map.CalculateRiverEdgeMasks();

            var riverIndicesBeforeCoastlineRepair = new HashSet<int>(GetFeatureIndices(map, isRoad: false));
            RepairCoastline(map);
            CleanupOrphanBeaches(map);

            // Extending a river's mouth onto former sea (see AbsorbIntoLand) can recreate exactly the
            // kind of incidental triangle the pass above already cleaned up - that new mouth hex was
            // never part of riverAnchors, so without this second pass it would sit there uncleaned for
            // RepairRegionBleedNearRivers to read a river shape that still has a stray triangle in it.
            // Every such newly-extended hex is added as an anchor too, so this pass can never remove the
            // extension it exists to protect.
            var riverAnchorsAfterCoastlineRepair = new HashSet<int>(riverAnchors);
            foreach (var index in GetFeatureIndices(map, isRoad: false))
            {
                if (!riverIndicesBeforeCoastlineRepair.Contains(index))
                {
                    riverAnchorsAfterCoastlineRepair.Add(index);
                }
            }
            RemoveIncidentalTouches(map, riverAnchorsAfterCoastlineRepair, isRoad: false);

            RepairRegionBleedNearRivers(map);
            RecalculateSettlementAdjacentBeachImpassability(map);

            map.UpdateHexTypes();
            map.CalculateRoadEdgeMasks();
            map.CalculateRiverEdgeMasks();

            // Region shapes only reach their final form once the coastline/region repairs above have
            // settled, so borders are (re)computed from scratch here rather than reused from the block-fill.
            BordersExporter.GenerateRegionEdges(map);
            map.CalculateRegionEdgeMasks();

            return true;
        }

        /// <summary>
        /// The new-grid index of an offset coordinate, clamped into bounds instead of discarded.
        /// Settlement/road/river placement comes from scaling hex centres, so a hex right at the map
        /// edge can land just outside it by one hex - dropping that hex instead of clamping it was
        /// leaving visible 1-hex gaps in otherwise-connected roads/rivers at the edges.
        /// </summary>
        private static int NewGridIndex(int q, int r, uint width, uint height)
        {
            q = Math.Min(Math.Max(q, 0), (int)width - 1);
            r = Math.Min(Math.Max(r, 0), (int)height - 1);
            return (r * (int)width) + q;
        }

        private static int ScaledIndex(Hex hex, double factor, uint newWidth, uint newHeight)
        {
            var (q, r) = HexGeometry.Scale(hex.Q, hex.R, factor);
            return NewGridIndex(q, r, newWidth, newHeight);
        }

        // ----------------------------------------------------------------- Settlements

        private static bool IsSettlementHex(Hex hex) => hex.IsTownSprawl || hex.TownSlotIndex != Hex.INVALID_SLOT_INDEX;

        /// <summary>
        /// Finds beach hexes adjacent to a settlement that are flagged impassable (a rule the source
        /// map always applies - a settlement's immediately adjacent beach is impassable) and clears the
        /// flag, returning the indices touched so <see cref="RestoreImpassability"/> can put it back.
        /// </summary>
        internal static List<int> ClearSettlementAdjacentBeachImpassability(Hex[] oldData, uint oldWidth, uint oldHeight)
        {
            var cleared = new List<int>();

            for (int i = 0; i < oldData.Length; ++i)
            {
                var hex = oldData[i];
                if (!hex.IsBeach || hex.IsPassable)
                {
                    continue;
                }

                for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                {
                    int nbrIndex = HexGridUtility.GetNeighbourIndexFast(hex, dir, (int)oldWidth, (int)oldHeight);
                    if (nbrIndex != -1 && IsSettlementHex(oldData[nbrIndex]))
                    {
                        hex.IsPassable = true;
                        cleared.Add(i);
                        break;
                    }
                }
            }

            return cleared;
        }

        internal static void RestoreImpassability(Hex[] oldData, List<int> indices)
        {
            foreach (var i in indices)
            {
                oldData[i].IsPassable = false;
            }
        }

        /// <summary>
        /// Re-derives "beach adjacent to a settlement is impassable" directly on the new grid, once the
        /// settlement is pinned at its final position - the source's version of this flag was cleared
        /// before sampling (<see cref="ClearSettlementAdjacentBeachImpassability"/>) specifically so it
        /// wouldn't get left behind at whatever unrelated spot ordinary sampling put the old beach hex.
        /// The rule is about the ring *around* a settlement: a settlement hex that happens to sit on a
        /// beach is part of the town, not part of its impassable border, and is skipped - sealing it off
        /// would wall the settlement in behind its own footprint.
        /// </summary>
        internal static void RecalculateSettlementAdjacentBeachImpassability(MapHexFile map)
        {
            for (int i = 0; i < map.Capacity; ++i)
            {
                var hex = map.HexData[i];
                if (!hex.IsBeach || IsSettlementHex(hex))
                {
                    continue;
                }

                for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                {
                    int nbrIndex = map.GetNeighbourIndex(hex, dir);
                    if (nbrIndex != -1 && IsSettlementHex(map.HexData[nbrIndex]))
                    {
                        hex.IsPassable = false;
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Finds every connected settlement blob (town slot(s) + sprawl, the same connectivity
        /// <c>SprawlValidator</c> uses) on the source grid and transplants each one, unscaled, onto the
        /// new grid. The whole blob is anchored to a single chosen hex (see
        /// <see cref="ChooseSettlementAnchor"/>) so it lands where that hex was scaled to, and every
        /// other hex keeps its position relative to the anchor (<see cref="HexGeometry.Translate"/>)
        /// so the shape survives the move.
        /// </summary>
        /// <returns>
        /// The final new-grid index of every settlement hex, keyed by its source-grid index -
        /// <see cref="RetraceLines"/> needs this to connect a road/river up to a settlement's *actual*
        /// stamped position rather than its plain scaled position, which can land somewhere else entirely.
        /// </returns>
        private static Dictionary<int, int> PinSettlements(MapHexFile map, Hex[] oldData, uint oldWidth, uint oldHeight, uint newWidth, uint newHeight, double factor)
        {
            var pinnedPositions = new Dictionary<int, int>();
            int oldCapacity     = oldData.Length;
            var visited         = new bool[oldCapacity];

            for (int startIndex = 0; startIndex < oldCapacity; ++startIndex)
            {
                if (visited[startIndex] || !IsSettlementHex(oldData[startIndex]))
                {
                    continue;
                }

                var blob  = new List<int>();
                var queue = new Queue<int>();
                queue.Enqueue(startIndex);
                visited[startIndex] = true;

                while (queue.Count > 0)
                {
                    int index = queue.Dequeue();
                    blob.Add(index);
                    var hex = oldData[index];

                    for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                    {
                        int nbrIndex = HexGridUtility.GetNeighbourIndexFast(hex, dir, (int)oldWidth, (int)oldHeight);
                        if (nbrIndex == -1 || visited[nbrIndex] || !IsSettlementHex(oldData[nbrIndex]))
                        {
                            continue;
                        }

                        visited[nbrIndex] = true;
                        queue.Enqueue(nbrIndex);
                    }
                }

                StampSettlementBlob(map, oldData, blob, oldWidth, oldHeight, newWidth, newHeight, factor, pinnedPositions);
            }

            return pinnedPositions;
        }

        private const int MIN_IMPASSABLE_BLOB_SIZE = 3;

        /// <summary>
        /// The hex the whole blob is positioned relative to. Every candidate here is a hex whose
        /// *relationship to the terrain around it* is load-bearing, and the terrain is sampled
        /// independently of the settlement (by the block-fill), so anchoring on such a
        /// hex is what keeps that relationship intact: the anchor lands wherever the terrain it belongs
        /// to landed, and the rest of the blob is carried along with it.
        ///
        /// A port slot wins outright - staying adjacent to sea is a hard requirement, not a nicety. A
        /// slot pressed against a wall of impassable terrain comes next: a town filling the one gap in a
        /// mountain range is deliberate authoring, and anchoring on the main slot instead would let
        /// rounding drift the settlement off the wall and open a passage that was never meant to exist.
        /// Everything after that is the plain fallback chain, ending at an arbitrary blob member.
        /// </summary>
        internal static int ChooseSettlementAnchor(Hex[] oldData, List<int> blob, uint oldWidth, uint oldHeight)
        {
            int anchorIndex = blob.Where(i => oldData[i].TownSlotIndex == Hex.PORT_SLOT_INDEX)
                                  .DefaultIfEmpty(-1).First();
            if (anchorIndex == -1)
            {
                anchorIndex = blob.Where(i => oldData[i].TownSlotIndex == Hex.MAIN_SLOT_INDEX
                                           && IsAgainstImpassableBlob(oldData, i, oldWidth, oldHeight))
                                  .DefaultIfEmpty(-1).First();
            }
            if (anchorIndex == -1)
            {
                anchorIndex = blob.Where(i => oldData[i].TownSlotIndex != Hex.INVALID_SLOT_INDEX
                                           && IsAgainstImpassableBlob(oldData, i, oldWidth, oldHeight))
                                  .DefaultIfEmpty(-1).First();
            }
            if (anchorIndex == -1)
            {
                anchorIndex = blob.Where(i => oldData[i].TownSlotIndex == Hex.MAIN_SLOT_INDEX)
                                  .DefaultIfEmpty(-1).First();
            }
            if (anchorIndex == -1)
            {
                anchorIndex = blob.Where(i => oldData[i].TownSlotIndex != Hex.INVALID_SLOT_INDEX)
                                  .DefaultIfEmpty(-1).First();
            }
            if (anchorIndex == -1)
            {
                anchorIndex = blob[0];
            }

            return anchorIndex;
        }

        /// <summary>
        /// Whether this hex sits against a *blob* of impassable terrain - a mountain range or canyon
        /// wall of at least <see cref="MIN_IMPASSABLE_BLOB_SIZE"/> hexes - rather than a lone impassable
        /// hex, which carries no shape worth pressing a settlement against. Coast is excluded: the
        /// impassable ring the map keeps around a coastal settlement
        /// (<see cref="RecalculateSettlementAdjacentBeachImpassability"/>) would otherwise match here for
        /// practically every seaside town, and the port slot already covers what those need.
        /// </summary>
        private static bool IsAgainstImpassableBlob(Hex[] oldData, int index, uint oldWidth, uint oldHeight)
        {
            var hex = oldData[index];
            for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
            {
                int nbrIndex = HexGridUtility.GetNeighbourIndexFast(hex, dir, (int)oldWidth, (int)oldHeight);
                if (nbrIndex == -1 || !IsImpassableTerrain(oldData[nbrIndex]))
                {
                    continue;
                }

                if (CountImpassableBlob(oldData, nbrIndex, oldWidth, oldHeight) >= MIN_IMPASSABLE_BLOB_SIZE)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsImpassableTerrain(Hex hex) => hex.IsImpassable && hex.IsLand && !hex.IsCoast;

        /// <summary>
        /// Size of the connected impassable blob containing <paramref name="startIndex"/>, counted only
        /// as far as <see cref="MIN_IMPASSABLE_BLOB_SIZE"/> - past that the exact size doesn't matter,
        /// and stopping early keeps this from walking an entire mountain range per candidate hex.
        /// </summary>
        private static int CountImpassableBlob(Hex[] oldData, int startIndex, uint oldWidth, uint oldHeight)
        {
            var visited = new HashSet<int> { startIndex };
            var queue   = new Queue<int>();
            queue.Enqueue(startIndex);
            int count = 0;

            while (queue.Count > 0 && count < MIN_IMPASSABLE_BLOB_SIZE)
            {
                var hex = oldData[queue.Dequeue()];
                ++count;

                for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                {
                    int nbrIndex = HexGridUtility.GetNeighbourIndexFast(hex, dir, (int)oldWidth, (int)oldHeight);
                    if (nbrIndex == -1 || visited.Contains(nbrIndex) || !IsImpassableTerrain(oldData[nbrIndex]))
                    {
                        continue;
                    }

                    visited.Add(nbrIndex);
                    queue.Enqueue(nbrIndex);
                }
            }

            return count;
        }

        /// <summary>
        /// Choosing the right anchor still isn't quite enough to keep a town on its wall. The settlement
        /// is placed by scaling its anchor *forward*, while the terrain around it was sampled by mapping
        /// each new hex *back* to the old grid, and at a fractional factor those two disagree about
        /// exactly where the boundary between two old hexes falls - so the anchor can land one hex short
        /// of the wall it belongs against. Shifts it by a single hex when that has happened.
        /// </summary>
        private static int NudgeAnchorAgainstImpassable(MapHexFile map, int anchorIndex)
        {
            if (IsAgainstImpassableOnNewGrid(map, anchorIndex))
            {
                return anchorIndex;
            }

            var anchorHex = map.HexData[anchorIndex];
            for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
            {
                int nbrIndex = map.GetNeighbourIndex(anchorHex, dir);
                if (nbrIndex == -1)
                {
                    continue;
                }

                var nbr = map.HexData[nbrIndex];
                if (nbr.IsSea || IsImpassableTerrain(nbr))
                {
                    continue; // the town can't be moved into the water, or inside the wall itself
                }

                if (IsAgainstImpassableOnNewGrid(map, nbrIndex))
                {
                    return nbrIndex;
                }
            }

            return anchorIndex; // nothing better one hex out - leave it where the scaling put it
        }

        private static bool IsAgainstImpassableOnNewGrid(MapHexFile map, int index)
        {
            var hex = map.HexData[index];
            for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
            {
                int nbrIndex = map.GetNeighbourIndex(hex, dir);
                if (nbrIndex != -1 && IsImpassableTerrain(map.HexData[nbrIndex]))
                {
                    return true;
                }
            }

            return false;
        }

        private static void StampSettlementBlob(MapHexFile map, Hex[] oldData, List<int> blob, uint oldWidth, uint oldHeight, uint newWidth, uint newHeight, double factor, Dictionary<int, int> pinnedPositions)
        {
            int anchorIndex = ChooseSettlementAnchor(oldData, blob, oldWidth, oldHeight);

            var anchorHex      = oldData[anchorIndex];
            int newAnchorIndex = ScaledIndex(anchorHex, factor, newWidth, newHeight);

            // Only for a blob anchored on impassable terrain in the first place - for anything else the
            // scaled position is already the right answer and shifting it would just be meddling.
            if (anchorHex.TownSlotIndex != Hex.PORT_SLOT_INDEX
                && IsAgainstImpassableBlob(oldData, anchorIndex, oldWidth, oldHeight))
            {
                newAnchorIndex = NudgeAnchorAgainstImpassable(map, newAnchorIndex);
            }

            var newAnchorHex = map.HexData[newAnchorIndex];

            foreach (var index in blob)
            {
                var hex      = oldData[index];
                var (q, r)   = HexGeometry.Translate(hex, anchorHex, newAnchorHex);
                int newIndex = NewGridIndex(q, r, newWidth, newHeight);
                var replaced = map.HexData[newIndex];

                var stamped   = hex.Clone();
                stamped.Q     = replaced.Q;
                stamped.R     = replaced.R;
                stamped.Index = newIndex;

                // Roads/rivers/trade routes are re-derived separately (RetraceLines uses this hex's
                // *pinned* position, recorded below, to connect up to them) - carrying the source's raw
                // route flags over here would place a stale, disconnected fragment at the pin instead.
                stamped.IsRoad         = false;
                stamped.IsRiver        = false;
                stamped.IsBridge       = false;
                stamped.IsTradeRoute   = false;
                stamped.RoadEdgeMask   = 0;
                stamped.RiverEdgeMask  = 0;
                stamped.TradeRouteMask = 0;

                map.HexData[newIndex]  = stamped;
                pinnedPositions[index] = newIndex;
            }
        }

        // ----------------------------------------------------------------- Areas of Interest

        /// <summary>
        /// An Area of Interest whose hexes form a filled hexagonal disk (1, 7, 19, 37... hexes - the
        /// centered hexagonal numbers 3r(r+1)+1) is re-stamped as a clean disk of radius
        /// <c>ceil(r * factor)</c> centred on the scaled anchor, instead of the jagged boundary
        /// nearest-neighbour sampling alone would leave. Any AoI that isn't a clean disk is left
        /// untouched - it already got sampled like any other layer by the block-fill.
        ///
        /// Grouped by *connected component*, not by raw <see cref="Hex.InterestIndex"/> value alone -
        /// the same named area of interest can appear as several separate physical instances on one
        /// map, all sharing one index value. Grouping by value across the whole map would lump every
        /// instance into one "blob" spanning huge, unrelated distances, which never matches a hexagon
        /// and so silently skipped every one of them.
        /// </summary>
        private static void PinNiceAreasOfInterest(MapHexFile map, Hex[] oldData, uint oldWidth, uint oldHeight, uint newWidth, uint newHeight, double factor)
        {
            int oldCapacity = oldData.Length;
            var visited     = new bool[oldCapacity];

            for (int startIndex = 0; startIndex < oldCapacity; ++startIndex)
            {
                sbyte interest = oldData[startIndex].InterestIndex;
                if (visited[startIndex] || interest == Hex.INVALID_AREA_OF_INT_INDEX)
                {
                    continue;
                }

                var blob  = new List<int>();
                var queue = new Queue<int>();
                queue.Enqueue(startIndex);
                visited[startIndex] = true;

                while (queue.Count > 0)
                {
                    int index = queue.Dequeue();
                    blob.Add(index);
                    var hex = oldData[index];

                    for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                    {
                        int nbrIndex = HexGridUtility.GetNeighbourIndexFast(hex, dir, (int)oldWidth, (int)oldHeight);
                        if (nbrIndex == -1 || visited[nbrIndex] || oldData[nbrIndex].InterestIndex != interest)
                        {
                            continue;
                        }

                        visited[nbrIndex] = true;
                        queue.Enqueue(nbrIndex);
                    }
                }

                if (!TryFindHexagon(oldData, blob, out Hex centre, out int radius))
                {
                    continue;
                }

                int newRadius = (int)Math.Ceiling(radius * factor);
                var (q, r)    = HexGeometry.Scale(centre.Q, centre.R, factor);
                var newCentre = new Hex(q, r);

                // Clear whatever the block-fill sampled for *this instance* in the immediate vicinity
                // before stamping a clean disk over it - scoped to the local area (not a full-map scan
                // for this interest value), so it can't touch a different, unrelated instance that
                // happens to share the same interest value elsewhere on the map.
                foreach (var index in HexesWithinRadius(map, newCentre, newRadius + 1))
                {
                    var clearHex = map.HexData[index];
                    if (clearHex.InterestIndex == interest)
                    {
                        clearHex.InterestIndex = Hex.INVALID_AREA_OF_INT_INDEX;
                    }
                }

                foreach (var index in HexesWithinRadius(map, newCentre, newRadius))
                {
                    map.HexData[index].InterestIndex = interest;
                }
            }
        }

        /// <summary>
        /// Tests whether <paramref name="blob"/> is exactly the set of hexes within some radius r of
        /// some centre hex - a filled hexagonal disk. The hex count gives r, and the blob's centroid
        /// gives the centre. A disk of radius r holds exactly as many hexes as the blob, so the blob is
        /// that disk precisely when every one of its hexes lies within r of the centre.
        /// </summary>
        internal static bool TryFindHexagon(Hex[] oldData, List<int> blob, out Hex centre, out int radius)
        {
            centre = null;
            radius = 0;

            int n = blob.Count;
            double rExact  = (-3.0 + Math.Sqrt(9.0 + (12.0 * (n - 1)))) / 6.0;
            int rCandidate = (int)Math.Round(rExact);
            if (rCandidate < 0 || ((3 * rCandidate * (rCandidate + 1)) + 1) != n)
            {
                return false;
            }

            double sumX = 0, sumY = 0;
            foreach (var i in blob)
            {
                var (x, y) = HexGeometry.Centre(oldData[i].Q, oldData[i].R);
                sumX += x;
                sumY += y;
            }

            var (centreQ, centreR) = HexGeometry.NearestHex(sumX / n, sumY / n);
            var candidateCentre    = blob.Select(i => oldData[i]).FirstOrDefault(hex => hex.Q == centreQ && hex.R == centreR);

            if (candidateCentre == null || blob.Any(i => oldData[i].GetDistance(candidateCentre) > rCandidate))
            {
                return false;
            }

            centre = candidateCentre;
            radius = rCandidate;
            return true;
        }

        /// <summary>
        /// Every hex on the grid within <paramref name="radius"/> hexes of <paramref name="centre"/>,
        /// which may itself lie off the grid. No hex that close is more than <paramref name="radius"/>
        /// columns or rows away, so only that square is searched.
        /// </summary>
        private static IEnumerable<int> HexesWithinRadius(MapHexFile map, Hex centre, int radius)
        {
            int width  = (int)map.MapWidth;
            int height = (int)map.MapHeight;

            for (int r = Math.Max(0, centre.R - radius); r <= Math.Min(height - 1, centre.R + radius); ++r)
            {
                for (int q = Math.Max(0, centre.Q - radius); q <= Math.Min(width - 1, centre.Q + radius); ++q)
                {
                    int index = (r * width) + q;
                    if (map.HexData[index].GetDistance(centre) <= radius)
                    {
                        yield return index;
                    }
                }
            }
        }

        // ----------------------------------------------------------------- Roads / rivers

        /// <summary>
        /// Re-draws roads or rivers as one-hex-wide lines, tracing the edges recorded in
        /// <see cref="Hex.RoadEdgeMask"/>/<see cref="Hex.RiverEdgeMask"/> - not every pair of physically
        /// adjacent feature hexes. At any gentle (60 degree) bend in a path, the hexes on either side of
        /// the bend are also physically adjacent to *each other* (an inherent hex-grid property, not a
        /// path connection), so testing raw adjacency draws a spurious third edge and turns every such
        /// bend into a triangle/blob. The edge mask already resolved that ambiguity - the game's own
        /// triangle-chord logic (<see cref="MapHexFile.CalculateRoadEdgeMasks"/>) keeps exactly the two
        /// edges that are actually part of the path - so tracing those bits reproduces the authored
        /// topology exactly.
        ///
        /// A hex that belongs to a settlement uses its <paramref name="pinnedPositions"/> entry (where
        /// <see cref="PinSettlements"/> actually stamped it) instead of its plain scaled position - a
        /// settlement's footprint is deliberately *not* scaled the same way as everything else, so its
        /// stamped position can land somewhere else entirely. Using the plain scaled position for a road
        /// ending at a settlement would leave a gap between the road and the settlement's real position;
        /// this draws however many hexes are needed to actually reach it, reconnecting a road/river to a
        /// settlement (e.g. a dead-end at a town) exactly as it was in the source.
        /// </summary>
        /// <returns>
        /// The new-grid index of every hex that is a *protected* anchor - a source feature hex whose
        /// source-topology degree isn't exactly 2 (a dead end, an isolated hex, or a genuine 3+-way
        /// junction), as opposed to a plain pass-through hex along a straight-ish stretch or a purely
        /// interpolated waypoint along a line between two anchors. <see cref="RemoveIncidentalTouches"/>
        /// must never remove a protected anchor.
        ///
        /// Deliberately *not* every source feature hex: independently scaling and rounding each hex's
        /// position (see <see cref="GetPlacedIndex"/>) can - even along a dead-straight or shallow-angle
        /// run with zero ambiguity in the source - occasionally land three *consecutive* pass-through
        /// hexes' placed positions mutually adjacent to each other, purely as a rounding artefact (not a
        /// real bend or junction). If every source hex were protected, <see cref="RemoveIncidentalTouches"/>
        /// could never clean that up - all three would look untouchable even though none of them
        /// represents a real feature. A plain pass-through hex (source degree exactly 2, no branch or
        /// endpoint) isn't structurally special, so it's fine to drop if doing so resolves a redundant
        /// local touch; only hexes that genuinely terminate or branch the path need to survive no matter
        /// what.
        /// </returns>
        private static HashSet<int> RetraceLines(MapHexFile map, Hex[] oldData, uint oldWidth, uint oldHeight, uint newWidth, uint newHeight, double factor, bool isRoad, Dictionary<int, int> pinnedPositions)
        {
            var anchorIndices = new HashSet<int>();
            int oldCapacity    = oldData.Length;

            for (int index = 0; index < oldCapacity; ++index)
            {
                var hex = oldData[index];
                bool isFeature = isRoad ? hex.IsRoad : hex.IsRiver;
                if (!isFeature)
                {
                    continue;
                }

                int anchorIndex  = GetPlacedIndex(index, hex, factor, pinnedPositions, newWidth, newHeight);
                byte mask        = isRoad ? hex.RoadEdgeMask : hex.RiverEdgeMask;
                int sourceDegree = HexGridUtility.GetNumBitsSetInEdgeMask(mask);

                // Keep isolated feature hexes (no edge-mask connection at all) alive at the new resolution.
                SetFeatureFlag(map, anchorIndex, isRoad, true);
                if (sourceDegree != 2 || IsSettlementHex(hex))
                {
                    // A through-town road/river hex can have plain pass-through degree 2 too, but it
                    // still must never be sacrificed - losing it would disconnect the settlement from the
                    // network entirely, which is worse than the cosmetic touch this carve-out exists to fix.
                    anchorIndices.Add(anchorIndex);
                }
                for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                {
                    if ((mask & (1 << dir)) == 0)
                    {
                        continue; // not part of the authored path topology in this direction
                    }

                    int nbrIndex = HexGridUtility.GetNeighbourIndexFast(hex, dir, (int)oldWidth, (int)oldHeight);
                    if (nbrIndex == -1 || nbrIndex < index)
                    {
                        continue; // each undirected edge is only traced once, from its lower-index end
                    }

                    int nbrAnchorIndex = GetPlacedIndex(nbrIndex, oldData[nbrIndex], factor, pinnedPositions, newWidth, newHeight);
                    foreach (var pathIndex in WalkTowards(map, anchorIndex, nbrAnchorIndex))
                    {
                        SetFeatureFlag(map, pathIndex, isRoad, true);
                    }
                }
            }

            return anchorIndices;
        }

        private static int GetPlacedIndex(int index, Hex hex, double factor, Dictionary<int, int> pinnedPositions, uint newWidth, uint newHeight)
        {
            if (pinnedPositions.TryGetValue(index, out int pinned))
            {
                return pinned;
            }

            return ScaledIndex(hex, factor, newWidth, newHeight);
        }

        /// <summary>
        /// The hexes passed through walking from one hex to another, always stepping to whichever
        /// neighbour lies nearest the destination. That neighbour is always one hex closer, so the
        /// walk is a one-hex-wide line no longer than the shortest path between the two, keeping close
        /// to the straight line joining them.
        /// </summary>
        private static IEnumerable<int> WalkTowards(MapHexFile map, int fromIndex, int toIndex)
        {
            var destination  = map.HexData[toIndex];
            int currentIndex = fromIndex;
            yield return currentIndex;

            while (currentIndex != toIndex)
            {
                var current         = map.HexData[currentIndex];
                int nextIndex       = -1;
                double nextDistance = HexGeometry.DistanceSquared(current, destination);

                for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                {
                    int nbrIndex = map.GetNeighbourIndex(current, dir);
                    if (nbrIndex == -1)
                    {
                        continue;
                    }

                    double distance = HexGeometry.DistanceSquared(map.HexData[nbrIndex], destination);
                    if (distance < nextDistance)
                    {
                        nextIndex    = nbrIndex;
                        nextDistance = distance;
                    }
                }

                if (nextIndex == -1)
                {
                    yield break; // boxed in by the map edge - nothing closer to step to
                }

                currentIndex = nextIndex;
                yield return currentIndex;
            }
        }

        /// <summary>
        /// How far (in hexes) from a candidate its former neighbours are allowed to travel
        /// to reconnect with each other before the removal is rejected. This is a *visual* locality bound,
        /// not a graph-theoretic one: a candidate can be a non-cut-vertex of the whole road/river network
        /// (i.e. the network stays "one component") purely because some entirely different, far-off route
        /// happens to link its neighbours back together - but removing it still leaves a real, visible gap
        /// at this specific spot, since nothing nearby actually reconnects. A gentle bend's genuinely
        /// redundant chord reconnects within a couple of hexes (the documented "distance-2 edge needs one
        /// bridging hex" case is 2 hexes away); this is generous relative to that.
        /// </summary>
        private const int LOCAL_RECONNECT_RADIUS = 6;

        /// <summary>
        /// Cleans up the residual "1 hex apart at a gentle bend" artefact (see the class-level remarks).
        /// A hex has an excess connection either because it has more than 2 road/river neighbours, or -
        /// the case a plain degree check misses entirely - because two of its (exactly 2) neighbours are
        /// *also* adjacent to each other, closing a triangle. A gentle bend's three hexes are always
        /// mutually adjacent (an inherent hex-grid property, not a mistake), so a fully-closed triangle
        /// sits at degree 2 on every one of its three vertices - none of them ever trip a ">2 neighbours"
        /// check, yet the triangle itself is still one hex too many for a topology whose source edge mask
        /// only ever authorises 2 of the 3 possible edges. Either way, the fix is to drop one *non-anchor*
        /// member (an anchor is a real source hex's placed position and must never be removed, even right
        /// at a bend's vertex). Tries each non-anchor candidate in turn and commits the first one whose
        /// former neighbours all reconnect with each other nearby (see
        /// <see cref="WouldLeaveVisualGap"/>/<see cref="LOCAL_RECONNECT_RADIUS"/>) - i.e. it was a
        /// redundant, incidental touch, not load-bearing. Repeats because dropping one hex can reveal
        /// another with the same problem next to it.
        ///
        /// An earlier version of this check used graph-wide articulation points (Tarjan's algorithm) -
        /// mathematically exact for "does the network stay one connected component", but that turned out
        /// to be the wrong question: a hex can be provably safe by that measure purely because some
        /// entirely different, far-off route happens to keep the *whole* network connected, while still
        /// leaving a visible break in the road/river at the spot it was removed from. Bounding the
        /// reconnection search to a small radius around the candidate answers the question that actually
        /// matters here - does *this* touch have a *nearby* redundant alternative - and as a side effect
        /// is also cheaper than a full graph traversal.
        /// </summary>
        internal static void RemoveIncidentalTouches(MapHexFile map, HashSet<int> anchorIndices, bool isRoad)
        {
            bool changed = true;
            int guard = 0;
            while (changed && guard++ < 12)
            {
                changed = false;
                var featureIndices = GetFeatureIndices(map, isRoad);

                foreach (int index in featureIndices)
                {
                    bool isFeature = isRoad ? map.HexData[index].IsRoad : map.HexData[index].IsRiver;
                    if (!isFeature)
                    {
                        continue; // removed earlier in this same pass
                    }

                    var neighbourIndices = GetFeatureNeighbourIndices(map, map.HexData[index], isRoad);
                    if (neighbourIndices.Count <= 2 && !HasMutuallyAdjacentPair(map, neighbourIndices, isRoad))
                    {
                        continue;
                    }

                    // Try dropping this hex itself first (the usual case - it's a purely interpolated
                    // midpoint that incidentally touches something outside its own line), then fall back
                    // to dropping one of its neighbours instead (this hex might itself be an anchor
                    // sitting right at a bend's vertex, which must survive).
                    var candidates = new List<int>();
                    if (!anchorIndices.Contains(index))
                    {
                        candidates.Add(index);
                    }
                    foreach (var n in neighbourIndices)
                    {
                        if (!anchorIndices.Contains(n))
                        {
                            candidates.Add(n);
                        }
                    }

                    foreach (var candidate in candidates)
                    {
                        var candidateHex = map.HexData[candidate];
                        var boundary = GetFeatureNeighbourIndices(map, candidateHex, isRoad);

                        SetFeatureFlag(map, candidate, isRoad, false);

                        if (!WouldLeaveVisualGap(map, candidateHex, boundary, isRoad))
                        {
                            changed = true;
                            break;
                        }

                        SetFeatureFlag(map, candidate, isRoad, true);
                    }
                }
            }
        }

        /// <summary>
        /// True if <paramref name="boundary"/> - the feature-neighbours a just-removed hex used to have -
        /// are not all still reachable from each other within <see cref="LOCAL_RECONNECT_RADIUS"/> hexes
        /// of <paramref name="candidate"/> (the removed hex itself). Reachability is a BFS
        /// restricted to that local disk, not the whole map's road/river network - see
        /// <see cref="LOCAL_RECONNECT_RADIUS"/> for why graph-wide reachability is the wrong question.
        /// </summary>
        private static bool WouldLeaveVisualGap(MapHexFile map, Hex candidate, List<int> boundary, bool isRoad)
        {
            if (boundary.Count <= 1)
            {
                return false; // nothing else to reconnect to - removal can't leave a gap
            }

            var remaining = new HashSet<int>(boundary);
            int start = boundary[0];
            remaining.Remove(start);

            var visited = new HashSet<int> { start };
            var queue = new Queue<int>();
            queue.Enqueue(start);

            while (queue.Count > 0 && remaining.Count > 0)
            {
                var hex = map.HexData[queue.Dequeue()];
                foreach (var n in GetFeatureNeighbourIndices(map, hex, isRoad))
                {
                    if (visited.Contains(n))
                    {
                        continue;
                    }

                    if (map.HexData[n].GetDistance(candidate) > LOCAL_RECONNECT_RADIUS)
                    {
                        continue; // stay within the local disk - a reconnection further out isn't "nearby"
                    }

                    visited.Add(n);
                    remaining.Remove(n);
                    queue.Enqueue(n);
                }
            }

            return remaining.Count > 0;
        }

        /// <summary>
        /// True if any two hexes in <paramref name="neighbourIndices"/> are themselves feature-adjacent
        /// to each other - i.e. the hex they're both neighbours of sits at the apex of a closed triangle.
        /// </summary>
        private static bool HasMutuallyAdjacentPair(MapHexFile map, List<int> neighbourIndices, bool isRoad)
        {
            for (int i = 0; i < neighbourIndices.Count; ++i)
            {
                var hexI = map.HexData[neighbourIndices[i]];
                for (int j = i + 1; j < neighbourIndices.Count; ++j)
                {
                    for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                    {
                        if (map.GetNeighbourIndex(hexI, dir) == neighbourIndices[j])
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Every currently-flagged road/river hex, gathered with a single pass over the grid so the
        /// "find over-degree hexes" scan doesn't have to rescan every cell of the grid itself.
        /// </summary>
        private static List<int> GetFeatureIndices(MapHexFile map, bool isRoad)
        {
            var result = new List<int>();
            for (int i = 0; i < map.Capacity; ++i)
            {
                bool isFeature = isRoad ? map.HexData[i].IsRoad : map.HexData[i].IsRiver;
                if (isFeature)
                {
                    result.Add(i);
                }
            }
            return result;
        }

        private static List<int> GetFeatureNeighbourIndices(MapHexFile map, Hex hex, bool isRoad)
        {
            var result = new List<int>();
            for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
            {
                int nbrIndex = map.GetNeighbourIndex(hex, dir);
                if (nbrIndex == -1)
                {
                    continue;
                }

                bool nbrIsFeature = isRoad ? map.HexData[nbrIndex].IsRoad : map.HexData[nbrIndex].IsRiver;
                if (nbrIsFeature)
                {
                    result.Add(nbrIndex);
                }
            }
            return result;
        }

        private static void SetFeatureFlag(MapHexFile map, int index, bool isRoad, bool value)
        {
            if (isRoad)
            {
                map.HexData[index].IsRoad = value;
            }
            else
            {
                map.HexData[index].IsRiver = value;
            }
        }

        // ----------------------------------------------------------------- Coastline

        /// <summary>
        /// Repeatedly scans every land hex for a shoreline that isn't "orthodox" and fixes it two ways:
        /// merges sea neighbours that no longer form a single contiguous run (a fragmented shore) back
        /// into one, and widens a surviving land connection thinner than <see cref="MIN_LAND_RUN"/>
        /// hexes (a "pinch point" - e.g. a peninsula joined to the mainland by just 1-2 hexes). Repeated
        /// because fixing one hex's shoreline can change its neighbours'; stops once a full pass makes no
        /// further changes.
        /// </summary>
        internal static void RepairCoastline(MapHexFile map)
        {
            for (int pass = 0; pass < MAX_COASTLINE_REPAIR_PASSES; ++pass)
            {
                bool changed = false;

                for (int index = 0; index < map.Capacity; ++index)
                {
                    var hex = map.HexData[index];
                    if (hex.IsLand == false || hex.RegionId == Hex.INVALID_REGION_INDEX)
                    {
                        continue;
                    }

                    if (TryFixShoreline(map, hex))
                    {
                        changed = true;
                    }
                }

                if (!changed)
                {
                    break;
                }
            }
        }

        internal static bool TryFixShoreline(MapHexFile map, Hex hex)
        {
            int count = HexGridUtility.NEIGHBOURS_COUNT;
            var neighbourIndices = new int[count];
            var isSea            = new bool[count];

            for (ushort dir = 0; dir < count; ++dir)
            {
                int nbrIndex = map.GetNeighbourIndex(hex, dir);
                if (nbrIndex == -1)
                {
                    return false; // map-edge hex: leave the boundary ambiguity alone
                }

                neighbourIndices[dir] = nbrIndex;
                isSea[dir]            = map.HexData[nbrIndex].IsSea;
            }

            if (!isSea.Any(v => v))
            {
                return false; // fully inland hex - nothing coastal to fix
            }

            bool changed = false;

            // 1) Merge fragmented sea pockets so only one contiguous run touches this hex's edge.
            var seaRuns = FindCyclicRuns(isSea);
            if (seaRuns.Count > 1)
            {
                var keep = seaRuns.OrderByDescending(run => run.Count).First();
                foreach (var run in seaRuns)
                {
                    if (run == keep)
                    {
                        continue;
                    }

                    foreach (var dir in run)
                    {
                        if (AbsorbIntoLand(map, map.HexData[neighbourIndices[dir]], hex))
                        {
                            isSea[dir] = false;
                            changed    = true;
                        }
                    }
                }
            }

            // 2) Widen a land connection thinner than MIN_LAND_RUN by converting the sea hexes
            // immediately flanking it, alternating ends so the run grows from its center outward.
            var isLand   = isSea.Select(v => !v).ToArray();
            var landRuns = FindCyclicRuns(isLand);
            if (landRuns.Count == 1 && landRuns[0].Count > 0 && landRuns[0].Count < MIN_LAND_RUN)
            {
                var landRun = landRuns[0];
                int needed  = MIN_LAND_RUN - landRun.Count;
                int before  = (landRun[0] - 1 + count) % count;
                int after   = (landRun[landRun.Count - 1] + 1) % count;

                for (int i = 0; i < needed; ++i)
                {
                    int dir = (i % 2 == 0) ? before : after;
                    if (AbsorbIntoLand(map, map.HexData[neighbourIndices[dir]], hex))
                    {
                        changed = true;
                    }

                    if (i % 2 == 0)
                    {
                        before = (before - 1 + count) % count;
                    }
                    else
                    {
                        after = (after + 1) % count;
                    }
                }
            }

            return changed;
        }

        /// <summary>
        /// Converts a sea hex to land, always proceeding (the coastline repair this backs still needs to
        /// happen), but patching up two things that specifically depended on the hex staying sea rather
        /// than refusing to touch it at all:
        /// <list type="bullet">
        /// <item>a bridge (<see cref="Hex.IsBridge"/>) spans water - once the water under it is land, it
        /// isn't a bridge any more, so the flag comes off instead of leaving a "bridge" sitting on dry
        /// land;</item>
        /// <item>a river that ends in the sea needs a sea hex to end at - if this was its last remaining
        /// sea neighbour, the hex itself becomes river instead, extending the river by one hex so its
        /// mouth still touches whatever sea is left nearby, rather than leaving the coastline unrepaired
        /// just to avoid moving the river's end point.</item>
        /// </list>
        /// Otherwise inherits the repairing hex's terrain (ground/region/climate/attrition/passability),
        /// the same way <see cref="Hex.AttritionIndex"/> already did - <see cref="Hex.IsPassable"/> was
        /// previously left at whatever the old sea hex happened to have, and beach status was always
        /// forced off instead of extending an existing beach strip across the repaired hex.
        /// </summary>
        private static bool AbsorbIntoLand(MapHexFile map, Hex target, Hex sourceOfTruth)
        {
            if (!target.IsSea)
            {
                return false;
            }

            bool extendRiverOnto = IsLastSeaNeighbourOfAnyRiver(map, target);

            target.IsSea           = false;
            target.GroundTypeIndex = sourceOfTruth.GroundTypeIndex;
            target.RegionId        = sourceOfTruth.RegionId;
            target.ClimateIndex    = sourceOfTruth.ClimateIndex;
            target.AttritionIndex  = sourceOfTruth.AttritionIndex;
            target.IsPassable      = sourceOfTruth.IsPassable;
            target.IsBeach         = sourceOfTruth.IsBeach;
            target.IsCliff         = false;
            target.IsBridge        = false; // no longer spans water - it's just land now

            if (extendRiverOnto)
            {
                target.IsRiver = true;
            }

            return true;
        }

        /// <summary>
        /// True if <paramref name="candidateSeaHex"/> is the last sea hex keeping some neighbouring
        /// river connected to the coast - converting it to land would (without further action) leave
        /// that river ending inland instead of at the sea.
        /// </summary>
        private static bool IsLastSeaNeighbourOfAnyRiver(MapHexFile map, Hex candidateSeaHex)
        {
            for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
            {
                int nbrIndex = map.GetNeighbourIndex(candidateSeaHex, dir);
                if (nbrIndex == -1)
                {
                    continue;
                }

                var nbr = map.HexData[nbrIndex];
                if (!nbr.IsRiver)
                {
                    continue;
                }

                int seaNeighbourCount = 0;
                for (ushort dir2 = 0; dir2 < HexGridUtility.NEIGHBOURS_COUNT; ++dir2)
                {
                    int nbrOfNbrIndex = map.GetNeighbourIndex(nbr, dir2);
                    if (nbrOfNbrIndex != -1 && map.HexData[nbrOfNbrIndex].IsSea)
                    {
                        seaNeighbourCount++;
                    }
                }

                if (seaNeighbourCount <= 1)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// A beach hex needs a sea neighbour by definition; the coastline repair above can absorb a
        /// beach hex's last sea neighbour into land, leaving a beach that no longer touches any coast.
        /// Demotes those back to plain land instead of leaving an invalid, inland "beach".
        /// </summary>
        internal static void CleanupOrphanBeaches(MapHexFile map)
        {
            for (int index = 0; index < map.Capacity; ++index)
            {
                var hex = map.HexData[index];
                if (!hex.IsBeach)
                {
                    continue;
                }

                bool hasSeaNeighbour = false;
                for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                {
                    int nbrIndex = map.GetNeighbourIndex(hex, dir);
                    if (nbrIndex != -1 && map.HexData[nbrIndex].IsSea)
                    {
                        hasSeaNeighbour = true;
                        break;
                    }
                }

                if (!hasSeaNeighbour)
                {
                    hex.IsBeach = false;
                }
            }
        }

        // ----------------------------------------------------------------- Regions

        private const int MAX_REGION_BLEED_REPAIR_PASSES  = 4;
        private const int MAX_REGION_BLEED_RIVER_DISTANCE = 2;

        /// <summary>
        /// A river is often authored as the border between two regions. The region layer is sampled
        /// independently of the river's retraced line (nearest-neighbour block-fill vs. the walked
        /// path), so the two can disagree right along the river - a strip of bank hexes assigned to the
        /// region on the *wrong* side.
        ///
        /// What disqualifies a patch is its *shape*, not its size: a bleed is by nature a thin sliver
        /// hugging the river, so a patch qualifies only if every one of its hexes sits within
        /// <see cref="MAX_REGION_BLEED_RIVER_DISTANCE"/> of a river. A one-hex-wide strip running the
        /// length of a river is exactly as much a bleed as a single stray hex is and gets corrected the
        /// same way, while any patch reaching further inland than that is a real region and is left
        /// alone the moment the flood fill touches its first inland hex. A patch containing a settlement
        /// is likewise never touched - a region is *defined* by its settlement, so that is proof the
        /// patch is genuine no matter how thin it looks.
        /// </summary>
        internal static void RepairRegionBleedNearRivers(MapHexFile map)
        {
            // Rivers don't move during this repair - only region ids do - so this is computed once.
            var nearRiver = BuildNearRiverMask(map);

            for (int pass = 0; pass < MAX_REGION_BLEED_REPAIR_PASSES; ++pass)
            {
                bool changed = false;
                var visited = new HashSet<int>();

                for (int index = 0; index < map.Capacity; ++index)
                {
                    if (visited.Contains(index) || !nearRiver[index])
                    {
                        continue; // too far inland to be the start of a river-hugging sliver
                    }

                    var hex = map.HexData[index];
                    if (hex.IsRiver || hex.IsSea || hex.RegionId == Hex.INVALID_REGION_INDEX)
                    {
                        continue; // only correct the banks, not the river hexes themselves
                    }

                    if (TryFixRegionBleedCluster(map, index, nearRiver, visited))
                    {
                        changed = true;
                    }
                }

                if (!changed)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Marks every non-river hex lying within <see cref="MAX_REGION_BLEED_RIVER_DISTANCE"/> steps of
        /// a river, via one multi-source breadth-first sweep out from every river hex at once. Lets
        /// <see cref="TryFixRegionBleedCluster"/> reject an inland patch the instant its flood fill
        /// reaches the first hex outside this band, instead of having to map the whole region first.
        /// </summary>
        private static bool[] BuildNearRiverMask(MapHexFile map)
        {
            int capacity = (int)map.Capacity;
            var depth    = new int[capacity];
            for (int i = 0; i < capacity; ++i)
            {
                depth[i] = -1;
            }

            var queue = new Queue<int>();
            for (int i = 0; i < capacity; ++i)
            {
                if (map.HexData[i].IsRiver)
                {
                    depth[i] = 0;
                    queue.Enqueue(i);
                }
            }

            // River hexes stay false: they seed the sweep but are never candidates themselves.
            var nearRiver = new bool[capacity];
            while (queue.Count > 0)
            {
                int index = queue.Dequeue();
                if (depth[index] >= MAX_REGION_BLEED_RIVER_DISTANCE)
                {
                    continue;
                }

                var hex = map.HexData[index];
                for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                {
                    int nbrIndex = map.GetNeighbourIndex(hex, dir);
                    if (nbrIndex == -1 || depth[nbrIndex] != -1)
                    {
                        continue;
                    }

                    depth[nbrIndex]     = depth[index] + 1;
                    nearRiver[nbrIndex] = true;
                    queue.Enqueue(nbrIndex);
                }
            }

            return nearRiver;
        }

        /// <summary>
        /// Flood-fills the connected same-region patch containing <paramref name="seedIndex"/>, aborting
        /// the moment it reaches a hex outside <paramref name="nearRiver"/> - that patch runs inland, so
        /// it's a real region rather than a sliver pinned against the bank. Every hex reached, including
        /// on an aborted fill, is added to <paramref name="visited"/>, so each hex is walked at most once
        /// per pass however many separate seeds the caller tries.
        /// </summary>
        private static bool TryFixRegionBleedCluster(MapHexFile map, int seedIndex, bool[] nearRiver, HashSet<int> visited)
        {
            int clusterRegion = map.HexData[seedIndex].RegionId;

            var cluster    = new List<int> { seedIndex };
            var clusterSet = new HashSet<int> { seedIndex };
            var queue      = new Queue<int>();
            queue.Enqueue(seedIndex);
            bool reachesInland = false;
            bool hasSettlement = IsSettlementHex(map.HexData[seedIndex]);

            while (queue.Count > 0 && !reachesInland)
            {
                var hex = map.HexData[queue.Dequeue()];
                for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                {
                    int nbrIndex = map.GetNeighbourIndex(hex, dir);
                    if (nbrIndex == -1 || clusterSet.Contains(nbrIndex))
                    {
                        continue;
                    }

                    var nbr = map.HexData[nbrIndex];
                    if (nbr.IsRiver || nbr.IsSea || nbr.RegionId != clusterRegion)
                    {
                        continue; // not part of this same-region patch
                    }

                    if (!nearRiver[nbrIndex])
                    {
                        reachesInland = true;
                        break;
                    }

                    if (IsSettlementHex(nbr))
                    {
                        hasSettlement = true;
                    }

                    clusterSet.Add(nbrIndex);
                    cluster.Add(nbrIndex);
                    queue.Enqueue(nbrIndex);
                }
            }

            foreach (var i in cluster)
            {
                visited.Add(i);
            }

            if (reachesInland || hasSettlement)
            {
                return false; // a real region, not a bleed - leave whatever border this really is alone
            }

            // The fill above already absorbed every same-region hex touching this patch, so the patch is
            // by construction cut off from the rest of its own region: only the *other* regions vote.
            bool touchesRiver = false;
            var regionCounts  = new Dictionary<int, int>();
            int totalVotes    = 0;
            foreach (var i in cluster)
            {
                var hex = map.HexData[i];
                for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                {
                    int nbrIndex = map.GetNeighbourIndex(hex, dir);
                    if (nbrIndex == -1 || clusterSet.Contains(nbrIndex))
                    {
                        continue; // internal to the cluster - doesn't vote
                    }

                    var nbr = map.HexData[nbrIndex];
                    if (nbr.IsRiver)
                    {
                        touchesRiver = true;
                        continue;
                    }
                    if (nbr.IsSea || nbr.RegionId == Hex.INVALID_REGION_INDEX)
                    {
                        continue;
                    }

                    regionCounts.TryGetValue(nbr.RegionId, out int count);
                    regionCounts[nbr.RegionId] = count + 1;
                    ++totalVotes;
                }
            }

            if (!touchesRiver)
            {
                return false; // only patches actually pinned against a river are in scope here
            }

            int bestRegion = clusterRegion;
            int bestCount  = 0;
            foreach (var pair in regionCounts)
            {
                if (pair.Value > bestCount)
                {
                    bestRegion = pair.Key;
                    bestCount  = pair.Value;
                }
            }

            // No absolute floor on vote count: a patch pinned against a river typically has very few
            // external neighbours to begin with (the river itself occupies at least one direction, and
            // map edges/sea often eat several more), so requiring some fixed minimum routinely starved
            // out exactly the single-hex bleeds this repair exists to fix - real data showed dozens of
            // them with only 1-3 total votes, all unanimous. An outright majority is still required (so
            // a patch genuinely between two regions, with no clear winner, is left for a human to judge)
            // - it just no longer needs a minimum number of voters behind it.
            if (bestRegion == clusterRegion || (bestCount * 2) <= totalVotes)
            {
                return false;
            }

            foreach (var i in cluster)
            {
                map.HexData[i].RegionId = bestRegion;
            }
            return true;
        }

        /// <summary>
        /// Splits a cyclic boolean sequence into its maximal runs of <c>true</c>. Used both for sea runs
        /// (a single run, or none, is an "orthodox" shoreline) and, inverted, for land runs (checking the
        /// surviving land connection is at least <see cref="MIN_LAND_RUN"/> hexes wide).
        /// </summary>
        private static List<List<int>> FindCyclicRuns(bool[] values)
        {
            int n = values.Length;
            var runs = new List<List<int>>();

            if (values.All(v => v))
            {
                runs.Add(Enumerable.Range(0, n).ToList());
                return runs;
            }

            if (values.All(v => !v))
            {
                return runs;
            }

            int start = 0;
            for (int i = 0; i < n; ++i)
            {
                if (values[i] && !values[(i - 1 + n) % n])
                {
                    start = i;
                    break;
                }
            }

            List<int> current = null;
            for (int step = 0; step < n; ++step)
            {
                int dir = (start + step) % n;
                if (values[dir])
                {
                    if (current == null)
                    {
                        current = new List<int>();
                        runs.Add(current);
                    }
                    current.Add(dir);
                }
                else
                {
                    current = null;
                }
            }

            return runs;
        }
    }
}
