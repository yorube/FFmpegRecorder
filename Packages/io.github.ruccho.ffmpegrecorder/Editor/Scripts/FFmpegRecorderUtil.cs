using System;
using UnityEditor.Media;
namespace Ruccho.FFmpegRecorder
{
    public static class FFmpegRecorderUtil
    {
        private static long GreatestCommonDivisor(long a, long b)
        {
            if (a == 0)
                return b;

            if (b == 0)
                return a;

            return (a < b) ? GreatestCommonDivisor(a, b % a) : GreatestCommonDivisor(b, a % b);
        }
        public static MediaRational RationalFromDouble(double value)
        {
            var integral = Math.Floor(value);
            var frac = value - integral;

            const long precision = 10000000;

            var gcd = GreatestCommonDivisor((long)Math.Round(frac * precision), precision);
            var denom = precision / gcd;

            return new MediaRational()
            {
                numerator = (int)((long)integral * denom + ((long)Math.Round(frac * precision)) / gcd),
                denominator = (int)denom
            };
        }
        public static double DoubleFromRational(MediaRational rational)
        {
            if (rational.denominator == 0)
            {
                return 0;
            }

            return rational.numerator / (float)rational.denominator;
        }
    }
}