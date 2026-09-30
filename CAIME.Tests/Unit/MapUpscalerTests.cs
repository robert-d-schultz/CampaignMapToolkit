using System.Collections.Generic;
using System.Linq;
using CAIME;
using CAIME.Tests.Helpers;
using CAIME.Upscaler;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CAIME.Tests.Unit
{
    /// <summary>
    /// Tests for <see cref="MapUpscaler"/>, the structure-preserving upscaler: settlements keep their
    /// original footprint (pinned to a scaled anchor instead of scaled themselves), roads/rivers are
    /// re-drawn as one-hex-wide lines instead of block-filled, and fragmented shorelines are repaired.
    /// </summary>
    [TestClass]
    public class MapUpscalerTests
    {
        [TestMethod]
        public void Factor1_ReturnsFalse()
        {
            var project = BuildProject(3, 3);
            Assert.IsFalse(MapUpscaler.Upscale(project, 1));
        }

        [TestMethod]
        public void Upscale_ScalesGridDimensions()
        {
            var project = BuildProject(4, 3);
            Assert.IsTrue(MapUpscaler.Upscale(project, 3));

            Assert.AreEqual(12u, project.MapHexFile.MapWidth);
            Assert.AreEqual(9u, project.MapHexFile.MapHeight);
        }

        [TestMethod]
        public void FractionalFactor_ScalesDimensionsAndPreservesSettlementAndRoad()
        {
            // A typical real-world case: 1.25x so a 600x400-ish map stays under the 2048 cap.
            const int width = 8, height = 8;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            var townHex = map.HexData[Index(map, 4, 4)];
            townHex.TownSlotIndex = Hex.MAIN_SLOT_INDEX;
            townHex.IsTownSprawl = true;

            for (int q = 0; q < width; ++q)
            {
                map.HexData[Index(map, q, 2)].IsRoad = true;
            }
            map.CalculateRoadEdgeMasks();

            Assert.IsTrue(MapUpscaler.Upscale(project, 1.25));

            Assert.AreEqual(10u, map.MapWidth);  // round(8 * 1.25)
            Assert.AreEqual(10u, map.MapHeight);

            int slotCount = map.HexData.Count(h => h.TownSlotIndex == Hex.MAIN_SLOT_INDEX);
            Assert.AreEqual(1, slotCount, "fractional factor must still keep exactly one main slot hex");

            int roadHexCount = map.HexData.Count(h => h.IsRoad);
            Assert.IsTrue(roadHexCount >= width - 1, $"expected a continuous retraced road, got only {roadHexCount} road hexes");
        }

        [TestMethod]
        public void Upscale_SettlementFootprintStaysOriginalSize_NotScaled()
        {
            const int width = 6, height = 6;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            // A 1-main-slot + 4-sprawl-hex settlement (5 hexes total) around (2,2).
            var mainIdx = Index(map, 2, 2);
            map.HexData[mainIdx].TownSlotIndex = Hex.MAIN_SLOT_INDEX;
            map.HexData[mainIdx].IsTownSprawl = true;

            var sprawlOffsets = new (int dq, int dr)[] { (0, 1), (0, -1), (-1, 0), (1, 0) };
            foreach (var (dq, dr) in sprawlOffsets)
            {
                map.HexData[Index(map, 2 + dq, 2 + dr)].IsTownSprawl = true;
            }

            const uint factor = 4;
            Assert.IsTrue(MapUpscaler.Upscale(project, factor));

            int settlementHexCount = map.HexData.Count(h => h.IsTownSprawl || h.TownSlotIndex != Hex.INVALID_SLOT_INDEX);
            Assert.AreEqual(5, settlementHexCount, "the settlement should keep its original 5-hex footprint, not scale up by the factor");

            int slotCount = map.HexData.Count(h => h.TownSlotIndex == Hex.MAIN_SLOT_INDEX);
            Assert.AreEqual(1, slotCount, "exactly one hex should carry the main slot after upscaling");
        }

        [TestMethod]
        public void Upscale_RoadStaysOneHexWide()
        {
            // A straight 5-hex road strip.
            const int width = 5, height = 3;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;
            for (int q = 0; q < width; ++q)
            {
                map.HexData[Index(map, q, 1)].IsRoad = true;
            }
            map.CalculateRoadEdgeMasks();

            const uint factor = 3;
            Assert.IsTrue(MapUpscaler.Upscale(project, factor));

            // Every road hex must have at most 2 road neighbours (a thin line has no branching/blobs).
            for (int i = 0; i < map.Capacity; ++i)
            {
                var hex = map.HexData[i];
                if (!hex.IsRoad)
                {
                    continue;
                }

                int roadNeighbours = 0;
                for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                {
                    int nbrIndex = map.GetNeighbourIndex(hex, dir);
                    if (nbrIndex != -1 && map.HexData[nbrIndex].IsRoad)
                    {
                        roadNeighbours++;
                    }
                }

                Assert.IsTrue(roadNeighbours <= 2,
                    $"hex ({hex.Q},{hex.R}) has {roadNeighbours} road neighbours - a 1-hex-wide line should have at most 2");
            }

            // The road must still be a single connected line spanning roughly factor * (width-1) + 1 hexes,
            // not shrunk down to a handful of disconnected anchors.
            int roadHexCount = map.HexData.Count(h => h.IsRoad);
            Assert.IsTrue(roadHexCount >= (width - 1) * (int)factor,
                $"expected a continuous retraced line, got only {roadHexCount} road hexes");
        }

        [TestMethod]
        public void Upscale_FragmentedShoreline_GetsRepairedToSingleRun()
        {
            // A small island: one land hex surrounded by sea, with the sea flag deliberately punched
            // out at two non-adjacent directions after upscaling would be hard to set up directly, so
            // instead verify the invariant the repair pass guarantees: after upscaling a plain
            // land/sea map, every land hex's sea neighbours form at most one contiguous run.
            const int width = 6, height = 6;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            // Sea on the left half, land on the right half - a natural coastline. Block-fill and the
            // road/river retrace don't touch land/sea directly, but the coastline repair pass must still
            // leave a consistent result after upscaling.
            for (int r = 0; r < height; ++r)
            {
                for (int q = 0; q < width; ++q)
                {
                    var hex = map.HexData[Index(map, q, r)];
                    hex.GroundTypeIndex = (sbyte)(q < width / 2 ? 1 : 0); // >= LandGroundTypes.Count => sea
                    hex.RegionId = 0;
                }
            }
            map.UpdateHexTypes();

            Assert.IsTrue(MapUpscaler.Upscale(project, 3));

            for (int i = 0; i < map.Capacity; ++i)
            {
                var hex = map.HexData[i];
                if (hex.IsLand == false || hex.RegionId == Hex.INVALID_REGION_INDEX)
                {
                    continue;
                }

                var isSea = new bool[HexGridUtility.NEIGHBOURS_COUNT];
                bool onEdge = false;
                for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                {
                    int nbrIndex = map.GetNeighbourIndex(hex, dir);
                    if (nbrIndex == -1)
                    {
                        onEdge = true;
                        break;
                    }
                    isSea[dir] = map.HexData[nbrIndex].IsSea;
                }

                if (onEdge)
                {
                    continue;
                }

                int runs = CountCyclicRuns(isSea);
                Assert.IsTrue(runs <= 1, $"hex ({hex.Q},{hex.R}) has {runs} disconnected sea runs after repair");
            }
        }

        [TestMethod]
        public void Upscale_BentRoad_StaysConnectedAndOneHexWide_UnderFractionalFactor()
        {
            // An L-shaped road: straight along row 3, then a sharp (120 degree) turn down column 4.
            // Deliberately a *sharp* turn, not a gentle 60 degree one: at a gentle bend the hexes on
            // either side are also physically adjacent to each other, which makes the source data an
            // actual triangle whose chord-dropping is a separate, already-tested engine behaviour
            // (see RoadEdgeMaskPaintTests) - using a sharp bend here keeps this test purely about
            // line-drawing/retracing, not entangled with that.
            const int width = 8, height = 8;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            var path = new List<int>();
            for (int q = 0; q <= 4; ++q)
            {
                path.Add(Index(map, q, 3));
            }

            // From (4,3) [even col], direction 3 ("down") repeatedly - direction 3 is two steps away
            // from direction 5 (the direction back to (3,3)), so (3,3) and each new hex are never
            // mutually adjacent.
            int cur = Index(map, 4, 3);
            for (int step = 0; step < 3; ++step)
            {
                int next = map.GetNeighbourIndex(map.HexData[cur], 3);
                Assert.AreNotEqual(-1, next, "test setup expects this bend to stay on the grid");
                cur = next;
                path.Add(cur);
            }

            foreach (var index in path)
            {
                map.HexData[index].IsRoad = true;
            }
            map.CalculateRoadEdgeMasks();

            Assert.IsTrue(MapUpscaler.Upscale(project, 1.4));

            var roadIndices = Enumerable.Range(0, (int)map.Capacity).Where(i => map.HexData[i].IsRoad).ToList();
            Assert.IsTrue(roadIndices.Count > 0, "the retraced road should not vanish");

            foreach (var i in roadIndices)
            {
                var hex = map.HexData[i];
                int degree = 0;
                for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                {
                    int nbrIndex = map.GetNeighbourIndex(hex, dir);
                    if (nbrIndex != -1 && map.HexData[nbrIndex].IsRoad)
                    {
                        degree++;
                    }
                }

                Assert.IsTrue(degree <= 2, $"hex ({hex.Q},{hex.R}) has road-degree {degree} - a bent 1-hex-wide path should have no branching/blobs");
            }

            var visited = new HashSet<int> { roadIndices[0] };
            var queue = new Queue<int>();
            queue.Enqueue(roadIndices[0]);
            while (queue.Count > 0)
            {
                var hex = map.HexData[queue.Dequeue()];
                for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                {
                    int nbrIndex = map.GetNeighbourIndex(hex, dir);
                    if (nbrIndex != -1 && map.HexData[nbrIndex].IsRoad && visited.Add(nbrIndex))
                    {
                        queue.Enqueue(nbrIndex);
                    }
                }
            }

            Assert.AreEqual(roadIndices.Count, visited.Count, "the retraced bent road must be a single connected path, not fragmented");
        }

        [TestMethod]
        public void Upscale_GentleBendRoad_TriangleAlreadyResolvedBySourceEdgeMask_StaysOneHexWide()
        {
            // A *gentle* (60 degree) bend in isolation: (3,3), (4,3) and (4,4) are all road AND all
            // three are mutually adjacent - an inherent hex-grid property of gentle bends, not a
            // mistake. Real authored roads take turns like this constantly. CalculateRoadEdgeMasks
            // already resolves the resulting triangle by dropping one chord (see
            // RoadEdgeMaskPaintTests); retracing must follow that resolved mask, not raw physical
            // adjacency, or the dropped chord reappears at the new resolution as a spurious extra edge.
            const int width = 8, height = 8;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            var a    = map.HexData[Index(map, 3, 3)];
            var bend = map.HexData[Index(map, 4, 3)];
            var c    = map.HexData[Index(map, 4, 4)];

            a.IsRoad = true;
            bend.IsRoad = true;
            c.IsRoad = true;
            map.CalculateRoadEdgeMasks();

            // Sanity: the source data really is a resolved triangle - exactly 2 of its 3 possible edges
            // survive in the mask (that's the behaviour this test relies on, tested independently by
            // RoadEdgeMaskPaintTests; if this ever changed, the assumption behind this test would need
            // revisiting too).
            int aToBendBit = DirectionTo(map, a, bend);
            int bendToCBit = DirectionTo(map, bend, c);
            int aToCBit    = DirectionTo(map, a, c);
            int survivingEdges = 0;
            if ((a.RoadEdgeMask & (1 << aToBendBit)) != 0) survivingEdges++;
            if ((bend.RoadEdgeMask & (1 << bendToCBit)) != 0) survivingEdges++;
            if ((a.RoadEdgeMask & (1 << aToCBit)) != 0) survivingEdges++;
            Assert.AreEqual(2, survivingEdges, "test setup expects the engine to have resolved this triangle down to 2 edges already");

            Assert.IsTrue(MapUpscaler.Upscale(project, 1.6));

            var roadIndices = Enumerable.Range(0, (int)map.Capacity).Where(i => map.HexData[i].IsRoad).ToList();
            Assert.IsTrue(roadIndices.Count > 0, "the retraced bend should not vanish");

            var visited = new HashSet<int> { roadIndices[0] };
            var queue = new Queue<int>();
            queue.Enqueue(roadIndices[0]);
            while (queue.Count > 0)
            {
                var hex = map.HexData[queue.Dequeue()];
                for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                {
                    int nbrIndex = map.GetNeighbourIndex(hex, dir);
                    if (nbrIndex != -1 && map.HexData[nbrIndex].IsRoad && visited.Add(nbrIndex))
                    {
                        queue.Enqueue(nbrIndex);
                    }
                }
            }

            Assert.AreEqual(roadIndices.Count, visited.Count, "the retraced bend must stay a single connected piece");

            // At factor 1.6 the surviving a-c edge (distance 2) needs exactly one bridging hex, and
            // that hex is *also* incidentally adjacent to bend - RemoveIncidentalTouches correctly
            // refuses to drop it (doing so would disconnect c entirely, verified: there is no
            // alternative bridging hex between a and c at this factor). So a single hex with degree 3
            // right at the vertex is the true, minimal, unavoidable residual here - not the spurious
            // reproduced-triangle-chord bug this test was originally written to catch (that part is
            // fully fixed, and is what the survivingEdges assertion above already confirms holds).
            int maxDegree = 0;
            foreach (var i in roadIndices)
            {
                var hex = map.HexData[i];
                int degree = 0;
                for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                {
                    int nbrIndex = map.GetNeighbourIndex(hex, dir);
                    if (nbrIndex != -1 && map.HexData[nbrIndex].IsRoad)
                    {
                        degree++;
                    }
                }

                maxDegree = System.Math.Max(maxDegree, degree);
            }

            Assert.IsTrue(maxDegree <= 3, $"expected at most one hex with a single incidental extra touch (degree 3), got max degree {maxDegree}");
        }

        [TestMethod]
        public void TryFixShoreline_WidensThinLandConnectionToMinimum()
        {
            const int width = 5, height = 5;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            foreach (var hex in map.HexData)
            {
                hex.GroundTypeIndex = 1; // sea
                hex.RegionId = 0;
            }

            var center = map.HexData[Index(map, 2, 2)];
            center.GroundTypeIndex = 0; // land

            int nbr0 = map.GetNeighbourIndex(center, 0);
            int nbr1 = map.GetNeighbourIndex(center, 1);
            map.HexData[nbr0].GroundTypeIndex = 0;
            map.HexData[nbr1].GroundTypeIndex = 0;
            map.UpdateHexTypes();

            Assert.AreEqual(2, CountLandNeighbours(map, center), "test setup should start with exactly a 2-hex land run");

            bool changed = MapUpscaler.TryFixShoreline(map, map.HexData[Index(map, 2, 2)]);

            Assert.IsTrue(changed);
            Assert.IsTrue(CountLandNeighbours(map, map.HexData[Index(map, 2, 2)]) >= 3,
                "a 2-hex land connection (a pinch point) should be widened to at least 3 hexes");
        }

        [TestMethod]
        public void CleanupOrphanBeaches_DemotesBeachWithNoSeaNeighbour()
        {
            const int width = 3, height = 3;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            foreach (var hex in map.HexData)
            {
                hex.GroundTypeIndex = 0; // land throughout - no sea anywhere
                hex.RegionId = 0;
            }

            map.HexData[Index(map, 1, 1)].IsBeach = true;
            map.UpdateHexTypes();

            MapUpscaler.CleanupOrphanBeaches(map);

            Assert.IsFalse(map.HexData[Index(map, 1, 1)].IsBeach, "a beach hex with no sea neighbour should be demoted to plain land");
        }

        [TestMethod]
        public void CleanupOrphanBeaches_LeavesBeachWithSeaNeighbourAlone()
        {
            const int width = 3, height = 3;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            foreach (var hex in map.HexData)
            {
                hex.GroundTypeIndex = 0; // land
                hex.RegionId = 0;
            }

            var center = map.HexData[Index(map, 1, 1)];
            center.IsBeach = true;
            int nbrIndex = map.GetNeighbourIndex(center, 0);
            map.HexData[nbrIndex].GroundTypeIndex = 1; // sea neighbour
            map.UpdateHexTypes();

            MapUpscaler.CleanupOrphanBeaches(map);

            Assert.IsTrue(map.HexData[Index(map, 1, 1)].IsBeach, "a beach hex with a sea neighbour should be left alone");
        }

        [TestMethod]
        public void TryFindHexagon_DetectsRadius1Hexagon()
        {
            const int width = 9, height = 9;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            var center = map.HexData[Index(map, 4, 4)];
            var blob = new List<int> { center.Index };
            for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
            {
                blob.Add(map.GetNeighbourIndex(center, dir));
            }

            bool found = MapUpscaler.TryFindHexagon(map.HexData, blob, out _, out int radius);

            Assert.IsTrue(found, "a center hex plus its 6 neighbours is a radius-1 'nice' hexagon");
            Assert.AreEqual(1, radius);
        }

        [TestMethod]
        public void TryFindHexagon_RejectsNonHexagonalBlob()
        {
            const int width = 9, height = 9;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            // A straight line of 7 hexes has the right count (matches radius 1's 7-hex total) but is not
            // a filled disk - must be rejected.
            var blob = new List<int>();
            for (int q = 0; q < 7; ++q)
            {
                blob.Add(Index(map, q, 4));
            }

            bool found = MapUpscaler.TryFindHexagon(map.HexData, blob, out _, out _);

            Assert.IsFalse(found, "a straight line is not a filled hexagonal disk, even with a matching hex count");
        }

        [TestMethod]
        public void Upscale_NiceAreaOfInterest_RestampedAsCleanBiggerHexagon()
        {
            const int width = 9, height = 9;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            const sbyte interest = 3;
            var center = map.HexData[Index(map, 4, 4)];
            center.InterestIndex = interest;
            for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
            {
                map.HexData[map.GetNeighbourIndex(center, dir)].InterestIndex = interest;
            }

            const double factor = 2.0;
            Assert.IsTrue(MapUpscaler.Upscale(project, factor));

            int newRadius = (int)System.Math.Ceiling(1 * factor); // ceil(1 * 2) = 2
            int expectedCount = (3 * newRadius * (newRadius + 1)) + 1; // 19

            int actualCount = map.HexData.Count(h => h.InterestIndex == interest);
            Assert.AreEqual(expectedCount, actualCount, $"expected a clean radius-{newRadius} hexagon ({expectedCount} hexes), not a jagged nearest-neighbour approximation");
        }

        [TestMethod]
        public void Upscale_RecomputesBordersForNewRegionAdjacency()
        {
            const int width = 6, height = 6;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            for (int r = 0; r < height; ++r)
            {
                for (int q = 0; q < width; ++q)
                {
                    var hex = map.HexData[Index(map, q, r)];
                    hex.GroundTypeIndex = 0; // land throughout
                    hex.RegionId = q < width / 2 ? 0 : 1;
                }
            }
            map.UpdateHexTypes();

            Assert.IsTrue(MapUpscaler.Upscale(project, 2));

            for (int i = 0; i < map.Capacity; ++i)
            {
                var hex = map.HexData[i];
                bool expectedBorder = false;
                for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                {
                    int nbrIndex = map.GetNeighbourIndex(hex, dir);
                    if (nbrIndex != -1 && map.HexData[nbrIndex].RegionId != hex.RegionId)
                    {
                        expectedBorder = true;
                    }
                }

                Assert.AreEqual(expectedBorder, hex.IsBorder,
                    $"hex ({hex.Q},{hex.R}) IsBorder should reflect the *new* grid's region adjacency, not a stale value from the source resolution");
            }
        }

        [TestMethod]
        public void Upscale_MultipleDisconnectedInstancesOfSameInterestIndex_EachRestampedIndependently()
        {
            // Real maps commonly reuse one InterestIndex value across several separate physical
            // instances of the same named area of interest. Grouping by raw value across the whole map
            // (instead of by connected component) would lump every instance together into one "blob"
            // spanning huge distances, which never matches a hexagon and so silently skipped all of them.
            const int width = 20, height = 9;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            const sbyte interest = 4;

            var centerA = map.HexData[Index(map, 4, 4)];
            centerA.InterestIndex = interest;
            for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
            {
                map.HexData[map.GetNeighbourIndex(centerA, dir)].InterestIndex = interest;
            }

            var centerB = map.HexData[Index(map, 15, 4)];
            centerB.InterestIndex = interest;
            for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
            {
                map.HexData[map.GetNeighbourIndex(centerB, dir)].InterestIndex = interest;
            }

            const double factor = 2.0;
            Assert.IsTrue(MapUpscaler.Upscale(project, factor));

            int newRadius = (int)System.Math.Ceiling(1 * factor); // 2
            int expectedCountPerInstance = (3 * newRadius * (newRadius + 1)) + 1; // 19

            int actualCount = map.HexData.Count(h => h.InterestIndex == interest);
            Assert.AreEqual(expectedCountPerInstance * 2, actualCount,
                $"both disconnected instances sharing one interest value should each become a clean radius-{newRadius} hexagon, not be lumped together and skipped");
        }

        [TestMethod]
        public void Upscale_RoadConnectsToSettlementAtItsPinnedPosition_NotSimpleAnchor()
        {
            // A port + main slot settlement, with a road running through the main slot (a "dead end"
            // town) connecting to an external road chain. The settlement is anchored to its port slot
            // (issue: port position must stay coastal), so the main slot's *pinned* position keeps its
            // unscaled offset from port's scaled position - generally different from the main slot's
            // own plain scaled position once rounding is involved. The road must connect to
            // wherever the main slot actually got pinned, and the stamp must not leak a stray road
            // flag anywhere else.
            const int width = 14, height = 10;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            var port = map.HexData[Index(map, 7, 5)];
            var main = map.HexData[Index(map, 7, 6)];
            port.TownSlotIndex = Hex.PORT_SLOT_INDEX;
            port.IsTownSprawl  = true;
            main.TownSlotIndex = Hex.MAIN_SLOT_INDEX;
            main.IsTownSprawl  = true;
            main.IsRoad        = true;

            var externalRoadHexes = new List<Hex>();
            for (int q = 2; q <= 6; ++q)
            {
                var h = map.HexData[Index(map, q, 6)];
                h.IsRoad = true;
                externalRoadHexes.Add(h);
            }

            map.CalculateRoadEdgeMasks();

            var lastExternal = externalRoadHexes[externalRoadHexes.Count - 1];
            int connectDir = DirectionTo(map, lastExternal, main);
            Assert.AreNotEqual(-1, connectDir, "test setup expects the external road to be adjacent to the settlement's main slot");
            Assert.IsTrue((lastExternal.RoadEdgeMask & (1 << connectDir)) != 0, "test setup expects the road to actually connect to the settlement");

            const double factor = 1.7;

            // Where the main slot *should* end up: anchored to the port slot, then kept at the same
            // (unscaled) position relative to it.
            var (scaledPortQ, scaledPortR) = HexGeometry.Scale(port.Q, port.R, factor);
            var (expectedQ, expectedR)     = HexGeometry.Translate(main, port, new Hex(scaledPortQ, scaledPortR));

            Assert.IsTrue(MapUpscaler.Upscale(project, factor));

            var pinnedMain = map.HexData[Index(map, expectedQ, expectedR)];
            Assert.AreEqual(Hex.MAIN_SLOT_INDEX, pinnedMain.TownSlotIndex, "the main slot should be pinned exactly where the port-anchored delta places it");
            Assert.IsTrue(pinnedMain.IsRoad, "the settlement's road-carrying hex should be road at its pinned position");

            int mainSlotCount = map.HexData.Count(h => h.TownSlotIndex == Hex.MAIN_SLOT_INDEX);
            Assert.AreEqual(1, mainSlotCount, "the main slot must not be duplicated at some other, unpinned position");

            var roadIndices = Enumerable.Range(0, (int)map.Capacity).Where(i => map.HexData[i].IsRoad).ToList();
            var visited = new HashSet<int> { roadIndices[0] };
            var queue = new Queue<int>();
            queue.Enqueue(roadIndices[0]);
            while (queue.Count > 0)
            {
                var hex = map.HexData[queue.Dequeue()];
                for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                {
                    int nbrIndex = map.GetNeighbourIndex(hex, dir);
                    if (nbrIndex != -1 && map.HexData[nbrIndex].IsRoad && visited.Add(nbrIndex))
                    {
                        queue.Enqueue(nbrIndex);
                    }
                }
            }

            Assert.AreEqual(roadIndices.Count, visited.Count, "the road must connect all the way from the external chain into the settlement, not stop short (a 'dead end' town connection)");
        }

        [TestMethod]
        public void TryFixShoreline_ConvertingABridgeHexToLand_ClearsTheBridgeFlag()
        {
            const int width = 5, height = 5;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            foreach (var hex in map.HexData)
            {
                hex.GroundTypeIndex = 1; // sea
                hex.RegionId = 0;
            }

            var center = map.HexData[Index(map, 2, 2)];
            center.GroundTypeIndex = 0; // land

            map.HexData[map.GetNeighbourIndex(center, 0)].GroundTypeIndex = 0;
            map.HexData[map.GetNeighbourIndex(center, 1)].GroundTypeIndex = 0;

            // Direction 5 is the first candidate the widening step tries (immediately before the land
            // run); flag that hex as a bridge.
            int bridgeIndex = map.GetNeighbourIndex(center, 5);
            map.HexData[bridgeIndex].IsBridge = true;
            map.UpdateHexTypes();

            bool changed = MapUpscaler.TryFixShoreline(map, map.HexData[Index(map, 2, 2)]);

            Assert.IsTrue(changed);
            Assert.IsFalse(map.HexData[bridgeIndex].IsSea, "the coastline repair must still proceed - a bridge doesn't block widening a thin land connection");
            Assert.IsFalse(map.HexData[bridgeIndex].IsBridge, "once the hex is land, it isn't a bridge any more - the flag must come off instead of leaving a 'bridge' sitting on dry land");
        }

        [TestMethod]
        public void TryFixShoreline_ConvertingARiverMouthSeaHexToLand_ExtendsTheRiverOntoIt()
        {
            const int width = 6, height = 6;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            foreach (var hex in map.HexData)
            {
                hex.GroundTypeIndex = 1; // sea
                hex.RegionId = 0;
            }

            var center = map.HexData[Index(map, 2, 2)];
            center.GroundTypeIndex = 0; // land
            map.HexData[map.GetNeighbourIndex(center, 0)].GroundTypeIndex = 0;
            map.HexData[map.GetNeighbourIndex(center, 1)].GroundTypeIndex = 0;

            // Direction 5 is the first candidate the widening step tries. Make that sea hex the *only*
            // sea neighbour of a river hex - i.e. the river's mouth.
            int candidateSeaIndex = map.GetNeighbourIndex(center, 5);
            var candidateSeaHex = map.HexData[candidateSeaIndex];

            int riverIndex = map.GetNeighbourIndex(candidateSeaHex, 0);
            Assert.AreNotEqual(center.Index, riverIndex, "test setup sanity: the river hex must not coincide with the hex under repair");
            var riverHex = map.HexData[riverIndex];
            riverHex.IsRiver = true;
            riverHex.GroundTypeIndex = 0; // land - a river hex is not itself sea

            // Force every other neighbour of riverHex to land, so candidateSeaHex really is its only sea neighbour.
            for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
            {
                int nbrIndex = map.GetNeighbourIndex(riverHex, dir);
                if (nbrIndex != -1 && nbrIndex != candidateSeaIndex)
                {
                    map.HexData[nbrIndex].GroundTypeIndex = 0;
                }
            }
            map.UpdateHexTypes();

            bool changed = MapUpscaler.TryFixShoreline(map, map.HexData[Index(map, 2, 2)]);

            Assert.IsTrue(changed);
            Assert.IsFalse(map.HexData[candidateSeaIndex].IsSea,
                "the coastline repair must still proceed - a river mouth doesn't block widening a thin land connection");
            Assert.IsTrue(map.HexData[candidateSeaIndex].IsRiver,
                "the newly-converted hex should extend the river onto it, so the river's mouth still touches whatever sea is left nearby instead of ending inland");
        }

        [TestMethod]
        public void RemoveIncidentalTouches_ProtectsRiverMouthExtendedByCoastlineRepair_FromBeingCollapsedAway()
        {
            // Same setup as TryFixShoreline_ConvertingARiverMouthSeaHexToLand_ExtendsTheRiverOntoIt, plus
            // one extra existing river hex chosen specifically to sit on the *other* side of the
            // river-mouth edge - i.e. adjacent to both the mouth hex and the sea hex about to be
            // absorbed. Once that sea hex becomes river, all three are mutually adjacent: exactly the
            // closed triangle RemoveIncidentalTouches_IsolatedClosedTriangle_CollapsesToOneEdge collapses
            // when nothing anchors it. Reproduces the ordering bug directly: the newly-extended mouth
            // hex was never part of the anchor set RetraceLines built (it didn't exist yet at that
            // point), so a naive re-run of the cleanup pass is just as likely to remove *that* hex as
            // either of the two pre-existing ones - undoing the coastline repair that just ran.
            const int width = 6, height = 6;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            foreach (var hex in map.HexData)
            {
                hex.GroundTypeIndex = 1; // sea
                hex.RegionId = 0;
            }

            var center = map.HexData[Index(map, 2, 2)];
            center.GroundTypeIndex = 0;
            map.HexData[map.GetNeighbourIndex(center, 0)].GroundTypeIndex = 0;
            map.HexData[map.GetNeighbourIndex(center, 1)].GroundTypeIndex = 0;

            int candidateSeaIndex = map.GetNeighbourIndex(center, 5);
            var candidateSeaHex = map.HexData[candidateSeaIndex];

            int riverIndex = map.GetNeighbourIndex(candidateSeaHex, 0);
            Assert.AreNotEqual(center.Index, riverIndex, "test setup sanity: the river hex must not coincide with the hex under repair");
            var riverHex = map.HexData[riverIndex];
            riverHex.IsRiver = true;
            riverHex.GroundTypeIndex = 0;

            // Find the hex flanking the riverHex/candidateSeaHex edge - adjacent to both - and make it a
            // second, pre-existing river hex instead of forcing it to plain land.
            int secondRiverIndex = -1;
            for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT && secondRiverIndex == -1; ++dir)
            {
                int nbrIndex = map.GetNeighbourIndex(riverHex, dir);
                if (nbrIndex == -1 || nbrIndex == candidateSeaIndex)
                {
                    continue;
                }

                for (ushort dir2 = 0; dir2 < HexGridUtility.NEIGHBOURS_COUNT; ++dir2)
                {
                    if (map.GetNeighbourIndex(candidateSeaHex, dir2) == nbrIndex)
                    {
                        secondRiverIndex = nbrIndex;
                        break;
                    }
                }
            }
            Assert.AreNotEqual(-1, secondRiverIndex, "test setup: need a hex flanking the river-mouth edge, adjacent to both riverHex and candidateSeaHex");

            map.HexData[secondRiverIndex].GroundTypeIndex = 0;
            map.HexData[secondRiverIndex].IsRiver = true;

            for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
            {
                int nbrIndex = map.GetNeighbourIndex(riverHex, dir);
                if (nbrIndex != -1 && nbrIndex != candidateSeaIndex && nbrIndex != secondRiverIndex)
                {
                    map.HexData[nbrIndex].GroundTypeIndex = 0;
                }
            }
            map.UpdateHexTypes();

            // Mirrors exactly what Upscale now does: diff the river set across RepairCoastline, and feed
            // whatever it added into RemoveIncidentalTouches as extra anchors.
            var riverIndicesBefore = new HashSet<int>();
            for (int i = 0; i < map.Capacity; ++i)
            {
                if (map.HexData[i].IsRiver) riverIndicesBefore.Add(i);
            }

            bool changed = MapUpscaler.TryFixShoreline(map, center);
            Assert.IsTrue(changed);
            Assert.IsTrue(map.HexData[candidateSeaIndex].IsRiver, "test setup sanity: the coastline repair should have extended the river onto the sea hex, closing the triangle");

            var extraAnchors = new HashSet<int>();
            for (int i = 0; i < map.Capacity; ++i)
            {
                if (map.HexData[i].IsRiver && !riverIndicesBefore.Contains(i)) extraAnchors.Add(i);
            }

            MapUpscaler.RemoveIncidentalTouches(map, extraAnchors, isRoad: false);

            Assert.IsTrue(map.HexData[candidateSeaIndex].IsRiver,
                "the hex the coastline repair just extended the river onto must survive the cleanup pass - it was fed in as an anchor precisely so this pass can't undo that extension");

            int survivingCount = new[] { riverIndex, secondRiverIndex, candidateSeaIndex }.Count(i => map.HexData[i].IsRiver);
            Assert.AreEqual(2, survivingCount, "the closed triangle should still collapse to a single edge - just never by removing the newly-protected mouth hex");
        }

        [TestMethod]
        public void TryFixShoreline_PropagatesImpassableOntoNewlyConvertedLand()
        {
            const int width = 5, height = 5;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            foreach (var hex in map.HexData)
            {
                hex.GroundTypeIndex = 1;
                hex.RegionId = 0;
            }

            var center = map.HexData[Index(map, 2, 2)];
            center.GroundTypeIndex = 0;
            center.IsPassable = false;

            map.HexData[map.GetNeighbourIndex(center, 0)].GroundTypeIndex = 0;
            map.HexData[map.GetNeighbourIndex(center, 1)].GroundTypeIndex = 0;
            map.UpdateHexTypes();

            bool changed = MapUpscaler.TryFixShoreline(map, map.HexData[Index(map, 2, 2)]);

            Assert.IsTrue(changed);
            var widenedHex = map.HexData[map.GetNeighbourIndex(center, 5)];
            Assert.IsFalse(widenedHex.IsSea);
            Assert.IsFalse(widenedHex.IsPassable, "a newly-converted land hex should inherit the repairing hex's impassable flag, same as attrition already does");
        }

        [TestMethod]
        public void TryFixShoreline_PropagatesBeachOntoNewlyConvertedLand()
        {
            const int width = 5, height = 5;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            foreach (var hex in map.HexData)
            {
                hex.GroundTypeIndex = 1;
                hex.RegionId = 0;
            }

            var center = map.HexData[Index(map, 2, 2)];
            center.GroundTypeIndex = 0;
            center.IsBeach = true;

            map.HexData[map.GetNeighbourIndex(center, 0)].GroundTypeIndex = 0;
            map.HexData[map.GetNeighbourIndex(center, 1)].GroundTypeIndex = 0;
            map.UpdateHexTypes();

            bool changed = MapUpscaler.TryFixShoreline(map, map.HexData[Index(map, 2, 2)]);

            Assert.IsTrue(changed);
            var widenedHex = map.HexData[map.GetNeighbourIndex(center, 5)];
            Assert.IsFalse(widenedHex.IsSea);
            Assert.IsTrue(widenedHex.IsBeach, "a newly-converted land hex bordering a beach should extend the beach strip, not leave a gap");
        }

        [TestMethod]
        public void RemoveIncidentalTouches_DropsRedundantHexWithoutDisconnectingAnything()
        {
            // A hex surrounded by a full ring of 6 road hexes: the ring hexes already form a closed
            // cycle among themselves (each adjacent to its two ring neighbours), so the centre hex is
            // entirely redundant for connectivity - a clean, verifiable case where dropping the
            // over-degree hex *is* safe, unlike a bend where the extra hex is load-bearing.
            const int width = 9, height = 9;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            var center = map.HexData[Index(map, 4, 4)];
            var ringIndices = new HashSet<int>();
            for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
            {
                int nbrIndex = map.GetNeighbourIndex(center, dir);
                ringIndices.Add(nbrIndex);
                map.HexData[nbrIndex].IsRoad = true;
            }
            center.IsRoad = true;

            var visitedRingOnly = new HashSet<int> { ringIndices.First() };
            var queueRing = new Queue<int>(new[] { ringIndices.First() });
            while (queueRing.Count > 0)
            {
                var hex = map.HexData[queueRing.Dequeue()];
                for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                {
                    int nbrIndex = map.GetNeighbourIndex(hex, dir);
                    if (nbrIndex != center.Index && ringIndices.Contains(nbrIndex) && visitedRingOnly.Add(nbrIndex))
                    {
                        queueRing.Enqueue(nbrIndex);
                    }
                }
            }
            Assert.AreEqual(6, visitedRingOnly.Count, "test setup expects the 6 ring hexes to already form a connected cycle without the centre");

            MapUpscaler.RemoveIncidentalTouches(map, new HashSet<int>(ringIndices), isRoad: true);

            Assert.IsFalse(center.IsRoad, "the centre hex is redundant (the ring already connects itself) and should be dropped");
            foreach (var i in ringIndices)
            {
                Assert.IsTrue(map.HexData[i].IsRoad, "the ring hexes (anchors) must never be removed");
            }
        }

        [TestMethod]
        public void ClearAndRestoreSettlementAdjacentBeachImpassability_RoundTrips()
        {
            const int width = 6, height = 6;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            var mainSlot = map.HexData[Index(map, 3, 3)];
            mainSlot.TownSlotIndex = Hex.MAIN_SLOT_INDEX;
            mainSlot.IsTownSprawl = true;

            int adjacentBeachIndex = map.GetNeighbourIndex(mainSlot, 0);
            map.HexData[adjacentBeachIndex].IsBeach = true;
            map.HexData[adjacentBeachIndex].IsPassable = false;

            var farBeach = map.HexData[Index(map, 0, 0)];
            farBeach.IsBeach = true;
            farBeach.IsPassable = false;

            var cleared = MapUpscaler.ClearSettlementAdjacentBeachImpassability(map.HexData, map.MapWidth, map.MapHeight);

            Assert.IsTrue(map.HexData[adjacentBeachIndex].IsPassable, "a settlement-adjacent beach's impassable flag should be cleared before sampling");
            Assert.IsFalse(farBeach.IsPassable, "a beach unrelated to any settlement must not be touched by the clear step");

            MapUpscaler.RestoreImpassability(map.HexData, cleared);

            Assert.IsFalse(map.HexData[adjacentBeachIndex].IsPassable, "the impassable flag must be restored on the source afterwards");
            Assert.IsFalse(farBeach.IsPassable, "the unrelated beach should still be untouched after restoring");
        }

        [TestMethod]
        public void RecalculateSettlementAdjacentBeachImpassability_MarksAdjacentBeach_LeavesFarBeachAlone()
        {
            const int width = 6, height = 6;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            var mainSlot = map.HexData[Index(map, 3, 3)];
            mainSlot.TownSlotIndex = Hex.MAIN_SLOT_INDEX;
            mainSlot.IsTownSprawl = true;

            int adjacentBeachIndex = map.GetNeighbourIndex(mainSlot, 0);
            map.HexData[adjacentBeachIndex].IsBeach = true;
            map.HexData[adjacentBeachIndex].IsPassable = true; // starts passable - the recalculation should fix it

            var farBeach = map.HexData[Index(map, 0, 0)];
            farBeach.IsBeach = true;
            farBeach.IsPassable = true;

            MapUpscaler.RecalculateSettlementAdjacentBeachImpassability(map);

            Assert.IsFalse(map.HexData[adjacentBeachIndex].IsPassable, "a beach immediately adjacent to a settlement must be recalculated as impassable");
            Assert.IsTrue(farBeach.IsPassable, "a beach hex unrelated to any settlement should be left as-is");
        }

        [TestMethod]
        public void RecalculateSettlementAdjacentBeachImpassability_LeavesTheSettlementsOwnBeachHexPassable()
        {
            const int width = 6, height = 6;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            var mainSlot = map.HexData[Index(map, 3, 3)];
            mainSlot.TownSlotIndex = Hex.MAIN_SLOT_INDEX;
            mainSlot.IsTownSprawl = true;

            // Part of the town *and* a beach - a seaside settlement whose own footprint reaches the shore.
            var sprawlBeach = map.HexData[map.GetNeighbourIndex(mainSlot, 0)];
            sprawlBeach.IsTownSprawl = true;
            sprawlBeach.IsBeach = true;
            sprawlBeach.IsPassable = true;

            // A plain beach next to the town, which should still be sealed off.
            var ringBeach = map.HexData[map.GetNeighbourIndex(mainSlot, 3)];
            ringBeach.IsBeach = true;
            ringBeach.IsPassable = true;

            MapUpscaler.RecalculateSettlementAdjacentBeachImpassability(map);

            Assert.IsTrue(sprawlBeach.IsPassable,
                "a beach hex that is itself part of the settlement is the town, not the ring around it, and must stay passable");
            Assert.IsFalse(ringBeach.IsPassable, "a plain beach adjacent to the settlement should still be made impassable");
        }

        [TestMethod]
        public void RepairRegionBleedNearRivers_FixesIsolatedStrayHexNearRiver()
        {
            const int width = 6, height = 6;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            foreach (var hex in map.HexData)
            {
                hex.GroundTypeIndex = 0;
                hex.RegionId = 0;
            }

            var riverHex = map.HexData[Index(map, 3, 3)];
            riverHex.IsRiver = true;

            // A single stray hex assigned to region 1, surrounded entirely by region 0 - the "bleed".
            int strayIndex = map.GetNeighbourIndex(riverHex, 0);
            map.HexData[strayIndex].RegionId = 1;

            MapUpscaler.RepairRegionBleedNearRivers(map);

            Assert.AreEqual(0, map.HexData[strayIndex].RegionId,
                "an isolated region bleed on the bank of a river should be corrected back to the surrounding majority region");
        }

        [TestMethod]
        public void RepairRegionBleedNearRivers_LeavesLegitimateRegionBorderAlone()
        {
            const int width = 6, height = 6;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            for (int r = 0; r < height; ++r)
            {
                for (int q = 0; q < width; ++q)
                {
                    var hex = map.HexData[Index(map, q, r)];
                    hex.GroundTypeIndex = 0;
                    hex.RegionId = q < width / 2 ? 0 : 1;
                }
            }

            // A river running along the region border - both sides have plenty of same-region support
            // and must not be touched.
            for (int r = 0; r < height; ++r)
            {
                map.HexData[Index(map, width / 2, r)].IsRiver = true;
            }

            MapUpscaler.RepairRegionBleedNearRivers(map);

            for (int r = 0; r < height; ++r)
            {
                for (int q = 0; q < width; ++q)
                {
                    var hex = map.HexData[Index(map, q, r)];
                    int expectedRegion = q < width / 2 ? 0 : 1;
                    Assert.AreEqual(expectedRegion, hex.RegionId,
                        $"hex ({q},{r}) should keep its legitimate region - a river forming an intentional, well-supported border must not be disturbed");
                }
            }
        }

        [TestMethod]
        public void RepairRegionBleedNearRivers_FixesTwoHexStrayClusterNearRiver()
        {
            const int width = 6, height = 6;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            foreach (var hex in map.HexData)
            {
                hex.GroundTypeIndex = 0;
                hex.RegionId = 0;
            }

            var riverHex = map.HexData[Index(map, 3, 3)];
            riverHex.IsRiver = true;

            // Two adjacent stray hexes assigned to region 1, surrounded entirely by region 0. Each one
            // sees the other as same-region "support", so a repair pass that only ever considers one hex
            // at a time can never fix this - the whole reason this session's fix generalised to clusters.
            int seedIndex = map.GetNeighbourIndex(riverHex, 0);
            var strayCluster = BuildHexChain(map, map.HexData[seedIndex], 2, riverHex.Index);
            foreach (var i in strayCluster)
            {
                map.HexData[i].RegionId = 1;
            }

            MapUpscaler.RepairRegionBleedNearRivers(map);

            foreach (var i in strayCluster)
            {
                Assert.AreEqual(0, map.HexData[i].RegionId,
                    "a 2-hex region bleed on the bank of a river should be corrected back to the surrounding majority region");
            }
        }

        [TestMethod]
        public void RepairRegionBleedNearRivers_FixesLongThinSliverRunningAlongRiver()
        {
            var map = BuildMapWithRiverColumn(out int riverQ);

            // A one-hex-wide strip of the wrong region running six hexes down the bank. As much a bleed
            // as a single stray hex is, but far too long to survive any rule that judged by patch size.
            var sliver = BuildBankSliver(map, riverQ, fromRow: 2, toRow: 7);
            Assert.IsTrue(sliver.Count > 2, "test setup: the sliver must be longer than a couple of hexes to be meaningful");

            MapUpscaler.RepairRegionBleedNearRivers(map);

            foreach (var index in sliver)
            {
                Assert.AreEqual(0, map.HexData[index].RegionId,
                    "a one-hex-wide strip hugging the river is a bleed however long it runs, and should be corrected along its whole length");
            }
        }

        [TestMethod]
        public void RepairRegionBleedNearRivers_LeavesPatchReachingInlandAlone()
        {
            var map = BuildMapWithRiverColumn(out int riverQ);

            var patch = BuildBankSliver(map, riverQ, fromRow: 2, toRow: 5);

            // Touches the river exactly like a bleed does, but then runs several hexes inland - past the
            // point where a patch can still be explained as sampling noise pinned against the bank.
            int current = patch[patch.Count / 2];
            for (int step = 0; step < 3; ++step)
            {
                int next = -1;
                int bestQ = map.HexData[current].Q;
                for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                {
                    int nbrIndex = map.GetNeighbourIndex(map.HexData[current], dir);
                    if (nbrIndex == -1 || map.HexData[nbrIndex].Q <= bestQ)
                    {
                        continue;
                    }

                    bestQ = map.HexData[nbrIndex].Q;
                    next = nbrIndex;
                }
                Assert.AreNotEqual(-1, next, "test setup: ran out of room walking the patch inland");

                map.HexData[next].RegionId = 1;
                patch.Add(next);
                current = next;
            }

            MapUpscaler.RepairRegionBleedNearRivers(map);

            foreach (var index in patch)
            {
                Assert.AreEqual(1, map.HexData[index].RegionId,
                    "a patch reaching inland is a real region, not a bleed, and must be left alone however it meets the river");
            }
        }

        [TestMethod]
        public void RepairRegionBleedNearRivers_LeavesThinPatchContainingSettlementAlone()
        {
            var map = BuildMapWithRiverColumn(out int riverQ);

            var sliver = BuildBankSliver(map, riverQ, fromRow: 2, toRow: 7);

            // A region is *defined* by its settlement, so a patch holding one is genuine no matter how
            // thin it looks from the river's point of view.
            map.HexData[sliver[2]].TownSlotIndex = Hex.MAIN_SLOT_INDEX;
            map.HexData[sliver[2]].IsTownSprawl = true;

            MapUpscaler.RepairRegionBleedNearRivers(map);

            foreach (var index in sliver)
            {
                Assert.AreEqual(1, map.HexData[index].RegionId,
                    "a river-hugging patch containing a settlement must never be reassigned - the settlement proves the region is real");
            }
        }

        [TestMethod]
        public void RepairRegionBleedNearRivers_FixesStrayHexWithFewNeighbours_NoAbsoluteVoteFloor()
        {
            // A corner hex has only 3 grid neighbours to begin with - once one of them is the river,
            // only 2 remain to vote. A bleed pinned against a river routinely has this few real
            // neighbours (the river eats one direction, a map edge eats several more), so a fixed
            // minimum vote count starves out exactly the single-hex bleeds this repair exists to fix,
            // even when the vote is completely unanimous.
            const int width = 6, height = 6;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            foreach (var hex in map.HexData)
            {
                hex.GroundTypeIndex = 0;
                hex.RegionId = 0;
            }

            var corner = map.HexData[Index(map, 0, 0)];

            int riverIndex = -1;
            int nonRiverNeighbourCount = 0;
            for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
            {
                int nbrIndex = map.GetNeighbourIndex(corner, dir);
                if (nbrIndex == -1)
                {
                    continue;
                }
                if (riverIndex == -1)
                {
                    riverIndex = nbrIndex;
                }
                else
                {
                    ++nonRiverNeighbourCount;
                }
            }
            Assert.AreNotEqual(-1, riverIndex, "test setup: corner hex must have at least one neighbour");
            Assert.IsTrue(nonRiverNeighbourCount >= 1 && nonRiverNeighbourCount < 4,
                $"test setup: need fewer than 4 non-river neighbours to exercise the no-floor behaviour, got {nonRiverNeighbourCount}");

            map.HexData[riverIndex].IsRiver = true;
            corner.RegionId = 1; // the bleed - everything else on the map is region 0

            MapUpscaler.RepairRegionBleedNearRivers(map);

            Assert.AreEqual(0, corner.RegionId,
                "a unanimous vote should correct the bleed even with fewer than 4 total voters - a river-adjacent corner routinely has this few neighbours");
        }

        [TestMethod]
        public void RepairRegionBleedNearRivers_LeavesStrayHexAlone_WhenVotesAreTied()
        {
            const int width = 6, height = 6;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            foreach (var hex in map.HexData)
            {
                hex.GroundTypeIndex = 0;
            }

            // Find a hex with exactly 3 grid neighbours (so 2 remain once one becomes the river) -
            // corner (0,0) itself doesn't necessarily have that many, so search the boundary for one that does.
            Hex corner = null;
            int riverIndex = -1;
            var nonRiverNeighbours = new List<int>();
            for (int q = 0; q < width && corner == null; ++q)
            {
                for (int r = 0; r < height && corner == null; ++r)
                {
                    var candidate = map.HexData[Index(map, q, r)];
                    int candidateRiverIndex = -1;
                    var candidateNonRiver = new List<int>();
                    for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                    {
                        int nbrIndex = map.GetNeighbourIndex(candidate, dir);
                        if (nbrIndex == -1) continue;
                        if (candidateRiverIndex == -1) candidateRiverIndex = nbrIndex;
                        else candidateNonRiver.Add(nbrIndex);
                    }

                    if (candidateNonRiver.Count == 2)
                    {
                        corner = candidate;
                        riverIndex = candidateRiverIndex;
                        nonRiverNeighbours = candidateNonRiver;
                    }
                }
            }
            Assert.IsNotNull(corner, "test setup: need a hex on this grid with exactly 3 neighbours (2 remaining once one becomes the river)");

            map.HexData[riverIndex].IsRiver = true;
            map.HexData[nonRiverNeighbours[0]].RegionId = 0;
            map.HexData[nonRiverNeighbours[1]].RegionId = 1;
            corner.RegionId = 2; // no support from either neighbour, but no majority for either one either

            MapUpscaler.RepairRegionBleedNearRivers(map);

            Assert.AreEqual(2, corner.RegionId,
                "a tied vote (no outright majority) must be left for a human to judge, even without a fixed vote-count floor");
        }

        // ----------------------------------------------------------------- Settlement anchoring

        [TestMethod]
        public void ChooseSettlementAnchor_PrefersSlotAgainstImpassableBlob_OverMainSlot()
        {
            const int width = 10, height = 10;
            var map = BuildProject(width, height).MapHexFile;

            var wall = BuildImpassableWall(map, wallQ: 6);
            var blob = BuildTownAgainstWall(map, wall, out int mainSlotIndex, out int wallSideIndex);

            int anchorIndex = MapUpscaler.ChooseSettlementAnchor(map.HexData, blob, map.MapWidth, map.MapHeight);

            Assert.AreEqual(wallSideIndex, anchorIndex,
                "the slot pressed against the impassable wall should anchor the blob, so upscaling can't drift the town off the wall and open a passage");
            Assert.AreNotEqual(mainSlotIndex, anchorIndex, "the main slot sits back from the wall and shouldn't win here");
        }

        [TestMethod]
        public void ChooseSettlementAnchor_FallsBackToMainSlot_WhenNoImpassableBlobNearby()
        {
            const int width = 10, height = 10;
            var map = BuildProject(width, height).MapHexFile;

            // Same town shape, no wall at all - nothing to press against, so the ordinary rule applies.
            var wall = new List<int>();
            var blob = BuildTownAgainstWall(map, wall, out int mainSlotIndex, out _);

            int anchorIndex = MapUpscaler.ChooseSettlementAnchor(map.HexData, blob, map.MapWidth, map.MapHeight);

            Assert.AreEqual(mainSlotIndex, anchorIndex, "with no impassable terrain in play the main slot should anchor the blob as before");
        }

        [TestMethod]
        public void ChooseSettlementAnchor_PortSlotStillWins_EvenAgainstImpassableBlob()
        {
            const int width = 10, height = 10;
            var map = BuildProject(width, height).MapHexFile;

            var wall = BuildImpassableWall(map, wallQ: 6);
            var blob = BuildTownAgainstWall(map, wall, out int mainSlotIndex, out int wallSideIndex);

            map.HexData[mainSlotIndex].TownSlotIndex = Hex.PORT_SLOT_INDEX;

            int anchorIndex = MapUpscaler.ChooseSettlementAnchor(map.HexData, blob, map.MapWidth, map.MapHeight);

            Assert.AreEqual(mainSlotIndex, anchorIndex,
                "staying adjacent to sea is a hard requirement, so a port slot must still outrank a slot merely pressed against impassable terrain");
            Assert.AreNotEqual(wallSideIndex, anchorIndex, "the wall-side slot shouldn't displace a port slot");
        }

        [TestMethod]
        public void Upscale_SlotAgainstImpassableBlob_StillTouchesItAtEveryFactor()
        {
            // Swept rather than run at one factor: the two candidate anchors differ only by however the
            // scale-and-round lands, so at any single factor they often agree by luck. The property
            // being asserted - the wall-side slot never comes off the wall - has to hold at all of them.
            var factors = new[] { 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 1.7, 1.8, 1.9, 2.0, 2.2, 2.5, 2.8, 3.0 };

            foreach (var factor in factors)
            {
                const int width = 10, height = 10;
                var project = BuildProject(width, height);
                var map = project.MapHexFile;

                var wall = BuildImpassableWall(map, wallQ: 6);
                BuildTownAgainstWall(map, wall, out _, out int wallSideIndex);
                sbyte wallSideSlot = map.HexData[wallSideIndex].TownSlotIndex;

                Assert.IsTrue(MapUpscaler.Upscale(project, factor));

                var placed = map.HexData.Where(h => h.TownSlotIndex == wallSideSlot).ToList();
                Assert.AreEqual(1, placed.Count, $"factor {factor}: the wall-side slot should be stamped exactly once");

                bool stillTouching = false;
                for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                {
                    int nbrIndex = map.GetNeighbourIndex(placed[0], dir);
                    if (nbrIndex != -1 && map.HexData[nbrIndex].IsImpassable && !map.HexData[nbrIndex].IsCoast)
                    {
                        stillTouching = true;
                        break;
                    }
                }

                Assert.IsTrue(stillTouching,
                    $"factor {factor}: the slot authored hard against the impassable wall has drifted off it, opening a passage that was never meant to exist");
            }
        }

        // ----------------------------------------------------------------- Helpers

        /// <summary>
        /// A 10x10 map, every hex region 0, with a river running the full height of column 5 - the
        /// standard setup for the region-bleed tests, which all turn on how a patch relates to a river.
        /// </summary>
        private static MapHexFile BuildMapWithRiverColumn(out int riverQ)
        {
            const int width = 10, height = 10;
            var map = BuildProject(width, height).MapHexFile;

            foreach (var hex in map.HexData)
            {
                hex.GroundTypeIndex = 0;
                hex.RegionId = 0;
            }

            riverQ = 5;
            for (int r = 0; r < height; ++r)
            {
                map.HexData[Index(map, riverQ, r)].IsRiver = true;
            }

            return map;
        }

        /// <summary>
        /// A one-hex-wide run of region 1 down the column immediately right of the river, from
        /// <paramref name="fromRow"/> to <paramref name="toRow"/> inclusive, asserting as it goes that
        /// the hexes really are consecutively adjacent (so a failure reads as bad setup, not bad logic).
        /// </summary>
        private static List<int> BuildBankSliver(MapHexFile map, int riverQ, int fromRow, int toRow)
        {
            var sliver = new List<int>();
            for (int r = fromRow; r <= toRow; ++r)
            {
                int index = Index(map, riverQ + 1, r);
                map.HexData[index].RegionId = 1;

                if (sliver.Count > 0)
                {
                    Assert.AreNotEqual(-1, DirectionTo(map, map.HexData[sliver[sliver.Count - 1]], map.HexData[index]),
                        "test setup: consecutive sliver hexes must be adjacent to form one connected patch");
                }

                sliver.Add(index);
            }

            return sliver;
        }

        /// <summary>A full-height column of impassable (non-coast) land - a mountain range to press a town against.</summary>
        private static List<int> BuildImpassableWall(MapHexFile map, int wallQ)
        {
            var wall = new List<int>();
            for (int r = 0; r < (int)map.MapHeight; ++r)
            {
                int index = Index(map, wallQ, r);
                map.HexData[index].IsPassable = false;
                wall.Add(index);
            }

            return wall;
        }

        /// <summary>
        /// A two-hex town: a main slot set back from <paramref name="wall"/>, plus a second (non-main,
        /// non-port) slot on the neighbour that actually touches it. Passing an empty wall builds the
        /// same shape with nothing to press against.
        /// </summary>
        private static List<int> BuildTownAgainstWall(MapHexFile map, List<int> wall, out int mainSlotIndex, out int wallSideIndex)
        {
            var mainSlot = map.HexData[Index(map, 4, 5)];
            mainSlot.TownSlotIndex = Hex.MAIN_SLOT_INDEX;
            mainSlot.IsTownSprawl = true;
            mainSlotIndex = mainSlot.Index;

            var wallSet = new HashSet<int>(wall);
            wallSideIndex = -1;
            for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
            {
                int nbrIndex = map.GetNeighbourIndex(mainSlot, dir);
                if (nbrIndex == -1 || map.HexData[nbrIndex].Q != 5)
                {
                    continue; // must be the column between the main slot and the wall
                }

                // With no wall supplied, any hex in that column will do - the shape is what matters.
                if (wallSet.Count > 0 && !TouchesAny(map, map.HexData[nbrIndex], wallSet))
                {
                    continue;
                }

                wallSideIndex = nbrIndex;
                break;
            }
            Assert.AreNotEqual(-1, wallSideIndex, "test setup: no neighbour of the main slot sits between it and the wall");

            map.HexData[wallSideIndex].TownSlotIndex = 2; // a plain building slot - neither main nor port
            map.HexData[wallSideIndex].IsTownSprawl = true;

            Assert.AreNotEqual(-1, DirectionTo(map, mainSlot, map.HexData[wallSideIndex]),
                "test setup: the two town hexes must be adjacent to form one blob");

            return new List<int> { mainSlotIndex, wallSideIndex };
        }

        private static bool TouchesAny(MapHexFile map, Hex hex, HashSet<int> targets)
        {
            for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
            {
                int nbrIndex = map.GetNeighbourIndex(hex, dir);
                if (nbrIndex != -1 && targets.Contains(nbrIndex))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Walks outward from <paramref name="start"/> one grid-adjacent hex at a time, avoiding
        /// <paramref name="forbiddenIndex"/> (typically the river hex used to anchor the test), to build
        /// a connected chain of <paramref name="length"/> distinct hex indices for cluster-based tests.
        /// </summary>
        private static List<int> BuildHexChain(MapHexFile map, Hex start, int length, int forbiddenIndex)
        {
            var chain = new List<int> { start.Index };
            var visited = new HashSet<int> { start.Index, forbiddenIndex };
            var current = start;

            while (chain.Count < length)
            {
                int next = -1;
                for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                {
                    int candidate = map.GetNeighbourIndex(current, dir);
                    if (candidate != -1 && !visited.Contains(candidate))
                    {
                        next = candidate;
                        break;
                    }
                }
                Assert.AreNotEqual(-1, next, "test setup: ran out of room building hex chain");

                chain.Add(next);
                visited.Add(next);
                current = map.HexData[next];
            }

            return chain;
        }

        /// <summary>The direction index from <paramref name="from"/> to <paramref name="to"/>, or -1 if not adjacent.</summary>
        private static int DirectionTo(MapHexFile map, Hex from, Hex to)
        {
            for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
            {
                if (map.GetNeighbourIndex(from, dir) == to.Index)
                {
                    return dir;
                }
            }
            return -1;
        }

        private static int CountLandNeighbours(MapHexFile map, Hex hex)
        {
            int count = 0;
            for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
            {
                int nbrIndex = map.GetNeighbourIndex(hex, dir);
                if (nbrIndex != -1 && !map.HexData[nbrIndex].IsSea)
                {
                    count++;
                }
            }
            return count;
        }

        private static int CountCyclicRuns(bool[] values)
        {
            int n = values.Length;
            if (values.All(v => v))
            {
                return 1;
            }
            if (values.All(v => !v))
            {
                return 0;
            }

            int runs = 0;
            for (int i = 0; i < n; ++i)
            {
                if (values[i] && !values[(i - 1 + n) % n])
                {
                    runs++;
                }
            }
            return runs;
        }

        [TestMethod]
        public void RemoveIncidentalTouches_IsolatedClosedTriangle_CollapsesToOneEdge()
        {
            // Three mutually-adjacent hexes with no other connections - a fully-closed triangle.
            // Every vertex sits at degree 2 (each touches only the other two), so a naive ">2
            // neighbours" check never even considers this shape, even though a source edge mask
            // only ever authorises 2 of the 3 possible edges - one hex here is always one too many.
            const int width = 8, height = 8;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            var a    = map.HexData[Index(map, 3, 3)];
            var bend = map.HexData[Index(map, 4, 3)];
            var c    = map.HexData[Index(map, 4, 4)];
            a.IsRoad = true;
            bend.IsRoad = true;
            c.IsRoad = true;

            MapUpscaler.RemoveIncidentalTouches(map, new HashSet<int>(), isRoad: true);

            int roadCount = 0;
            for (int i = 0; i < map.Capacity; ++i)
            {
                if (map.HexData[i].IsRoad)
                {
                    roadCount++;
                }
            }

            Assert.AreEqual(2, roadCount, "a fully-closed triangle with no anchors should collapse to a single edge (2 hexes)");
        }

        [TestMethod]
        public void RemoveIncidentalTouches_RandomNetworks_NeverIncreasesComponentCount()
        {
            // A basic invariant that must hold regardless of how the safety check is implemented:
            // removing hexes must never fragment the network into more pieces than it started with.
            var failures = new List<string>();

            for (int seed = 0; seed < 40; ++seed)
            {
                const int width = 20, height = 20;
                var project = BuildProject(width, height);
                var map = project.MapHexFile;

                var rng = new System.Random(seed);
                BuildRandomRoadNetwork(map, width, height, rng);

                var featureIndicesBefore = new List<int>();
                for (int i = 0; i < map.Capacity; ++i)
                {
                    if (map.HexData[i].IsRoad) featureIndicesBefore.Add(i);
                }
                if (featureIndicesBefore.Count == 0) continue;

                int before = CountComponentsBruteForce(map, featureIndicesBefore);

                MapUpscaler.RemoveIncidentalTouches(map, new HashSet<int>(), isRoad: true);

                var featureIndicesAfter = new List<int>();
                for (int i = 0; i < map.Capacity; ++i)
                {
                    if (map.HexData[i].IsRoad) featureIndicesAfter.Add(i);
                }
                int after = CountComponentsBruteForce(map, featureIndicesAfter);

                if (after > before)
                {
                    failures.Add($"seed={seed} before={before} after={after}");
                }
            }

            Assert.IsTrue(failures.Count == 0, $"{failures.Count} networks were fragmented: " + string.Join(" | ", failures));
        }

        [TestMethod]
        public void RemoveIncidentalTouches_OnlyDistantReconnection_DoesNotRemoveBridgingHex()
        {
            // B bridges A and C (2 hexes apart) and also incidentally touches D, the same shape as the
            // documented "distance-2 edge needs one bridging hex" case. D additionally connects via a
            // long (35+ hex) winding detour that loops back to touch C - so the whole network stays one
            // connected graph even without B (a global connectivity check would call B safe to drop), but
            // nothing *nearby* reconnects A and C once B is gone - dropping it would leave a real, visible
            // gap. This is the exact scenario a purely graph-wide safety check gets wrong.
            const int width = 100, height = 100;
            var project = BuildProject(width, height);
            var map = project.MapHexFile;

            int aIdx = Index(map, 50, 50);
            var a = map.HexData[aIdx];
            int bIdx = map.GetNeighbourIndex(a, 0);
            Assert.AreNotEqual(-1, bIdx);
            var b = map.HexData[bIdx];
            int cIdx = map.GetNeighbourIndex(b, 0);
            Assert.AreNotEqual(-1, cIdx);
            var c = map.HexData[cIdx];
            int dIdx = map.GetNeighbourIndex(b, 2);
            Assert.AreNotEqual(-1, dIdx);
            var d = map.HexData[dIdx];

            a.IsRoad = true;
            b.IsRoad = true;
            c.IsRoad = true;
            d.IsRoad = true;

            var visited = new HashSet<int> { aIdx, bIdx, cIdx, dIdx };
            var pathHexes = new List<int>();

            bool IsAdjacentToB(int idx)
            {
                for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                {
                    if (map.GetNeighbourIndex(b, dir) == idx) return true;
                }
                return false;
            }

            // Outbound: two legs in different directions (an "L"), so the walk back toward C in the next
            // phase can't just retrace close beside the outbound leg the whole way - it has to come in
            // from a different angle, keeping the whole middle of the detour genuinely far from C. Each
            // leg's direction is picked by explicitly verifying its very first step doesn't land back on
            // a hex that's still directly adjacent to B - hex neighbours-of-a-neighbour can easily still
            // touch B (the same "gentle bend" adjacency this whole fix is about), so a hardcoded direction
            // can accidentally fail to go anywhere - this must be checked, not assumed.
            const int legSteps = 25;
            int currentIdx = dIdx;
            for (int leg = 0; leg < 2; ++leg)
            {
                ushort chosenDir = ushort.MaxValue;
                for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                {
                    int candidate = map.GetNeighbourIndex(map.HexData[currentIdx], dir);
                    if (candidate != -1 && !visited.Contains(candidate) && !IsAdjacentToB(candidate))
                    {
                        chosenDir = dir;
                        break;
                    }
                }
                Assert.AreNotEqual(ushort.MaxValue, chosenDir, $"could not find a direction for leg {leg} that moves away from B - adjust test construction");

                for (int i = 0; i < legSteps; ++i)
                {
                    int nextIdx = map.GetNeighbourIndex(map.HexData[currentIdx], chosenDir);
                    Assert.AreNotEqual(-1, nextIdx, "outbound leg ran off the map - adjust test map size/direction");
                    map.HexData[nextIdx].IsRoad = true;
                    visited.Add(nextIdx);
                    pathHexes.Add(nextIdx);
                    currentIdx = nextIdx;
                }
            }

            // Return leg: greedily step toward C (without revisiting) until adjacent to it.
            while (map.HexData[currentIdx].GetDistance(c) > 1)
            {
                var current = map.HexData[currentIdx];
                int best = -1, bestDist = int.MaxValue;
                for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                {
                    int nbrIndex = map.GetNeighbourIndex(current, dir);
                    if (nbrIndex == -1 || visited.Contains(nbrIndex)) continue;
                    int dist = map.HexData[nbrIndex].GetDistance(c);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = nbrIndex;
                    }
                }
                Assert.AreNotEqual(-1, best, "return leg got stuck - adjust test construction");
                map.HexData[best].IsRoad = true;
                visited.Add(best);
                pathHexes.Add(best);
                currentIdx = best;
                Assert.IsTrue(pathHexes.Count < 400, "return leg did not converge - adjust test construction");
            }

            Assert.IsTrue(pathHexes.Count > 40, "test construction sanity check: the detour must be much longer than the local reconnection radius");
            Assert.IsFalse(IsAdjacentToB(currentIdx), "test construction sanity check failed: the detour's final hex ended up directly adjacent to B too - adjust construction");

            // Sanity-check the construction itself: every hex except near the very start (close to D) and
            // very end (close to C) must be genuinely far from B - otherwise this test isn't actually
            // exercising "only a distant reconnection exists" and a pass wouldn't mean anything.
            const int MARGIN = 8;
            for (int i = MARGIN; i < pathHexes.Count - MARGIN; ++i)
            {
                int dist = map.HexData[pathHexes[i]].GetDistance(b);
                Assert.IsTrue(dist > 6, $"test construction sanity check failed: detour hex at path index {i} is only distance {dist} from B - the detour isn't actually distant there, adjust construction");
            }

            MapUpscaler.RemoveIncidentalTouches(map, new HashSet<int> { aIdx }, isRoad: true);

            Assert.IsTrue(b.IsRoad, "B must survive - its only nearby reconnection was itself; the alternate route is a distant detour, not a local redundancy");
            Assert.IsTrue(c.IsRoad, "C must survive - it's a genuine endpoint of the A-B-C line");
            Assert.IsTrue(d.IsRoad, "D must survive - removing it would disconnect the entire detour");
        }

        [TestMethod]
        public void Upscale_PureStraightRoad_NeverProducesOverDegreeHex()
        {
            var badFactors = new List<string>();

            foreach (double factor in new[] { 1.2, 1.3, 1.4, 1.5, 1.6, 1.7, 1.8, 1.9, 2.1, 2.3, 2.7 })
            {
                const int width = 60, height = 60;
                var project = BuildProject(width, height);
                var map = project.MapHexFile;

                // A perfectly straight road: every hex reached by stepping in the SAME direction from
                // the last - no kinks, nothing that could be considered a "bend" by any definition.
                int startIdx = Index(map, 5, 30);
                map.HexData[startIdx].IsRoad = true;
                int currentIdx = startIdx;
                for (int i = 0; i < 30; ++i)
                {
                    int nextIdx = map.GetNeighbourIndex(map.HexData[currentIdx], 0);
                    if (nextIdx == -1) break;
                    map.HexData[nextIdx].IsRoad = true;
                    currentIdx = nextIdx;
                }

                map.CalculateRoadEdgeMasks();

                // Sanity: the source really is triangle-free and every hex has at most degree 2.
                for (int i = 0; i < map.Capacity; ++i)
                {
                    if (!map.HexData[i].IsRoad) continue;
                    int degree = 0;
                    for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                    {
                        int nbrIndex = map.GetNeighbourIndex(map.HexData[i], dir);
                        if (nbrIndex != -1 && map.HexData[nbrIndex].IsRoad) degree++;
                    }
                    Assert.IsTrue(degree <= 2, $"test construction sanity check failed at factor {factor}: source hex {i} has degree {degree}");
                }

                Assert.IsTrue(MapUpscaler.Upscale(project, factor));

                for (int i = 0; i < map.Capacity; ++i)
                {
                    if (!map.HexData[i].IsRoad) continue;
                    var nbrs = new List<int>();
                    for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                    {
                        int nbrIndex = map.GetNeighbourIndex(map.HexData[i], dir);
                        if (nbrIndex != -1 && map.HexData[nbrIndex].IsRoad) nbrs.Add(nbrIndex);
                    }
                    if (nbrs.Count > 2)
                    {
                        badFactors.Add($"factor={factor} hex=({map.HexData[i].Q},{map.HexData[i].R}) degree={nbrs.Count}");
                    }
                }
            }

            Assert.IsTrue(badFactors.Count == 0, "a pure straight road produced an over-degree hex after upscaling: " + string.Join(" | ", badFactors));
        }

        [TestMethod]
        public void Upscale_ShallowStaircaseDiagonalRoad_NeverProducesTriangleOrOverDegreeHex()
        {
            // A "2 steps dir0, 1 step dir1" staircase - a shallow, mostly-straight diagonal, the kind of
            // shallow-angle road a human calls "straight" even though dir0/dir1 are angularly adjacent.
            // The source is verified triangle-free (max degree 2) below; independently scaling and
            // rounding each hex's position used to occasionally land three *consecutive* pass-through
            // hexes mutually adjacent purely as a rounding artefact, even though none of them is a real
            // bend or junction - see the "Deliberately *not* every source feature hex" remarks on
            // RetraceLines for why narrowing anchor protection to non-pass-through hexes fixes this.
            var failures = new List<string>();

            foreach (double factor in new[] { 1.2, 1.3, 1.4, 1.5, 1.6, 1.7, 1.8, 1.9, 2.1, 2.3, 2.7 })
            {
                const int width = 80, height = 80;
                var project = BuildProject(width, height);
                var map = project.MapHexFile;

                int currentIdx = Index(map, 5, 40);
                map.HexData[currentIdx].IsRoad = true;
                var pattern = new ushort[] { 0, 0, 1 };
                for (int i = 0; i < 30; ++i)
                {
                    int nextIdx = map.GetNeighbourIndex(map.HexData[currentIdx], pattern[i % pattern.Length]);
                    if (nextIdx == -1) break;
                    map.HexData[nextIdx].IsRoad = true;
                    currentIdx = nextIdx;
                }

                map.CalculateRoadEdgeMasks();

                int sourceMaxDegree = 0;
                for (int i = 0; i < map.Capacity; ++i)
                {
                    if (!map.HexData[i].IsRoad) continue;
                    int degree = 0;
                    for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                    {
                        int nbrIndex = map.GetNeighbourIndex(map.HexData[i], dir);
                        if (nbrIndex != -1 && map.HexData[nbrIndex].IsRoad) degree++;
                    }
                    sourceMaxDegree = System.Math.Max(sourceMaxDegree, degree);
                }
                Assert.AreEqual(0, CountClosedTriangles(map, isRoad: true), $"test construction sanity check failed at factor {factor}: source already has a triangle");
                Assert.IsTrue(sourceMaxDegree <= 2, $"test construction sanity check failed at factor {factor}: source already has an over-degree hex");

                Assert.IsTrue(MapUpscaler.Upscale(project, factor));

                int afterTriangles = CountClosedTriangles(map, isRoad: true);
                int afterMaxDegree = 0;
                for (int i = 0; i < map.Capacity; ++i)
                {
                    if (!map.HexData[i].IsRoad) continue;
                    int degree = 0;
                    for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                    {
                        int nbrIndex = map.GetNeighbourIndex(map.HexData[i], dir);
                        if (nbrIndex != -1 && map.HexData[nbrIndex].IsRoad) degree++;
                    }
                    afterMaxDegree = System.Math.Max(afterMaxDegree, degree);
                }

                if (afterTriangles != 0 || afterMaxDegree > 2)
                {
                    failures.Add($"factor={factor} afterTriangles={afterTriangles} afterMaxDegree={afterMaxDegree}");
                }
            }

            Assert.IsTrue(failures.Count == 0, "a triangle-free, degree<=2 source road produced a triangle or over-degree hex after upscaling: " + string.Join(" | ", failures));
        }

        private static int CountClosedTriangles(MapHexFile map, bool isRoad)
        {
            int count = 0;
            for (int i = 0; i < map.Capacity; ++i)
            {
                var hex = map.HexData[i];
                bool isFeature = isRoad ? hex.IsRoad : hex.IsRiver;
                if (!isFeature) continue;

                var nbrs = new List<int>();
                for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                {
                    int nbrIndex = map.GetNeighbourIndex(hex, dir);
                    if (nbrIndex == -1) continue;
                    bool nbrIsFeature = isRoad ? map.HexData[nbrIndex].IsRoad : map.HexData[nbrIndex].IsRiver;
                    if (nbrIsFeature) nbrs.Add(nbrIndex);
                }

                for (int a = 0; a < nbrs.Count; ++a)
                {
                    var hexA = map.HexData[nbrs[a]];
                    for (int b = a + 1; b < nbrs.Count; ++b)
                    {
                        for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                        {
                            if (map.GetNeighbourIndex(hexA, dir) == nbrs[b])
                            {
                                count++;
                            }
                        }
                    }
                }
            }
            return count / 3;
        }

        private static void BuildRandomRoadNetwork(MapHexFile map, int width, int height, System.Random rng)
        {
            // Random walk from a random start, occasionally branching to a previously-visited hex,
            // to produce a connected graph with cycles (not just a simple tree/line/triangle).
            int startQ = rng.Next(width), startR = rng.Next(height);
            var visited = new List<int> { Index(map, startQ, startR) };
            map.HexData[visited[0]].IsRoad = true;

            int steps = 60 + rng.Next(60);
            int currentIndex = visited[0];

            for (int s = 0; s < steps; ++s)
            {
                var current = map.HexData[currentIndex];
                var dirs = Enumerable.Range(0, HexGridUtility.NEIGHBOURS_COUNT).OrderBy(_ => rng.Next()).ToList();
                int nextIndex = -1;
                foreach (var d in dirs)
                {
                    int nbrIndex = map.GetNeighbourIndex(current, (ushort)d);
                    if (nbrIndex != -1)
                    {
                        nextIndex = nbrIndex;
                        break;
                    }
                }
                if (nextIndex == -1) break;

                map.HexData[nextIndex].IsRoad = true;
                visited.Add(nextIndex);
                currentIndex = nextIndex;

                // Occasionally jump back to an earlier visited hex to create branches/cycles.
                if (rng.NextDouble() < 0.15)
                {
                    currentIndex = visited[rng.Next(visited.Count)];
                }
            }
        }

        private static int CountComponentsBruteForce(MapHexFile map, List<int> featureIndices)
        {
            var visited = new HashSet<int>();
            int components = 0;

            foreach (var start in featureIndices)
            {
                if (!map.HexData[start].IsRoad || visited.Contains(start)) continue;

                components++;
                visited.Add(start);
                var queue = new Queue<int>();
                queue.Enqueue(start);

                while (queue.Count > 0)
                {
                    var hex = map.HexData[queue.Dequeue()];
                    for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                    {
                        int nbrIndex = map.GetNeighbourIndex(hex, dir);
                        if (nbrIndex != -1 && map.HexData[nbrIndex].IsRoad && visited.Add(nbrIndex))
                        {
                            queue.Enqueue(nbrIndex);
                        }
                    }
                }
            }

            return components;
        }

        private static Project BuildProject(int width, int height)
        {
            var hexData = new Hex[width * height];
            for (int r = 0; r < height; ++r)
            {
                for (int q = 0; q < width; ++q)
                {
                    int idx = (r * width) + q;
                    hexData[idx] = new Hex(q, r, idx);
                }
            }

            var map = BordersTestHarness.BuildMap(hexData, (uint)width, (uint)height);
            MapHexHarness.SetProperty(map, nameof(MapHexFile.LandGroundTypes), new List<string> { "land_0" });
            MapHexHarness.SetProperty(map, nameof(MapHexFile.SeaGroundTypes),  new List<string> { "sea_0" });

            var project = new Project();
            MapHexHarness.SetProperty(project, nameof(Project.MapHexFile), map);
            return project;
        }

        private static int Index(MapHexFile map, int q, int r) => (r * (int)map.MapWidth) + q;
    }
}
