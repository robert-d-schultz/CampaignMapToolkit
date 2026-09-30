using System;
using System.Collections.Generic;
using System.Linq;
using CAIME;
using CAIME.Tests.Helpers;
using CAIME.Upscaler;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CAIME.Tests.Unit
{
    /// <summary>
    /// Tests for <see cref="MapHexFile.UpscaleMapHex"/>, the nearest-neighbour upscaler: each new hex
    /// copies the source hex its centre lands in once shrunk back onto the source grid (see
    /// <see cref="HexGeometryTests"/> for the underlying geometry).
    /// </summary>
    [TestClass]
    public class UpscaleMapHexTests
    {
        [TestMethod]
        public void Factor1_IsANoOp_ReturnsFalse()
        {
            var map = BuildGrid(3, 3);
            var result = map.UpscaleMapHex(1);

            Assert.IsFalse(result);
            Assert.AreEqual(3u, map.MapWidth);
            Assert.AreEqual(3u, map.MapHeight);
        }

        [TestMethod]
        public void Factor0_ReturnsFalse()
        {
            var map = BuildGrid(3, 3);
            var result = map.UpscaleMapHex(0);

            Assert.IsFalse(result);
        }

        [TestMethod]
        public void Upscale_ScalesDimensionsAndCapacity()
        {
            var map = BuildGrid(4, 3);
            var result = map.UpscaleMapHex(3);

            Assert.IsTrue(result);
            Assert.AreEqual(12u, map.MapWidth);
            Assert.AreEqual(9u, map.MapHeight);
            Assert.AreEqual(108u, map.Capacity);
            Assert.AreEqual(108, map.HexData.Length);
        }

        [TestMethod]
        public void Upscale_NearestNeighbourHexCopiesSourceTerrainFlags()
        {
            const int width = 3, height = 3;
            var map = BuildGrid(width, height);

            var sourceHex = map.HexData[Index(map, 1, 1)];
            sourceHex.IsSea = true;
            sourceHex.GroundTypeIndex = 0;

            const uint factor = 4;
            var (anchorQ, anchorR) = HexGeometry.Scale(1, 1, factor);

            map.UpscaleMapHex(factor);

            var anchorHex = map.HexData[Index(map, anchorQ, anchorR)];
            Assert.IsTrue(anchorHex.IsSea, $"the hex nearest to source (1,1)'s scaled centre ({anchorQ},{anchorR}) should have copied IsSea from it");

            // A hex far from (1,1) - nearest to source (0,0) instead - should not have picked up the flag.
            var (farQ, farR) = HexGeometry.Scale(0, 0, factor);
            var farHex = map.HexData[Index(map, farQ, farR)];
            Assert.IsFalse(farHex.IsSea, $"hex ({farQ},{farR}), nearest to source (0,0), should not have picked up (1,1)'s IsSea flag");
        }

        [TestMethod]
        public void Upscale_TownSlotOnlyKeptOnBlockOrigin()
        {
            const int width = 3, height = 3;
            var map = BuildGrid(width, height);

            var townHex = map.HexData[Index(map, 1, 1)];
            townHex.TownSlotIndex = Hex.MAIN_SLOT_INDEX;
            townHex.IsTownSprawl = false;

            const uint factor = 3;
            map.UpscaleMapHex(factor);

            int originCol = 1 * (int)factor;
            int originRow = 1 * (int)factor;

            int slotCount = 0;
            for (int r = 0; r < factor; ++r)
            {
                for (int c = 0; c < factor; ++c)
                {
                    var hex = map.HexData[Index(map, originCol + c, originRow + r)];
                    if (hex.TownSlotIndex != Hex.INVALID_SLOT_INDEX)
                    {
                        slotCount++;
                        Assert.IsTrue(c == 0 && r == 0, "only the block's origin hex should keep the slot index");
                    }
                }
            }

            Assert.AreEqual(1, slotCount, "exactly one hex in the expanded block should keep the town slot");
        }

        [TestMethod]
        public void Upscale_RoadEdgeMasksAreReciprocallyConsistent()
        {
            // A short road strip along row 0 of a small grid, then upscale it.
            const int width = 4, height = 2;
            var map = BuildGrid(width, height);
            for (int q = 0; q < width; ++q)
            {
                map.HexData[Index(map, q, 0)].IsRoad = true;
            }

            map.UpscaleMapHex(2);

            for (int i = 0; i < map.Capacity; ++i)
            {
                var hex = map.HexData[i];
                for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                {
                    bool bitSet = (hex.RoadEdgeMask & (1 << dir)) != 0;
                    if (!bitSet)
                    {
                        continue;
                    }

                    int nbrIndex = map.GetNeighbourIndex(hex, dir);
                    Assert.AreNotEqual(-1, nbrIndex, $"hex {i} has a road edge pointing off the map");

                    var nbr = map.HexData[nbrIndex];
                    Assert.IsTrue(hex.IsRoad && nbr.IsRoad, $"hex {i} <-> {nbrIndex} road edge must connect two road hexes");

                    int invDir = HexGridUtility.InverseDir(dir);
                    Assert.IsTrue((nbr.RoadEdgeMask & (1 << invDir)) != 0,
                        $"hex {i} <-> {nbrIndex} road edge must be reciprocated on the neighbour");
                }
            }
        }

        [TestMethod]
        public void FractionalFactor_RoundsDimensionsAndKeepsEveryHexUnique()
        {
            // 1.5x on an 8x8 grid should land on 12x12 (round(8*1.5)), and every new hex must still
            // map back to a source hex that actually exists (no out-of-range reads).
            const int width = 8, height = 8;
            var map = BuildGrid(width, height);

            var result = map.UpscaleMapHex(1.5);

            Assert.IsTrue(result);
            Assert.AreEqual(12u, map.MapWidth);
            Assert.AreEqual(12u, map.MapHeight);
            Assert.AreEqual(144, map.HexData.Length);

            foreach (var hex in map.HexData)
            {
                Assert.IsNotNull(hex);
            }
        }

        [TestMethod]
        public void FractionalFactor_TownSlotStillKeptExactlyOnce()
        {
            const int width = 4, height = 4;
            var map = BuildGrid(width, height);

            var townHex = map.HexData[Index(map, 2, 2)];
            townHex.TownSlotIndex = Hex.MAIN_SLOT_INDEX;

            map.UpscaleMapHex(1.25);

            int slotCount = 0;
            foreach (var hex in map.HexData)
            {
                if (hex.TownSlotIndex != Hex.INVALID_SLOT_INDEX)
                {
                    slotCount++;
                }
            }

            Assert.AreEqual(1, slotCount, "a fractional factor must not duplicate (or drop) the town slot");
        }

        [TestMethod]
        public void Upscale_TownSprawlScalesUpAsOneConnectedBlob()
        {
            const int width = 5, height = 5;
            var map = BuildGrid(width, height);

            var townHex = map.HexData[Index(map, 2, 2)];
            townHex.TownSlotIndex = Hex.MAIN_SLOT_INDEX;
            townHex.IsTownSprawl  = true;
            foreach (var nbr in Neighbours(map, townHex))
            {
                nbr.IsTownSprawl = true;
            }

            map.UpscaleMapHex(2);

            var sprawl = map.HexData.Where(h => h.IsTownSprawl).ToList();
            Assert.IsTrue(sprawl.Count > 7, $"the 7-hex sprawl should grow with the map, got {sprawl.Count} hexes");

            var reached = new HashSet<Hex> { sprawl[0] };
            var queue   = new Queue<Hex>(reached);
            while (queue.Count > 0)
            {
                foreach (var nbr in Neighbours(map, queue.Dequeue()))
                {
                    if (nbr.IsTownSprawl && reached.Add(nbr))
                    {
                        queue.Enqueue(nbr);
                    }
                }
            }

            Assert.AreEqual(sprawl.Count, reached.Count, "the scaled sprawl must stay one connected blob");
        }

        // ----------------------------------------------------------------- Helpers

        private static MapHexFile BuildGrid(int width, int height)
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
            return BordersTestHarness.BuildMap(hexData, (uint)width, (uint)height);
        }

        private static int Index(MapHexFile map, int q, int r) => (r * (int)map.MapWidth) + q;

        private static IEnumerable<Hex> Neighbours(MapHexFile map, Hex hex)
        {
            for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
            {
                int nbrIndex = map.GetNeighbourIndex(hex, dir);
                if (nbrIndex != -1)
                {
                    yield return map.HexData[nbrIndex];
                }
            }
        }
    }
}
