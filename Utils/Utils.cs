
using NumbersGoUp.Models;

namespace NumbersGoUp.Utils
{
    public static class Utils
    {
        public static double SineReduce(this double degrees)
        {
            return Math.Sin(degrees * Math.PI / 180).DoubleReduce(1,-1);
        }
        public static float SingleCubeReduce(this double value, double max = 1.0, double min = 0.0, double increment = 1)
        {
            if (max == min) throw new DivideByZeroException();
            if (value < min) { value = min; }
            if (value > max) { value = max; }
            var normalized = (Math.Pow(value,3) - Math.Pow(min, 3)) / ((Math.Pow(max, 3) - Math.Pow(min, 3)) * increment);
            return increment == 1 ? Convert.ToSingle(normalized) : Convert.ToSingle(Math.Round(normalized));
        }
        public static double DoubleReduce(this double value, double max = 1.0, double min = 0.0)
        {
            if (max == min) throw new DivideByZeroException();
            if (max < min)
            {
                throw new ArgumentOutOfRangeException("max must be greater than min");
            }
            if (value < min) { value = min; }
            if (value > max) { value = max; }
            return (value - min) / (max - min);
        }
        public static double DoubleReduceSafe(this double value, double max = 1.0, double min = 0.0) => max == 0 && min == 0 ? 0 : value.DoubleReduce(max, min);
        public static double FibonacciReduce(this double value, double max = 1.0, double min = 0.0, double exp = 1, double radius = 0.2)
        {
            var x = value.DoubleReduce(max, min) * 1000;
            var result = 0.0;
            var prev = 1; var current = 1;
            while (current < 1000)
            {
                result = Math.Max(result, x.ZeroReduce(current * (1 + radius), current * (1 - radius)));

                var temp = current;
                current += prev;
                prev = temp;

            }
            return Math.Pow(result, exp);
        }
        private static double ShiftReduce(this double value, double outUpper, double outLower) => outLower + (value * (outUpper - outLower));
        public static double DoubleReduce(this double value, double max, double min, double outUpper, double outLower) => value.DoubleReduce(max,min).ShiftReduce(outUpper, outLower);
        public static double DoubleReduce(this float value, double max = 1.0, double min = 0.0) => DoubleReduce(Convert.ToDouble(value), max, min);
        public static double ZeroReduceSlow(this double value, double max = 1.0, double min = -1.0) => Math.Sqrt(1 - Math.Pow(value.DoubleReduce(max, min, 1, -1), 2));
        public static double ZeroReduceSlow(this double value, double max, double min, double outUpper, double outLower) => value.ZeroReduceSlow(max, min).ShiftReduce(outUpper, outLower);
        public static double ZeroReduce(this double value, double max = 1.0, double min = -1.0) => 1 - Math.Pow(value.DoubleReduce(max, min, 1, -1), 2);
        public static double ZeroReduce(this double value, double max, double min, double outUpper, double outLower) => value.ZeroReduce(max, min).ShiftReduce(outUpper, outLower);
        public static double ZeroReduceFast(this double value, double max = 1.0, double min = -1.0) => 1 - Math.Abs(value.DoubleReduce(max, min, 1, -1));
        public static double ZeroReduceFast(this double value, double max, double min, double outUpper, double outLower) => value.ZeroReduceFast(max, min).ShiftReduce(outUpper, outLower);
        public static float SingleReduce(this double value, double max = 1.0, double min = 0.0) => Convert.ToSingle(value.DoubleReduce(max, min));
        public static float SingleReduce(this float value, double max = 1.0, double min = 0.0) => SingleReduce(Convert.ToDouble(value), max, min);
        public static double? ToDouble(this decimal? value) => value.HasValue ? Convert.ToDouble(value.Value) : null;
        public static double ToDouble(this decimal value) => Convert.ToDouble(value);
        public static double Sqrt(this double value) => Math.Sqrt(value);
        public static double Square(this double value) => Math.Pow(value, 2);

