using System;

namespace CAIME.Upscaler
{
    /// <summary>
    /// Places hexes on a continuous plane so the grid can be scaled like an image: scale a hex's
    /// centre, then take whichever hex lies nearest the result. Positions are measured in columns
    /// across and rows up, with odd columns sitting half a row higher - the flat-top layout
    /// <see cref="Hex.Directions_FlatTop"/> describes.
    /// </summary>
    internal static class HexGeometry
    {
        /// <summary>
        /// Keeps a point that sits exactly between two hexes landing on the same side of the tie every
        /// time. Without it, floating-point noise from a fractional factor decides, and the blocks a
        /// nearest-neighbour fill produces come out ragged.
        /// </summary>
        private const double TIE_TOLERANCE = 1e-9;

        public static (double x, double y) Centre(int q, int r)
        {
            return (q, r + (0.5 * (q & 1)));
        }

        /// <summary>
        /// The hex whose centre is nearest the point, which is the hex the point falls inside. Only
        /// the two columns either side of the point can hold it, so each gets its nearest row and
        /// the closer of the two wins.
        /// </summary>
        public static (int q, int r) NearestHex(double x, double y)
        {
            int leftColumn = (int)Math.Floor(x);
            var left       = NearestInColumn(leftColumn, y);
            var right      = NearestInColumn(leftColumn + 1, y);

            return DistanceSquared(right, x, y) < DistanceSquared(left, x, y) - TIE_TOLERANCE ? right : left;
        }

        /// <summary>
        /// Where a hex lands on a grid <paramref name="factor"/> times larger.
        /// </summary>
        public static (int q, int r) Scale(int q, int r, double factor)
        {
            var (x, y) = Centre(q, r);
            return NearestHex(x * factor, y * factor);
        }

        /// <summary>
        /// The hex that sits relative to <paramref name="to"/> as <paramref name="hex"/> sits relative
        /// to <paramref name="from"/>. Adding the column and row differences directly would be wrong
        /// whenever the move crosses an odd number of columns, because odd columns sit half a row
        /// higher than even ones.
        /// </summary>
        public static (int q, int r) Translate(Hex hex, Hex from, Hex to)
        {
            var (hexX, hexY)   = Centre(hex.Q, hex.R);
            var (fromX, fromY) = Centre(from.Q, from.R);
            var (toX, toY)     = Centre(to.Q, to.R);

            return NearestHex(toX + (hexX - fromX), toY + (hexY - fromY));
        }

        /// <summary>
        /// Squared straight-line distance between two hex centres, for comparing which is nearer.
        /// Columns are 1.5 hex radii apart and rows √3 apart, so the two axes are weighted 3:4 (the
        /// true squared distance, scaled by 4/3 to keep the weights whole).
        /// </summary>
        public static double DistanceSquared(Hex a, Hex b)
        {
            var (bx, by) = Centre(b.Q, b.R);
            return DistanceSquared((a.Q, a.R), bx, by);
        }

        private static (int q, int r) NearestInColumn(int q, double y)
        {
            return (q, (int)Math.Floor(y - (0.5 * (q & 1)) + 0.5 + TIE_TOLERANCE));
        }

        private static double DistanceSquared((int q, int r) hex, double x, double y)
        {
            var (hexX, hexY) = Centre(hex.q, hex.r);
            double dx = hexX - x;
            double dy = hexY - y;
            return (3 * dx * dx) + (4 * dy * dy);
        }
    }
}
