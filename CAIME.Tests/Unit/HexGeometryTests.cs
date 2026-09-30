using CAIME;
using CAIME.Upscaler;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CAIME.Tests.Unit
{
    /// <summary>
    /// <see cref="HexGeometry"/> places every hex the upscalers sample, pin, and walk between, so it
    /// must agree exactly with the grid's own adjacency (<see cref="Hex.Directions_FlatTop"/>): any
    /// drift here would silently misplace a town or draw a crooked road.
    /// </summary>
    [TestClass]
    public class HexGeometryTests
    {
        [TestMethod]
        public void NearestHex_OfAHexCentre_IsThatHex()
        {
            for (int q = -3; q < 12; ++q)
            {
                for (int r = -3; r < 12; ++r)
                {
                    var (x, y) = HexGeometry.Centre(q, r);
                    Assert.AreEqual((q, r), HexGeometry.NearestHex(x, y), $"centre of ({q},{r}) should map back to it");
                }
            }
        }

        [TestMethod]
        public void DistanceSquared_IsTheSameToAllSixNeighbours()
        {
            // Every neighbour's centre is the same distance away on a regular hex grid - if the axis
            // weights were wrong, "nearest" would favour some directions over others.
            for (int q = 0; q < 10; ++q)
            {
                for (int r = 0; r < 10; ++r)
                {
                    var hex = new Hex(q, r);
                    for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                    {
                        var nbr = hex.Add(Hex.Directions_FlatTop[q & 1, dir]);
                        Assert.AreEqual(4.0, HexGeometry.DistanceSquared(hex, nbr), 1e-12, $"({q},{r}) dir {dir}");
                    }
                }
            }
        }

        [TestMethod]
        public void Scale_ByAWholeFactor_PutsNeighboursExactlyFactorHexesApart()
        {
            foreach (var hex in new[] { new Hex(3, 3), new Hex(4, 3) })
            {
                for (int factor = 2; factor <= 5; ++factor)
                {
                    var (q, r) = HexGeometry.Scale(hex.Q, hex.R, factor);
                    var scaled = new Hex(q, r);

                    for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
                    {
                        var nbr = hex.Add(Hex.Directions_FlatTop[hex.Q & 1, dir]);
                        var (nq, nr) = HexGeometry.Scale(nbr.Q, nbr.R, factor);

                        Assert.AreEqual(factor, scaled.GetDistance(new Hex(nq, nr)),
                            $"({hex.Q},{hex.R}) dir {dir} at factor {factor}");
                    }
                }
            }
        }

        [TestMethod]
        public void Translate_AcrossAnOddNumberOfColumns_KeepsTheNeighbourInTheSameDirection()
        {
            // Moving from an even column to an odd one is where adding offset differences directly
            // goes wrong, since odd columns sit half a row higher.
            var from = new Hex(2, 2);
            var to   = new Hex(5, 7);

            for (ushort dir = 0; dir < HexGridUtility.NEIGHBOURS_COUNT; ++dir)
            {
                var neighbourOfFrom = from.Add(Hex.Directions_FlatTop[from.Q & 1, dir]);
                var neighbourOfTo   = to.Add(Hex.Directions_FlatTop[to.Q & 1, dir]);

                Assert.AreEqual((neighbourOfTo.Q, neighbourOfTo.R), HexGeometry.Translate(neighbourOfFrom, from, to), $"dir {dir}");
            }
        }
    }
}
