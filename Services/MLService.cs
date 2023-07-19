using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.ML;
using Microsoft.ML.Data;
using NumbersGoUp.Models;
using NumbersGoUp.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace NumbersGoUp.Services
{
    public partial class MLService
    {
        public const int FEATURE_HISTORY = 350;
        private const int RELOAD_TIME_MONTHS = 6;
        private const int LOOKAHEAD = 60;
        private const int CROSS_VALIDATIONS = 3;
        private readonly ILogger<MLService> _logger;
        private readonly IAppCancellation _appCancellation;
        private readonly DataService _dataService;
        private readonly IBrokerService _brokerService;
        private readonly IStocksContextFactory _contextFactory;
        private readonly MLContext _mlContext;
        private readonly IMLFileService _mlFileService;
        private Task _startTask;
        private PredictionEngine<BarFeatures1, MLPrediction> _buyPredEngine;
        private PredictionEngine<BarFeatures1, MLPrediction> _sellPredEngine;
        private readonly SemaphoreSlim _semBar = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _taskSem = new SemaphoreSlim(1, 1);
        private const string MODEL_PREFIX = "MLModel";
        private static readonly string[] MODEL_TYPES = new[] { "Buy", "Sell" }; 

        public MLService(IAppCancellation appCancellation, ILogger<MLService> logger, DataService dataService, 
                            IBrokerService brokerService, IStocksContextFactory contextFactory, IMLFileService mlFileService)
        {
            _logger = logger;
            _appCancellation = appCancellation;
            _dataService = dataService;
            _brokerService = brokerService;
            _contextFactory = contextFactory;
            _mlContext = new MLContext();
            _mlFileService = mlFileService;
        }
        public async Task Init(DateTime date, bool forceRefresh = false)
        {
            try
            {
                var mlFiles = await GetFiles(date, forceRefresh);
                await mlFiles.ReadBuy((stream) =>
                {
                    var loadedModel = _mlContext.Model.Load(stream, out var modelInputSchema);
                    _buyPredEngine = _mlContext.Model.CreatePredictionEngine<BarFeatures1, MLPrediction>(loadedModel);
                    return Task.CompletedTask;
                });
                await mlFiles.ReadSell((stream) =>
                {
                    var loadedModel = _mlContext.Model.Load(stream, out var modelInputSchema);
                    _sellPredEngine = _mlContext.Model.CreatePredictionEngine<BarFeatures1, MLPrediction>(loadedModel);
                    return Task.CompletedTask;
                });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error starting MlService");
                throw;
            }
        }
        public async Task Ready()
        {
            if (_startTask == null)
            {
                await _taskSem.WaitAsync();
                try
                {
                    if (_startTask == null)
                    {
                        _startTask = Task.Run(async () => await Init(DateTime.UtcNow));
                    }
                }
                finally
                {
                    _taskSem.Release();
                }
            }
            await _startTask;
        }
        //public async Task<(MLPrediction buyPredict, MLPrediction sellPredict)> BarPredict(string symbol)
        //{
        //    BarMetric[] barMetrics;
        //    using (var stocksContext = _contextFactory.CreateDbContext())
        //    {
        //        barMetrics = await stocksContext.BarMetrics.Where(b => b.Symbol == symbol).OrderBy(b => b.BarDayMilliseconds)
        //                                                    .Take(FEATURE_HISTORY).Include(b => b.HistoryBar).ThenInclude(b => b.Ticker).ToArrayAsync(_appCancellation.Token);
        //    }
        //    if (barMetrics.Length != FEATURE_HISTORY)
        //    {
        //        _logger.LogError($"Bar metrics for {(barMetrics.Length > 0 ? barMetrics[0].Symbol : "NULL")} did not return the required history (retrieved {barMetrics.Length} results). returning default prediction");
        //        return (null, null);
        //    }
        //    var lastMarketDay = await _brokerService.GetLastMarketDay();
        //    if (barMetrics.First().BarDay.CompareTo(lastMarketDay) < 0)
        //    {
        //        _logger.LogError($"BarMetrics data for {symbol} isn't up to date! Returning default prediction.");
        //        return (null, null);
        //    }
        //    return await BarPredict(barMetrics);
        //}
        //public async Task<(MLPrediction buyPredict, MLPrediction sellPredict)> BarPredict(BarMetric barMetric)
        //{
        //    BarMetric[] barMetrics;
        //    using (var stocksContext = _contextFactory.CreateDbContext())
        //    {
        //        barMetrics = await stocksContext.BarMetrics.Where(b => b.Symbol == barMetric.Symbol && b.BarDayMilliseconds <= barMetric.BarDayMilliseconds)
        //                            .OrderBy(b => b.BarDayMilliseconds).Take(FEATURE_HISTORY).Include(b => b.HistoryBar).ThenInclude(b => b.Ticker).ToArrayAsync(_appCancellation.Token);
        //    }
        //    return await BarPredict(barMetrics);
        //}
        public async Task<(MLPrediction buyPredict, MLPrediction sellPredict)> BarPredict(BarMetric[] barMetrics)
        {
            if(_buyPredEngine == null || _sellPredEngine == null)
            {
                _logger.LogError($"Predition engine not initialized. Call {nameof(MLService)}.{nameof(Init)} method");
            }
            else if (barMetrics.Length == FEATURE_HISTORY)
            {
                await _semBar.WaitAsync(_appCancellation.Token);
                try
                {
                    var barFeatures = ToBarFeatures1(barMetrics);
                    return (_buyPredEngine.Predict(barFeatures), _sellPredEngine.Predict(barFeatures));
                }
                finally
                {
                    _semBar.Release();
                }
            }
            else
            {
                _logger.LogWarning($"Insufficient history to run predictions. Found {barMetrics.Length} bars. Required {FEATURE_HISTORY}");
            }
            return (null, null);
        }
        private async Task<IMLFiles> GetFiles(DateTime? date = null, bool forceRefresh = false)
        {
            var mlFiles = await _mlFileService.GetFiles(date, forceRefresh);
            if (mlFiles.RequiresRefresh)
            {
                await GenerateMLData(mlFiles, date);
            }
            return mlFiles;
        }
        private async Task GenerateMLData(IMLFiles mlFiles, DateTime? date = null)
        {
            var (buyFeatures, sellFeatures) = await WriteBarFeatures(date);
            _logger.LogInformation($"Generating ML data with {buyFeatures.Count()} features");
            var trainingDataViewBuy = _mlContext.Data.LoadFromEnumerable(buyFeatures);
            var trainingDataViewSell = _mlContext.Data.LoadFromEnumerable(sellFeatures);
            _logger.LogInformation($"Generating ML data with {buyFeatures.Count()} features");
            var trainedBuyModel = GenerateTransformerModel(trainingDataViewBuy);
            var trainedSellModel = GenerateTransformerModel(trainingDataViewSell);

            await mlFiles.WriteBuy((stream) =>
            {
                _mlContext.Model.Save(trainedBuyModel, trainingDataViewBuy.Schema, stream);
                return Task.CompletedTask;
            });
            await mlFiles.WriteSell((stream) =>
            {
                _mlContext.Model.Save(trainedSellModel, trainingDataViewSell.Schema, stream);
                return Task.CompletedTask;
            });
        }
        private ITransformer GenerateTransformerModel(IDataView trainingDataView, int? numberOfLeaves = null, int? minimumExampleCountPerLeaf = null)
        {
            var pipeline = _mlContext.Transforms.Concatenate("Features", BarFeatures1.Columns);
            var trainer = _mlContext.BinaryClassification.Trainers.LightGbm("Label", "Features", numberOfLeaves: numberOfLeaves, minimumExampleCountPerLeaf: minimumExampleCountPerLeaf);
            var trainingPipeline = pipeline.Append(trainer);
            return trainingPipeline.Fit(trainingDataView);
        }
        public async Task TestMLData()
        {
            int trainSize = 200000;
            DateTime? date = new DateTime(2020, 1, 11);
            var (buyFeatures, sellFeatures) = await WriteBarFeatures(date);
            _logger.LogInformation($"***Running default metrics***");
            MLMetrics(buyFeatures.Take(trainSize), buyFeatures.Skip(trainSize));
            MLMetrics(sellFeatures.Take(trainSize), sellFeatures.Skip(trainSize));
            //GeneratePFIData(buyFeatures.Take(trainSize), buyFeatures.Skip(trainSize), null);
        }
        public void MLMetrics(IEnumerable<BarFeatures1> trainFeatures, IEnumerable<BarFeatures1> testFeatures, int? leaves = null, int? minLeaves = null)
        {
            var pipeline = _mlContext.Transforms.Concatenate("Features", BarFeatures1.Columns);
            var trainer = _mlContext.BinaryClassification.Trainers.LightGbm("Label", "Features", numberOfLeaves: leaves, minimumExampleCountPerLeaf: minLeaves);
            var trainingPipeline = pipeline.Append(trainer);
            _logger.LogInformation($"Running training on {trainFeatures.Count()} features and testing on {testFeatures.Count()} features");
            var trainingDataView = _mlContext.Data.LoadFromEnumerable(trainFeatures);
            var testingDataView = _mlContext.Data.LoadFromEnumerable(testFeatures);
            //DataOperationsCatalog.TrainTestData dataSplit = _mlContext.Data.TrainTestSplit(trainingDataView, 0.2);
            var trainedModel = trainingPipeline.Fit(trainingDataView);
            CalibratedBinaryClassificationMetrics metrics = _mlContext.BinaryClassification.Evaluate(trainedModel.Transform(testingDataView), "Label");
            var s = $@"""
                    =============== Evaluating to get model's accuracy metrics ===============
                    *************************************************************************************************************
                    *       Metrics for Binary Classification model - Test Data     
                    *------------------------------------------------------------------------------------------------------------
                    """;
            Console.WriteLine($"=============== Evaluating to get model's accuracy metrics ===============");
            Console.WriteLine($"*************************************************************************************************************");
            Console.WriteLine($"*       Metrics for Binary Classification model - Test Data     ");
            Console.WriteLine($"*------------------------------------------------------------------------------------------------------------");
            Console.WriteLine($"*       Accuracy:    {metrics.Accuracy:#.###}");
            Console.WriteLine($"*       F1Score:    {metrics.F1Score:#.###}");
            Console.WriteLine($"*       AreaUnderRocCurve:    {metrics.AreaUnderRocCurve:#.###}");
            Console.WriteLine($"*       AreaUnderPrecisionRecallCurve:    {metrics.AreaUnderPrecisionRecallCurve:#.###}");
            Console.WriteLine(metrics.ConfusionMatrix.GetFormattedConfusionTable());
            Console.WriteLine($"*************************************************************************************************************");
        }
        static string[] SelectColumn(DataViewSchema.Column column, int vectorLength)
        {
            var type = column.Type.ToString();
            return type.StartsWith("Vector") ? Enumerable.Repeat(column.Name, vectorLength).Select((name, i) => $"{name}-{i}").ToArray() : new[] { column.Name };
        }
        public void GeneratePFIData(IEnumerable<BarFeatures1> trainFeatures, IEnumerable<BarFeatures1> testFeatures, int? leaves)
        {
            var trainingDataView = _mlContext.Data.LoadFromEnumerable(trainFeatures);
            var testDataView = _mlContext.Data.LoadFromEnumerable(testFeatures);
            string[] featureColumnNames = trainingDataView.Schema.Select(c => SelectColumn(c, BarFeatures1.VECTOR_LENGTH)).SelectMany(name => name).Where(columnName => columnName != "Label").ToArray();
            var pipeline = _mlContext.Transforms.Concatenate("Features", BarFeatures1.Columns);
            ITransformer dataPrepTransformer = pipeline.Fit(trainingDataView);
            IDataView preprocessedTrainData = dataPrepTransformer.Transform(trainingDataView);
            IDataView preprocessedTestData = dataPrepTransformer.Transform(testDataView);
            var trainer = _mlContext.BinaryClassification.Trainers.LightGbm("Label", "Features", numberOfLeaves: leaves);

            var trainedModel = trainer.Fit(preprocessedTrainData);

            //var cvResults = _mlContext.BinaryClassification.CrossValidateNonCalibrated(preprocessedTrainData, trainer, CROSS_VALIDATIONS);
            //foreach (var cvResult in cvResults)
            //{
            //    Console.WriteLine($"=============== Evaluating to get model's accuracy metrics ===============");
            //    Console.WriteLine($"*************************************************************************************************************");
            //    Console.WriteLine($"*       Metrics for Binary Classification model - Test Data     ");
            //    Console.WriteLine($"*------------------------------------------------------------------------------------------------------------");
            //    Console.WriteLine($"*       Accuracy:    {cvResult.Metrics.Accuracy:#.###}");
            //    Console.WriteLine($"*       F1Score:    {cvResult.Metrics.F1Score:#.###}");
            //    Console.WriteLine($"*       AreaUnderRocCurve:    {cvResult.Metrics.AreaUnderRocCurve:#.###}");
            //    Console.WriteLine($"*       AreaUnderPrecisionRecallCurve:    {cvResult.Metrics.AreaUnderPrecisionRecallCurve:#.###}");
            //    Console.WriteLine(cvResult.Metrics.ConfusionMatrix.GetFormattedConfusionTable());
            //    Console.WriteLine($"*************************************************************************************************************");
            //}

            var pfi = _mlContext.BinaryClassification.PermutationFeatureImportance(trainedModel, preprocessedTestData, permutationCount: 3);
            var featureImportanceMetrics = pfi.Select((metric, index) => new { index, metric.NegativeRecall })
                    .OrderByDescending(myFeatures => Math.Abs(myFeatures.NegativeRecall.Mean));

            Console.WriteLine("Feature\tPFI");

            foreach (var feature in featureImportanceMetrics)
            {
                Console.WriteLine($"{featureColumnNames[feature.index],-20}|\t{feature.NegativeRecall.Mean:F6}");
            }
        }

        private async Task<(IEnumerable<BarFeatures1> buyFeatures, IEnumerable<BarFeatures1> sellFeatures)> WriteBarFeatures(DateTime? endTime = null)
        {
            using var stocksContext = _contextFactory.CreateDbContext();
            var tickers = await stocksContext.Tickers.ToArrayAsync(_appCancellation.Token);
            var buyFeatureList = new List<BarFeatures1>();
            var sellFeatureList = new List<BarFeatures1>();
            foreach (var ticker in tickers)
            {
                IQueryable<BarMetric> barQuery;
                if (endTime.HasValue)
                {
                    var startMills = new DateTimeOffset(endTime.Value.AddYears(-DataService.LOOKBACK_YEARS)).ToUnixTimeMilliseconds();
                    var cutoffMillis = new DateTimeOffset(endTime.Value).ToUnixTimeMilliseconds()+1000;
                    barQuery = stocksContext.BarMetrics.Where(b => b.Symbol == ticker.Symbol && b.BarDayMilliseconds > startMills && b.BarDayMilliseconds < cutoffMillis);
                }
                else
                {
                    barQuery = stocksContext.BarMetrics.Where(b => b.Symbol == ticker.Symbol);
                }
                var barMetrics = await barQuery.Include(b => b.HistoryBar).OrderBy(b => b.BarDayMilliseconds).ToArrayAsync(_appCancellation.Token);
                var (buyFeatures, sellFeatures) = WriteBarFeatures(barMetrics);
                buyFeatureList.AddRange(buyFeatures);
                sellFeatureList.AddRange(sellFeatures);
            }
            return (buyFeatureList, sellFeatureList);
        }
        private (IEnumerable<BarFeatures1> buyFeatures, IEnumerable<BarFeatures1> sellFeatures) WriteBarFeatures(BarMetric[] barMetrics)
        {
            var buyFeatureList = new List<BarFeatures1>();
            var sellFeatureList = new List<BarFeatures1>();
            for (var i = 0; i < barMetrics.Length; i++)
            {
                if(i >= FEATURE_HISTORY)
                {
                    var futureBars = barMetrics.Skip(i).Take(LOOKAHEAD).ToArray();
                    if (futureBars.Any() && futureBars.Length == LOOKAHEAD)
                    {
                        var previousMetrics = barMetrics.Skip(i - FEATURE_HISTORY).Take(FEATURE_HISTORY).ToArray();

                        if (previousMetrics.Any())
                        {
                            var barFeatures = ToBarFeatures1(previousMetrics);
                            if (barFeatures != null)
                            {
                                var currentPrice = barMetrics[i].HistoryBar.Price();
                                var futurePrice = futureBars[futureBars.Length - 1].HistoryBar.Price();
                                var (regressionSlope, regressionIntercept) = futureBars.Select(b => b.HistoryBar.Price()).ToArray().CalculateRegression();
                                var regressionPrice = (regressionSlope * (futureBars.Length - 1)) + regressionIntercept;
                                barFeatures.Label = regressionSlope > 0 && futureBars.Last().SMASMA > 0;
                                buyFeatureList.Add(barFeatures);
                                sellFeatureList.Add(new BarFeatures1
                                {
                                    AlmaSMA1 = barFeatures.AlmaSMA1,
                                    SMASMA = barFeatures.SMASMA,
                                    MonthTrend = barFeatures.MonthTrend,
                                    Label = regressionSlope < 0,
                                    PredictBuy = barFeatures.PredictBuy,
                                    PredictSell = barFeatures.PredictSell,
                                    ProfitLossPerc = barFeatures.ProfitLossPerc,
                                    RegressionAngle = barFeatures.RegressionAngle,
                                    RegressionSlope = barFeatures.RegressionSlope,
                                    VolAlmaSMA = barFeatures.VolAlmaSMA,
                                    WeekTrend = barFeatures.WeekTrend,
                                    AlmaSMA1Performance = barFeatures.AlmaSMA1Performance,
                                    CloseAlmaSMA = barFeatures.CloseAlmaSMA,
                                    HighAlmaSMA = barFeatures.HighAlmaSMA,
                                    LowAlmaSMA = barFeatures.LowAlmaSMA,
                                    MaxMonthConsecutiveLosses = barFeatures.MaxMonthConsecutiveLosses,
                                    ProfitLossPerformance = barFeatures.ProfitLossPerformance,
                                    SMASMAPerformance = barFeatures.SMASMAPerformance,
                                    VelAlmaSMA = barFeatures.VelAlmaSMA
                                });
                            }
                        }
                    }
                }
            }
            return (buyFeatureList, sellFeatureList);
        }

        private static readonly double[] _gaussianWeights = Utils.Utils.GaussianWeights(24 * DataService.LOOKBACK_YEARS);
        public BarFeatures1 ToBarFeatures1(BarMetric[] barMetrics)
        {
            if(barMetrics.Length < FEATURE_HISTORY) { return null; }
            var barFeatures = new BarFeatures1();
            var currentBar = barMetrics[barMetrics.Length - 1];
            var currentMin = 0.0;
            var slopes = new List<double>();
            const int monthPeriod = 20;
            var initialPrice = barMetrics[0].HistoryBar.Price();
            double x = 0.0, y = 0.0, xsqr = 0.0, xy = 0.0;
            var maxMonthConsecutiveLosses = 0.0;
            var consecutiveLosses = 0;
            for (var i = 0; i < barMetrics.Length; i++)
            {
                if (i % monthPeriod == 0)
                {
                    var min = (barMetrics.Skip(i).Take(monthPeriod).Min(b => b.HistoryBar.LowPrice) - initialPrice) * 100 / initialPrice;
                    slopes.Add(min - currentMin);
                    currentMin = min;
                    consecutiveLosses = barMetrics.Skip(i).Take(monthPeriod).Average(b => b.SMASMA) > 0 ? 0 : (consecutiveLosses + 1);
                    maxMonthConsecutiveLosses = maxMonthConsecutiveLosses < consecutiveLosses ? consecutiveLosses : maxMonthConsecutiveLosses;
                }
                var perc = (barMetrics[i].HistoryBar.Price() - initialPrice) * 100 / initialPrice;
                y += perc;
                x += i;
                xsqr += Math.Pow(i, 2);
                xy += perc * i;
            }
            var regressionDenom = (barMetrics.Length * xsqr) - Math.Pow(x, 2);
            var regressionSlope = regressionDenom != 0 ? ((barMetrics.Length * xy) - (x * y)) / regressionDenom : 0.0;
            var yintercept = (y - (regressionSlope * x)) / barMetrics.Length;
            var regressionStDev = Math.Sqrt(barMetrics.Select((bar, i) =>
            {
                var perc = (bar.HistoryBar.Price() - initialPrice) * 100 / initialPrice;
                var regression = (regressionSlope * i) + yintercept;
                return Math.Pow(perc - regression, 2);
            }).Sum() / barMetrics.Length) * 2;
            var currentRegression = (regressionSlope * (barMetrics.Length - 1)) + yintercept;
            var currentPerc = (currentBar.HistoryBar.Price() - initialPrice) * 100 / initialPrice;
            barFeatures.RegressionAngle = Convert.ToSingle(Utils.Utils.GetAngle(currentPerc - currentRegression, regressionStDev));
            barFeatures.RegressionSlope = Convert.ToSingle(regressionSlope);
            if (slopes.Count > 0)
            {
                barFeatures.MonthTrend = Convert.ToSingle(slopes.Reverse<double>().ToArray().ApplyAlma(_gaussianWeights));
            }else { return null; }
            barFeatures.MaxMonthConsecutiveLosses = Convert.ToSingle(maxMonthConsecutiveLosses);
            var barMetricsDesc = barMetrics.Reverse().ToArray();
            barFeatures.PredictBuy = Convert.ToSingle(ManualPredict(barMetricsDesc, true));
            barFeatures.PredictSell = Convert.ToSingle(1 - ManualPredict(barMetricsDesc, false));
            var monthMetrics = barMetricsDesc.Take(20).ToArray();
            barFeatures.VelAlmaSMA = Convert.ToSingle(monthMetrics.CalculateVelocities(b => b.SMASMA).ApplyAlma(_gaussianWeights));
            barFeatures.AlmaSMA1 = Convert.ToSingle(currentBar.AlmaSMA1);
            //barFeatures.AlmaSMA3 = Convert.ToSingle(currentBar.AlmaSMA3);
            barFeatures.HighAlmaSMA = Convert.ToSingle(currentBar.HighAlmaSMA3);
            barFeatures.LowAlmaSMA = Convert.ToSingle(currentBar.LowAlmaSMA3);
            barFeatures.CloseAlmaSMA = Convert.ToSingle(currentBar.CloseAlmaSMA3);
            //barFeatures.OpenAlmaSMA = Convert.ToSingle(currentBar.OpenAlmaSMA3);
            barFeatures.ProfitLossPerc = Convert.ToSingle(currentBar.ProfitLossPerc);
            barFeatures.WeekTrend = Convert.ToSingle(currentBar.WeekTrend);
            barFeatures.VolAlmaSMA = Convert.ToSingle(currentBar.VolAlmaSMA);
            var AlmaSMA1Avg = monthMetrics.Average(b => b.AlmaSMA1);
            var AlmaSMA1StDev = Math.Sqrt(monthMetrics.Sum(b => Math.Pow(b.AlmaSMA1 - AlmaSMA1Avg, 2)) / monthMetrics.Length);
            barFeatures.AlmaSMA1Performance = Convert.ToSingle(AlmaSMA1StDev > 0 ? (Math.Pow(AlmaSMA1Avg, 2) / Math.Pow(AlmaSMA1StDev, 2)) : 0.0);
            var SMASMAAvg = barMetrics.Average(b => b.SMASMA);
            var SMASMAStDev = Math.Sqrt(barMetrics.Sum(b => Math.Pow(b.SMASMA - SMASMAAvg, 2)) / barMetrics.Length);
            barFeatures.SMASMAPerformance = Convert.ToSingle(SMASMAStDev > 0 ? (Math.Pow(SMASMAAvg, 2) / Math.Pow(SMASMAStDev, 2)) : 0.0);
            barFeatures.SMASMA = Convert.ToSingle(currentBar.SMASMA);
            var ProfitLossAvg = barMetrics.Average(b => b.ProfitLossPerc);
            var ProfitLossStDev = Math.Sqrt(barMetrics.Sum(b => Math.Pow(b.ProfitLossPerc - ProfitLossAvg, 2)) / barMetrics.Length);
            barFeatures.ProfitLossPerformance = Convert.ToSingle(ProfitLossStDev > 0 ? (Math.Pow(ProfitLossAvg, 2) / Math.Pow(ProfitLossStDev, 2)) : 0.0);
            return barFeatures;
        }

        private double ManualPredict(BarMetric[] barMetricsFull, bool buy)
        {
            var AlmaSma1Avg = barMetricsFull.Average(b => b.AlmaSMA1);
            var AlmaSma2Avg = barMetricsFull.Average(b => b.AlmaSMA2);
            var AlmaSma3Avg = barMetricsFull.Average(b => b.AlmaSMA3);
            var SMASMAAvg = barMetricsFull.Average(b => b.SMASMA);
            var AlmaSma1StDev = Math.Sqrt(barMetricsFull.Sum(b => Math.Pow(b.AlmaSMA1 - AlmaSma1Avg, 2)) / barMetricsFull.Length);
            var AlmaSma2StDev = Math.Sqrt(barMetricsFull.Sum(b => Math.Pow(b.AlmaSMA2 - AlmaSma2Avg, 2)) / barMetricsFull.Length);
            var AlmaSma3StDev = Math.Sqrt(barMetricsFull.Sum(b => Math.Pow(b.AlmaSMA3 - AlmaSma3Avg, 2)) / barMetricsFull.Length);
            var SMASMAStDev = Math.Sqrt(barMetricsFull.Sum(b => Math.Pow(b.SMASMA - SMASMAAvg, 2)) / barMetricsFull.Length);

            var AlmaVelStDev = (barMetricsFull.CalculateVelocityStDev(b => b.AlmaSMA1) + barMetricsFull.CalculateVelocityStDev(b => b.AlmaSMA2) + barMetricsFull.CalculateVelocityStDev(b => b.AlmaSMA3)) / 3;
            var SMAVelStDev = barMetricsFull.CalculateVelocityStDev(b => b.SMASMA);

            var barMetrics = barMetricsFull.Take(PredicterService.FEATURE_HISTORY_DAY).ToArray();

            double pricePrediction;

            if (buy)
            {
                var bullPricePrediction = ((
                                    (barMetrics[0].AlmaSMA1.ZeroReduce(AlmaSma1Avg + AlmaSma1StDev, AlmaSma1Avg - AlmaSma1StDev) * 0.2) +
                                    (barMetrics[0].AlmaSMA2.ZeroReduce(AlmaSma2Avg + AlmaSma2StDev, AlmaSma2Avg - AlmaSma2StDev) * 0.2) +
                                    (barMetrics[0].AlmaSMA3.ZeroReduce(AlmaSma3Avg + AlmaSma3StDev, AlmaSma3Avg - AlmaSma3StDev) * 0.2)
                                  ) * barMetrics.CalculateAvgVelocity(b => b.AlmaSMA3).DoubleReduce(0, -AlmaVelStDev)) +
                                  ((1 - barMetrics[0].SMASMA.DoubleReduce(SMASMAAvg, SMASMAAvg - SMASMAStDev)) * 0.15) +
                                  (barMetrics.CalculateAvgVelocity(b => b.SMASMA).ZeroReduce(SMAVelStDev, -SMAVelStDev) * 0.1) +
                                  (barMetrics[0].WeekTrend.DoubleReduce(1, -1) * 0.15);

                var bearPricePrediction = ((
                                    ((1 - barMetrics[0].AlmaSMA1.DoubleReduce(AlmaSma1Avg, AlmaSma1Avg - (AlmaSma1StDev * 1.5))) * 0.2) +
                                    ((1 - barMetrics[0].AlmaSMA2.DoubleReduce(AlmaSma2Avg, AlmaSma2Avg - (AlmaSma2StDev * 1.5))) * 0.2) +
                                    ((1 - barMetrics[0].AlmaSMA3.DoubleReduce(AlmaSma3Avg, AlmaSma3Avg - (AlmaSma3StDev * 1.5))) * 0.2)
                                  ) * barMetrics.CalculateAvgVelocity(b => b.AlmaSMA3).DoubleReduce(0.5 * AlmaVelStDev, -AlmaVelStDev)) +
                                  ((1 - barMetrics[0].SMASMA.DoubleReduce(SMASMAAvg, SMASMAAvg - (SMASMAStDev * 1.5))) * 0.15) +
                                  (barMetrics.CalculateAvgVelocity(b => b.SMASMA).ZeroReduce(0.5 * SMAVelStDev, -0.5 * SMAVelStDev) * 0.1) +
                                  (barMetrics[0].WeekTrend.DoubleReduce(2, -1) * 0.15);

                var coeff = barMetrics.Average(b => b.SMASMA).DoubleReduce(SMASMAAvg, SMASMAAvg - SMASMAStDev) * barMetrics.CalculateAvgVelocity(b => b.SMASMA).DoubleReduce(0.5 * SMAVelStDev, -SMAVelStDev);
                pricePrediction = (coeff * bullPricePrediction) + ((1 - coeff) * bearPricePrediction);
            }
            else
            {
                var bullPricePrediction = ((
                                              ((1 - barMetrics[0].AlmaSMA1.ZeroReduce(AlmaSma1Avg + (AlmaSma1StDev * 2), AlmaSma1Avg - AlmaSma1StDev)) * barMetrics[0].AlmaSMA1.DoubleReduce(AlmaSma1Avg - AlmaSma1StDev, AlmaSma1Avg - (AlmaSma1StDev * 2)) * 0.2) +
                                              ((1 - barMetrics[0].AlmaSMA2.ZeroReduce(AlmaSma2Avg + (AlmaSma2StDev * 2), AlmaSma2Avg - AlmaSma2StDev)) * barMetrics[0].AlmaSMA2.DoubleReduce(AlmaSma2Avg - AlmaSma2StDev, AlmaSma2Avg - (AlmaSma2StDev * 2)) * 0.2) +
                                              ((1 - barMetrics[0].AlmaSMA3.ZeroReduce(AlmaSma3Avg + (AlmaSma3StDev * 2), AlmaSma3Avg - AlmaSma3StDev)) * barMetrics[0].AlmaSMA3.DoubleReduce(AlmaSma3Avg - AlmaSma3StDev, AlmaSma3Avg - (AlmaSma3StDev * 2)) * 0.2)
                                          ) * (1 - barMetrics.CalculateAvgVelocity(b => b.AlmaSMA3).DoubleReduce(AlmaVelStDev, 0))) +
                                        (barMetrics[0].SMASMA.DoubleReduce(SMASMAAvg + (SMASMAStDev * 1.5), SMASMAAvg) * 0.15) +
                                        (barMetrics.CalculateAvgVelocity(b => b.SMASMA).ZeroReduce(0.5 * SMAVelStDev, -0.5 * SMAVelStDev) * 0.1) +
                                        ((1 - barMetrics[0].WeekTrend.DoubleReduce(1, -2)) * 0.15);

                var bearPricePrediction = ((
                                              (barMetrics[0].AlmaSMA1.DoubleReduce(AlmaSma1Avg + (AlmaSma1StDev * 1.5), AlmaSma1Avg) * 0.2) +
                                              (barMetrics[0].AlmaSMA2.DoubleReduce(AlmaSma2Avg + (AlmaSma2StDev * 1.5), AlmaSma2Avg) * 0.2) +
                                              (barMetrics[0].AlmaSMA3.DoubleReduce(AlmaSma3Avg + (AlmaSma3StDev * 1.5), AlmaSma3Avg) * 0.2)
                                          ) * (1 - barMetrics.CalculateAvgVelocity(b => b.AlmaSMA3).DoubleReduce(AlmaVelStDev, 0))) +
                                        (barMetrics[0].SMASMA.DoubleReduce(SMASMAAvg + SMASMAStDev, SMASMAAvg) * 0.15) +
                                        (barMetrics.CalculateAvgVelocity(b => b.SMASMA).ZeroReduce(SMAVelStDev, -SMAVelStDev) * 0.1) +
                                        ((1 - barMetrics[0].WeekTrend.DoubleReduce(1, -1)) * 0.15);

                var coeff = barMetrics.Average(b => b.SMASMA).DoubleReduce(SMASMAAvg, SMASMAAvg - SMASMAStDev) * barMetrics.CalculateAvgVelocity(b => b.SMASMA).DoubleReduce(0.5 * SMAVelStDev, -SMAVelStDev);
                pricePrediction = (coeff * bullPricePrediction) + ((1 - coeff) * bearPricePrediction);
            }

            return pricePrediction;
        }
    }
    public class BarFeatures1
    {
        public const int VECTOR_LENGTH = 8;
        public static readonly string[] Columns = new string[] {
            nameof(RegressionAngle), nameof(RegressionSlope), nameof(MonthTrend), nameof(ProfitLossPerformance), nameof(MaxMonthConsecutiveLosses), 
            nameof(AlmaSMA1), nameof(AlmaSMA1Performance), nameof(HighAlmaSMA), nameof(CloseAlmaSMA), nameof(LowAlmaSMA), nameof(VelAlmaSMA), 
            nameof(SMASMA), nameof(SMASMAPerformance), nameof(ProfitLossPerc), nameof(WeekTrend), nameof(VolAlmaSMA), nameof(PredictBuy), nameof(PredictSell),
        };
        public float RegressionAngle { get; set; }
        public float RegressionSlope { get; set; }
        public float MonthTrend { get; set; }
        public float ProfitLossPerformance { get; set; }
        public float MaxMonthConsecutiveLosses { get; set; }
        public float AlmaSMA1 { get; set; }
        public float AlmaSMA1Performance { get; set; }
        public float HighAlmaSMA { get; set; }
        public float CloseAlmaSMA { get; set; }
        public float LowAlmaSMA { get; set; }
        //public float OpenAlmaSMA { get; set; }
        public float VelAlmaSMA { get; set; }
        public float SMASMA { get; set; }
        public float SMASMAPerformance { get; set; }
        public float ProfitLossPerc { get; set; }
        public float WeekTrend { get; set; }
        public float VolAlmaSMA { get; set; }
        public float PredictBuy { get; set; }
        public float PredictSell { get; set; }
        public bool Label { get; set; }
        public float Weight { get; set; }
    }
    public class MLPrediction
    {
        [ColumnName("PredictedLabel")]

        public bool Positive;

        public float Probability { get; set; }
    }
    public interface IMLFileService
    {       
        /**
         * returns true if refresh of ML data file is required. false otherwise.
         */
        Task<IMLFiles> GetFiles(DateTime? date, bool forceRefresh);
    }
    public interface IMLFiles
    {
        public bool RequiresRefresh { get; }
        Task ReadBuy(Func<Stream, Task> readerFn);
        Task WriteBuy(Func<Stream, Task> writerFn);
        Task ReadSell(Func<Stream, Task> readerFn);
        Task WriteSell(Func<Stream, Task> writerFn);
    }
}