        public static double Curve1(this double x, double exp, double cutoff = 1) => Math.Pow(x, exp);
        public static double Curve2(this double x, double exp, double cutoff = 1) => 1 - Math.Pow(Math.Pow(x * cutoff.CurveCoeff(exp), exp) - 1, 2);
        public static double Curve3(this double x, double exp, double cutoff = 1) => 1 + Math.Pow(Math.Pow(x * cutoff.CurveCoeff(exp), exp) - 1, 3);
        public static double Curve4(this double x, double exp, double cutoff = 1) => 1 - Math.Pow(Math.Pow(x * cutoff.CurveCoeff(exp), exp) - 1, 4);
        public static double Curve6(this double x, double exp, double cutoff = 1) => 1 - Math.Pow(Math.Pow(x * cutoff.CurveCoeff(exp), exp) - 1, 6);
        private static double CurveCoeff(this double cutoff, double exp) => 1 / Math.Pow(cutoff, exp);
        public static double WCurve(this double x, int peaks = 2) => (-0.5 * Math.Cos(peaks * 2 * Math.PI * x)) + 0.5;
        public static double WExpCurve(this double x, int peaks = 2) => ((-0.5 * Math.Cos(peaks * 2 * Math.PI * x)) + 0.5)*x;
        public static double VTailCurve(this double x, int peaks = 1) => (-0.5 * Math.Cos(((peaks * 2) + 1) * Math.PI * x)) + 0.5;
        public static double VTailExpCurve(this double x, int peaks = 1) => (-0.5 * Math.Cos(((peaks * 2) + 1) * Math.PI * x)) + 0.5;
        public static bool TickerAny(this string[] symbols, ITicker t) => symbols.Any(s => string.Equals(s, t.Symbol, StringComparison.InvariantCultureIgnoreCase));
        public static double ApplyAlma<T>(this T[] objsDesc, Func<T, double> objFn, double? sigma = null) where T:class
        {
            double WtdSum = 0, WtdNorm = 0;
            if (!sigma.HasValue)
            {
                sigma = objsDesc.Length * 0.6;
            }
            for (int i = 0; i < objsDesc.Length; i++)
            {
                double eq = Math.Exp(-1 * (Math.Pow(i, 2) / Math.Pow(sigma.Value, 2)));
                WtdSum = WtdSum + (eq * objFn(objsDesc[i]));
                WtdNorm = WtdNorm + eq;
            }
            return WtdSum / WtdNorm;
        }
        public static double ApplyAlma(this double[] valuesDesc, double? sigma = null)
        {
            double WtdSum = 0, WtdNorm = 0;
            if (!sigma.HasValue)
            {
                sigma = valuesDesc.Length * 0.6;
            }
            for (int i = 0; i < valuesDesc.Length; i++)
            {
                double eq = Math.Exp(-1 * (Math.Pow(i, 2) / Math.Pow(sigma.Value, 2)));
                WtdSum = WtdSum + (eq * valuesDesc[i]);
                WtdNorm = WtdNorm + eq;
            }
            return WtdSum / WtdNorm;
        }
        public static double CalculateVelocity<T>(this IEnumerable<T> barsDesc, Func<T, double> angleValueFn)
        {
            if (barsDesc.Count() < 2) { throw new Exception("Length does not meet minimum requirements to calculate velocity"); }
            return angleValueFn(barsDesc.First()) - angleValueFn(barsDesc.Last());
        }
        public static double CalculateAvgVelocity<T>(this T[] barsDesc, Func<T, double> angleValueFn)
        {
            if (barsDesc.Length < 2) { throw new Exception("Length does not meet minimum requirements to calculate velocity"); }
            double sum = 0;
            for(var i = 0; i < (barsDesc.Length-1); i++)
            {
                sum += angleValueFn(barsDesc[i]) - angleValueFn(barsDesc[i + 1]);
            }
            return sum / (barsDesc.Length - 1);
        }
        public static double CalculateVelocityAlma(this double[] barsDesc)
        {
            var size = barsDesc.Length - 1;
            if (size < 2) { throw new Exception("Length does not meet minimum requirements to calculate acceleration"); }
            var vels = new List<double>();
            for (var i = 0; i < size; i++)
            {
                vels.Add(barsDesc[i] - barsDesc[i + 1]);
            }
            return vels.ToArray().ApplyAlma();
        }
        public static double CalculateVelocityStDev<T>(this T[] barsDesc, Func<T, double> angleValueFn)
        {
            if (barsDesc.Length < 2) { throw new Exception("Length does not meet minimum requirements to calculate velocity"); }
            double sum = 0;
            for (var i = 0; i < (barsDesc.Length - 1); i++)
            {
                sum += angleValueFn(barsDesc[i]) - angleValueFn(barsDesc[i + 1]);
            }
            var avg = sum / (barsDesc.Length - 1); 
            sum = 0;
            for (var i = 0; i < (barsDesc.Length - 1); i++)
            {
                sum += Math.Pow(angleValueFn(barsDesc[i]) - angleValueFn(barsDesc[i + 1]) - avg, 2);
            }
            return Math.Sqrt(sum / (barsDesc.Length - 1));
        }
        public static double[] CalculateVelocities<T>(this T[] barsDesc, Func<T, double> valueFn)
        {
            var velocities = new double[barsDesc.Length - 1];
            for(var i = 0; i < velocities.Length; i++)
            {
                velocities[i] = valueFn(barsDesc[i]) - valueFn(barsDesc[i + 1]);
            }
            return velocities;
        }
        public static double CalculateAcceleration<T>(this IEnumerable<T> barsDesc, Func<T, double> angleValueFn)
        {
            var size = barsDesc.Count();
            if (size < 3) { throw new Exception("Length does not meet minimum requirements to calculate acceleration"); }
            return (angleValueFn(barsDesc.First()) - angleValueFn(barsDesc.Skip(size / 2).First())) - (angleValueFn(barsDesc.Skip(size / 2).First()) - angleValueFn(barsDesc.Last()));
        }
        public static double CalculateAcceleration(this double[] valuesDesc)
        {
            var size = valuesDesc.Length;
            if (size < 3) { throw new Exception("Length does not meet minimum requirements to calculate acceleration"); }
            return (valuesDesc[0] - valuesDesc[valuesDesc.Length / 2]) - (valuesDesc[valuesDesc.Length / 2] - valuesDesc[valuesDesc.Length - 1]);
        }
        public static double CalculateAvgAcceleration<T>(this T[] barsDesc, Func<T, double> valueFn)
        {
            var size = barsDesc.Length - 2;
            if (size < 1) { throw new Exception("Length does not meet minimum requirements to calculate acceleration"); }
            double sum = 0;
            for (var i = 0; i < size; i++)
            {
                sum += valueFn(barsDesc[i]) - valueFn(barsDesc[i + 1]) - valueFn(barsDesc[i + 1]) + valueFn(barsDesc[i + 2]);
            }
            return sum / size;
        }
        public static double CalculateAccelerationAlma(this double[] barsDesc)
        {
            var size = barsDesc.Length - 2;
            if (size < 2) { throw new Exception("Length does not meet minimum requirements to calculate acceleration"); }
            var accels = new List<double>();
            for (var i = 0; i < size; i++)
            {
                accels.Add(barsDesc[i] - barsDesc[i + 1] - barsDesc[i + 1] + barsDesc[i + 2]);
            }
            return accels.ToArray().ApplyAlma();
        }
        public static (double avg, double stdev) CalculateAvgStDev<T>(this IEnumerable<T> values, Func<T, double> valueFn)
        {
            var count = values?.Count() ?? 0;
            if (count == 0) return (0, 0);
            if (count == 1) return (valueFn(values.First()), 0);
            var avg = values.Average(v => valueFn(v));
            var stdev = Math.Sqrt(values.Sum(v => Math.Pow(valueFn(v) - avg, 2)) / count);
            return (avg, stdev);
        }
        public static double CalculateAvgStDevRatio(this IEnumerable<double> values)
        {
            var count = values.Count();
            if (count == 0) return 0;
            if (count == 1) return values.First();
            var avg = values.Average();
            return avg / Math.Sqrt(values.Select((v, i) =>
            {
                return Math.Pow(v - avg, 2);
            }).Sum() / count);
        }
        public static (double slope, double yintercept) CalculateRegression<T>(this T[] itemsAsc, Func<T, double> valueFn)
        {
            double x = 0.0, y = 0.0, xsqr = 0.0, xy = 0.0;
            for (var i = 1; i <= itemsAsc.Length; i++)
            {
                y += valueFn(itemsAsc[i - 1]);
                x += i;
                xsqr += Math.Pow(i, 2);
                xy += valueFn(itemsAsc[i - 1]) * i;
            }
            var regressionDenom = (itemsAsc.Length * xsqr) - Math.Pow(x, 2);
            var regressionSlope = regressionDenom != 0 ? ((itemsAsc.Length * xy) - (x * y)) / regressionDenom : 0.0;
            var yintercept = (y - (regressionSlope * x)) / itemsAsc.Length;
            return (regressionSlope, yintercept);
        }
        public static double RegressionStDev<T>(this T[] itemsAsc, Func<T,double> valueFn, double slope, double yintercept)
        {
            return Math.Sqrt(itemsAsc.Select((b, i) =>
            {
                var regression = (slope * i) + yintercept;
                return Math.Pow(valueFn(b) - regression, 2);
            }).Sum() / itemsAsc.Length);
        }
        public static double CalculateFutureRegressionStDevRatio<T>(this T[] itemsAsc, Func<T, double> valueFn, int offset)
        {
            var (slope, yintercept) = itemsAsc.CalculateRegression(valueFn);
            var stdev = itemsAsc.RegressionStDev(valueFn, slope, yintercept);
            var currentRegression = (slope * (itemsAsc.Length + offset)) + yintercept;
            return currentRegression / stdev;
        }
        public static double CalculateFutureRegression<T>(this T[] itemsAsc, Func<T, double> valueFn, int offset)
        {
            int index = itemsAsc.Length + offset;
            var (slope, yintercept) = itemsAsc.CalculateRegression(valueFn);
            return (slope * index) + yintercept;
        }
        public static (double slope, double yintercept) CalculateRegression(this double[] itemsAsc) => itemsAsc.CalculateRegression((x) => x);
        public static double CalculateFutureRegression(this double[] itemsAsc, int offset) => itemsAsc.CalculateFutureRegression((x) => x, offset);
        public static double CalculateFutureRegressionStDevRatio(this double[] itemsAsc, int offset) => itemsAsc.CalculateFutureRegressionStDevRatio(x => x, offset);
        public static double CalculateCovariance<T>(this T[] itemsAsc1, T[] itemsAsc2, Func<T,double> valueFn)
        {
            var avgChange1 = itemsAsc1.Average(i => valueFn(i));
            var avgChange2 = itemsAsc2.Average(i => valueFn(i));
            var minlength = Math.Min(itemsAsc1.Length, itemsAsc2.Length);
            if(minlength > 2)
            {
                var covValues = new double[minlength];
                for (var i = 0; i < minlength; i++)
                {
                    covValues[i] = (valueFn(itemsAsc1[i]) - avgChange1) * (valueFn(itemsAsc2[i]) - avgChange2);
                }

                return covValues.Average();
            }
            return -1;
        }
        public static double CalculateCovariance(this (double, double)[] items)
        {
            var avg1 = items.Select(i => i.Item1).Average();
            var avg2 = items.Select(i => i.Item2).Average();
            var covValues = new double[items.Length];
            for(var i = 0; i < items.Length; i++)
            {
                covValues[i] = (items[i].Item1 - avg1) * (items[i].Item2 - avg2);
            }
            return covValues.Average();
        }
        public static double CalculateCorrelation(this (double, double)[] items)
        {
            var avg1 = items.Select(i => i.Item1).Average();
            var avg2 = items.Select(i => i.Item2).Average();
            var covValues = new double[items.Length];
            var diffsqr1 = new double[items.Length];
            var diffsqr2 = new double[items.Length];
            for (var i = 0; i < items.Length; i++)
            {
                var diff1 = items[i].Item1 - avg1;
                var diff2 = items[i].Item2 - avg2;
                covValues[i] = diff1 * diff2;
                diffsqr1[i] = Math.Pow(diff1, 2);
                diffsqr2[i] = Math.Pow(diff2, 2);
            }
            var stdevs = Math.Sqrt(diffsqr1.Average()) * Math.Sqrt(diffsqr2.Average());
            return covValues.Average() / stdevs;
        }
        public static double KalmanValue(double[] valuesAsc, double[] errorDiffsAsc)
        {
            double xk = 0; double pk = 1; double kk; double zk; double r;
            for(var i = 0; i < valuesAsc.Length; i++)
            {
                r = errorDiffsAsc[i];
                zk = valuesAsc[i];
                kk = pk / (pk + r);
                pk = (1 - kk) * pk;
                xk = xk + (kk * (zk - xk));
            }
            return xk;
        }
        public static async Task<int> BatchJobs<T>(this IEnumerable<T> data, Func<T,Task> fn, int batchSize = 10)
        {
            var length = data.Count();
            var totalCount = 0;
            for (var i = 0; i < length; i += batchSize)
            {
                var batch = data.Skip(i).Take(batchSize);
                var tasks = new List<Task>();
                foreach (var entity in batch)
                {
                    var entityLocal = entity;
                    tasks.Add(fn(entityLocal));
                    totalCount++;
                }
                await Task.WhenAll(tasks);
            }
            return totalCount;
        }
        public static bool GetArgValue(string[] args, string key, out string value)
        {
            value = null;
            if (args.Length > 0)
            {
                foreach (var arg in args)
                {
                    var argParts = arg.Split('=');
                    if (argParts.Length == 2 && argParts[0] == key)
                    {
                        value = argParts[1];
                        return true;
                    }
                }
            }
            return false;
        }
        public static (double max, double min) MaxMin<T>(this IEnumerable<T> objs, Func<T, double> valueFn)
        {
            double max = double.MinValue;
            double min = double.MaxValue;
            foreach(var obj in objs)
            {
                var value = valueFn(obj);
                if(value > max) { max = value; }
                if(value < min) { min = value; }
            }
            return (max, min);
        }
    }
    public class MinMaxStore<T>
    {
        private Func<T, double> _perfFn;

        public double Max { get; set; }
        public double Min { get; set; }

        public MinMaxStore(Func<T, double> perfFn)
        {
            _perfFn = perfFn;
            Max = 0.0;
            Min = double.MaxValue;
        }
        public double Run(T data)
        {
            var result = _perfFn(data);
            Min = result < Min ? result : Min;
            Max = result > Max ? result : Max;
            return result;
        }

        public double Rank(T data) => _perfFn(data).DoubleReduce(Max, Min);
    }
}
