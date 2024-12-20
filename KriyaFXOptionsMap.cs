#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.DrawingTools;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Web.Script.Serialization; // For JavaScriptSerializer
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
using System.Net.WebSockets;
using System.Threading;
using System.IO;
#endregion

//This namespace holds Indicators in this folder and is required. Do not change it.
namespace NinjaTrader.NinjaScript.Indicators
{
    public class KriyaFXOptionsMap : Indicator
    {
        private HttpClient httpClient;
        private string bearerToken;
        private JavaScriptSerializer jsonSerializer;
        private List<double> strikeLevels = new List<double>();
        private List<double> indexStrikes = new List<double>();

        // SharpDX Resources
        private SharpDX.DirectWrite.TextFormat textFormat;
        private SharpDX.Direct2D1.Brush textBrush;
        private SharpDX.Direct2D1.Brush strikeLineBrush;

        // Mutex for thread safety
        private object renderLock = new object();

        private double futurePrice;
        private double indexPrice;
        private double currentRatio;

        private List<double> netAskVolumes = new List<double>();
        private List<double> callAskVolumes = new List<double>();
        private List<double> putAskVolumes = new List<double>();

        private double totalAskVolume;
        private double totalGexVolume;
        private SharpDX.DirectWrite.TextFormat tableTitleFormat;
        private SharpDX.DirectWrite.TextFormat tableContentFormat;
        private SharpDX.Direct2D1.Brush tableBorderBrush;
        private SharpDX.Direct2D1.Brush tableBackgroundBrush;

        private double expectedMove;
        private bool isLoggedIn = false;
        private bool isDataFetched = false;
        private long lastUpdateTimestamp;
        private string underlyingSymbol;

        private ClientWebSocket webSocket;
        private CancellationTokenSource cts;
        private List<string> messageChunks = new List<string>();
        private bool isExpectedMoveLevelsCalculated = false;
        private double fixedExpectedMaxPrice;
        private double fixedExpectedMinPrice;

        // Add these to your class-level private variables
        private List<double> callProbabilityOfTouch = new List<double>();
        private List<double> putProbabilityOfTouch = new List<double>();

        // Class to store net ask volume data per strike
        private class NetAskVolumeData
        {
            public double NetAskVolume { get; set; }
            public long NetAskVolumeTimestamp { get; set; }
        }

        // Dictionary to store previous net ask volume data per strike
        private Dictionary<double, NetAskVolumeData> previousNetAskVolumeData = new Dictionary<double, NetAskVolumeData>();

        // Variables to store previous total ask volume and timestamp
        private double previousTotalAskVolume = 0;
        private long previousTotalAskVolumeTimestamp = 0;

        private double maxGexLevel = 0;
        private double minGexLevel = 0;
        private bool isGexLevelsCalculated = false;
        private bool isRatioCalculated = false;
        protected override void OnStateChange()
        {
            try
            {
                if (State == State.SetDefaults)
                {
                    Description = @"Retrieves and plots Options Data from the KriyaFX service";
                    Name = "KriyaFXOptionsMap";
                    Calculate = Calculate.OnBarClose;
                    IsOverlay = true;
                    DisplayInDataBox = true;
                    DrawOnPricePanel = true;
                    DrawHorizontalGridLines = true;
                    DrawVerticalGridLines = true;
                    PaintPriceMarkers = true;
                    ScaleJustification = NinjaTrader.Gui.Chart.ScaleJustification.Right;
                    IsSuspendedWhileInactive = true;
                    SelectedSymbol = "SPY"; 
                    Username = string.Empty;
                    Password = string.Empty;
                    WebSocketUrl = "ws://kriyafx.de";
					StrikeMoneyThreshold = 8; 
					StrikeMoneyAlert = 5;
                    ShowProbabilityOfTouch = true;
                    ShowMaxGexLevels = true;
                    ShowExpectedMoveLevels = true;
                    Calculate = Calculate.OnEachTick;  // Change to calculate on each tick
					
                    Print("KriyaFXOptionsMap: SetDefaults completed");
                }
                else if (State == State.Configure)
                {
                    httpClient = new HttpClient();
                    jsonSerializer = new JavaScriptSerializer();
                    webSocket = new ClientWebSocket();
                    cts = new CancellationTokenSource();
                    Print("KriyaFXOptionsMap: Configure completed");
                }
                else if (State == State.DataLoaded)
                {
                    Print("KriyaFXOptionsMap: DataLoaded - Initiating login");
                    // Only initiate login here, not WebSocket connection
                    Task.Run(async () =>
                    {
                        try
                        {
                            await Login();
                        }
                        catch (Exception ex)
                        {
                            Print("Error in Login: " + ex.Message);
                        }
                    }).Wait();
                    Print("KriyaFXOptionsMap: DataLoaded - Login initiated");
                }
                else if (State == State.Historical)
                {
                    Print("KriyaFXOptionsMap: Historical state reached");
                }
                else if (State == State.Realtime)
                {
                    Print("KriyaFXOptionsMap: Realtime state reached - Initiating WebSocket connection");
                    // Initiate WebSocket connection here
                    Task.Run(async () =>
                    {
                        try
                        {
                            await ConnectWebSocket();
                        }
                        catch (Exception ex)
                        {
                            Print("Error in ConnectWebSocket: " + ex.Message);
                        }
                    }).Wait();
                }
                else if (State == State.Terminated)
                {
                    if (httpClient != null)
                    {
                        httpClient.Dispose();
                    }
                    DisposeSharpDXResources();
                    DisconnectWebSocket();
                    Print("KriyaFXOptionsMap: Terminated - Resources disposed");
                }
            }
            catch (Exception ex)
            {
                Print("Error in OnStateChange: " + ex.Message);
                if (ex.InnerException != null)
                {
                    Print("Inner Exception: " + ex.InnerException.Message);
                }
            }
        }

        protected override void OnBarUpdate() { }

        protected override void OnMarketData(MarketDataEventArgs marketDataUpdate)
        {
            if (marketDataUpdate.MarketDataType == MarketDataType.Ask)
            {
                futurePrice = marketDataUpdate.Price;
            }
        }

        private async Task<bool> Login()
        {
            try
            {
                Print("Attempting login...");

                var loginData = new
                {
                    username = Username,
                    password = Password
                };

                var content = new StringContent(jsonSerializer.Serialize(loginData), Encoding.UTF8, "application/json");
                var request = new HttpRequestMessage(HttpMethod.Post, "https://kriyafx.de/api/login")
                {
                    Content = content
                };

                var response = await httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    var tokenObject = jsonSerializer.Deserialize<Dictionary<string, object>>(responseContent);
                    bearerToken = tokenObject["token"].ToString();
                    isLoggedIn = true;
                    Print("Login successful. Bearer token received.");
                    return true;
                }
                else
                {
                    isLoggedIn = false;
                    Print("Login failed. Status code: " + response.StatusCode);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Print("Error during login: " + ex.Message);
                isLoggedIn = false;
                return false;
            }
        }

        private async Task InitializeConnectionAsync()
        {
            try
            {
                if (await Login())
                {
                    await ConnectWebSocket();
                }
                else
                {
                    Print("Failed to login. WebSocket connection not established.");
                }
            }
            catch (Exception ex)
            {
                Print("Error in InitializeConnectionAsync: " + ex.Message);
                if (ex.InnerException != null)
                {
                    Print("Inner Exception: " + ex.InnerException.Message);
                }
            }
        }
        private void UpdateRatio()
        {
            if (futurePrice > 0 && indexPrice > 0)
            {
                double newRatio = futurePrice / indexPrice;
                ratioHistory.Enqueue(newRatio);
                if (ratioHistory.Count > 100)
                    ratioHistory.Dequeue();

                currentRatio = ratioHistory.Average(); // Simple moving average of the ratio
            }
        }

        private void ProcessOptionsData(string json)
        {
            try
            {
                Print("Processing options data...");
                var data = jsonSerializer.Deserialize<Dictionary<string, object>>(json);

                if (!data.ContainsKey("Options"))
                {
                    Print("Options data not found in the response.");
                    isDataFetched = false;
                    return;
                }
                var options = data["Options"] as Dictionary<string, object>;
                if (options == null)
                {
                    Print("Options or Data not found in the response.");
                    isDataFetched = false;
                    return;
                }

                // Use the SelectedSymbol string to select the appropriate data
                string symbolKey = SelectedSymbol;
                if (!options.ContainsKey(symbolKey))
                {
                    Print("Data for "+symbolKey+" not found in the response.");
                    isDataFetched = false;
                    return;
                }

                var symbolData = options[symbolKey] as Dictionary<string, object>;
                if (symbolData == null || !symbolData.ContainsKey("Data"))
                {
                    Print(symbolKey +" data structure is invalid.");
                    isDataFetched = false;
                    return;
                }

                long currentUpdateTimestamp = 0;
                if (symbolData.ContainsKey("Updated"))
                {
                    currentUpdateTimestamp = Convert.ToInt64(symbolData["Updated"]);
                }

                // Only process the data if the timestamp has changed
                if (currentUpdateTimestamp != lastUpdateTimestamp)
                {
                    lastUpdateTimestamp = currentUpdateTimestamp;
                    Print("New update timestamp: " + lastUpdateTimestamp);

                    if (symbolData.ContainsKey("Symbol"))
                    {
                        underlyingSymbol = symbolData["Symbol"].ToString();
                    }

                    // Calculate future price and ratio
                    indexPrice = Convert.ToDouble(symbolData["Price"]);
                    if (!isRatioCalculated && futurePrice > 0 && indexPrice > 0)
                    {
                        UpdateRatio();
                        isRatioCalculated = true;
                    }               

                    double roundedIndexStrike = Math.Floor(indexPrice);

                    if (symbolData.ContainsKey("ExpectedMove"))
                    {
                        expectedMove = Convert.ToDouble(symbolData["ExpectedMove"]);

                        indexPrice = Convert.ToDouble(symbolData["Price"]);

                        if (!isExpectedMoveLevelsCalculated)
                        {
                            fixedExpectedMaxPrice = (indexPrice + expectedMove) * currentRatio;
                            fixedExpectedMinPrice = (indexPrice - expectedMove) * currentRatio;

                            isExpectedMoveLevelsCalculated = true;

                        }
                    }

                    var dataJson = jsonSerializer.Serialize(symbolData["Data"]);
                    var optionsData = jsonSerializer.Deserialize<List<Dictionary<string, object>>>(dataJson);

                    strikeLevels.Clear();
                    indexStrikes.Clear();
                    netAskVolumes.Clear();
                    callAskVolumes.Clear();
                    putAskVolumes.Clear();
                    callProbabilityOfTouch.Clear();
                    putProbabilityOfTouch.Clear();

                    // Calculate GEX levels
                    double maxGex = double.MinValue;
                    double minGex = double.MaxValue;
                    double maxGexStrike = 0;
                    double minGexStrike = 0;

                    foreach (var item in optionsData)
                    {
                        if (item.ContainsKey("strike") && item.ContainsKey("Net_ASK_Volume") &&
                            item.ContainsKey("call") && item.ContainsKey("put") && item.ContainsKey("Net_GEX_Volume"))
                        {
                            double indexStrike = Convert.ToDouble(item["strike"]);
                            double futureStrike = indexStrike * currentRatio;
                            double netAskVolume = Convert.ToDouble(item["Net_ASK_Volume"]);
                            var call = item["call"] as Dictionary<string, object>;
                            var put = item["put"] as Dictionary<string, object>;

                            double callAskVolume = call.ContainsKey("ASK_Volume") ? Convert.ToDouble(call["ASK_Volume"]) : 0;
                            double putAskVolume = put.ContainsKey("ASK_Volume") ? Convert.ToDouble(put["ASK_Volume"]) : 0;

                            // Add probability of touch processing
                            double callPoT = call.ContainsKey("ProbabilityOfTouch") ? Convert.ToDouble(call["ProbabilityOfTouch"]) : 0;
                            double putPoT = put.ContainsKey("ProbabilityOfTouch") ? Convert.ToDouble(put["ProbabilityOfTouch"]) : 0;

                            // Update the dictionaries with current values
                            previousNetAskVolumeData[futureStrike] = new NetAskVolumeData
                            {
                                NetAskVolume = netAskVolume,
                                NetAskVolumeTimestamp = lastUpdateTimestamp
                            };

                            double strike = Convert.ToDouble(item["strike"]);
                            double netGexVolume = Convert.ToDouble(item["Net_GEX_Volume"]);

                            if (netGexVolume > maxGex)
                            {
                                maxGex = netGexVolume;
                                maxGexStrike = strike;
                            }
                            if (netGexVolume < minGex)
                            {
                                minGex = netGexVolume;
                                minGexStrike = strike;
                            }

                            // Add all values to lists
                            indexStrikes.Add(indexStrike);
                            strikeLevels.Add(futureStrike);
                            netAskVolumes.Add(netAskVolume);
                            callAskVolumes.Add(callAskVolume);
                            putAskVolumes.Add(putAskVolume);
                            callProbabilityOfTouch.Add(callPoT);
                            putProbabilityOfTouch.Add(putPoT);
                        }
                    }

                    // After loop completes, set the levels
                    maxGexLevel = maxGexStrike * currentRatio;  // Apply the ratio like other price levels
                    minGexLevel = minGexStrike * currentRatio;
                    isGexLevelsCalculated = true;

                    if (symbolData.ContainsKey("Total_ASK_Volume"))
                    {
                        totalAskVolume = Convert.ToDouble(symbolData["Total_ASK_Volume"]);
                    }

                    if (options.ContainsKey("Total_GEX_Volume"))
                    {
                        totalGexVolume = Convert.ToDouble(symbolData["Total_GEX_Volume"]);
                    }

                    isDataFetched = true;

                    // Invalidate the chart to trigger a redraw
                    if (ChartControl != null)
                    {
                        ChartControl.Dispatcher.InvokeAsync(() =>
                        {
                            ChartControl.InvalidateVisual();
                            Print("Chart invalidated for redraw at " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                        });
                    }
                    else
                    {
                        Print("ChartControl is null, cannot invalidate");
                    }
                }
                else
                {
                    Print("Timestamp unchanged: " + currentUpdateTimestamp + ", skipping data processing");
                }
            }
            catch (Exception ex)
            {
                Print("Error processing options data: " + ex.Message);
                isDataFetched = false;
            }
        }

        private string FormatTimestamp(long unixTimestamp)
        {
            DateTimeOffset dateTimeOffset = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp);
            return dateTimeOffset.LocalDateTime.ToString("dd/MM/yy, HH:mm:ss");
        }

        public override void OnRenderTargetChanged()
        {
            // Dispose existing resources
            DisposeSharpDXResources();

            if (RenderTarget != null)
            {
                // Initialize SharpDX resources
                textFormat = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Arial", 12);
                tableTitleFormat = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Arial", 14);
                tableContentFormat = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Arial", 12);

                textBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.White);
                strikeLineBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.DodgerBlue);
                tableBorderBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.White);
                tableBackgroundBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color(0, 0, 0, 192)); // More opaque black (75% opacity)
            }
        }

        private void DisposeSharpDXResources()
        {
            if (textBrush != null) { textBrush.Dispose(); textBrush = null; }
            if (strikeLineBrush != null) { strikeLineBrush.Dispose(); strikeLineBrush = null; }
            if (textFormat != null) { textFormat.Dispose(); textFormat = null; }
            if (tableTitleFormat != null) { tableTitleFormat.Dispose(); tableTitleFormat = null; }
            if (tableContentFormat != null) { tableContentFormat.Dispose(); tableContentFormat = null; }
            if (tableBorderBrush != null) { tableBorderBrush.Dispose(); tableBorderBrush = null; }
            if (tableBackgroundBrush != null) { tableBackgroundBrush.Dispose(); tableBackgroundBrush = null; }
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            Print("OnRender started");

            if (RenderTarget == null || textFormat == null || textBrush == null || strikeLineBrush == null)
            {
                Print("OnRender - Essential resources are null");
                return;
            }

            Print("Calling PlotStrikeLevels");
            PlotStrikeLevels(chartControl, chartScale);

            Print("Calling DrawVolumeTable");
            DrawVolumeTable(chartControl);

            // Draw Expected Move levels
            Print("Calling ExpectedMoveLevels");

            if (isExpectedMoveLevelsCalculated && ShowExpectedMoveLevels)
            {
                DrawExpectedMoveLevel(chartControl, chartScale, fixedExpectedMaxPrice, "Expected Max Price");
                DrawExpectedMoveLevel(chartControl, chartScale, fixedExpectedMinPrice, "Expected Min Price");
            }

            // Draw GEX levels if enabled
            if (isGexLevelsCalculated && ShowMaxGexLevels)
            {
                DrawGexLevel(chartControl, chartScale, maxGexLevel, true);
                DrawGexLevel(chartControl, chartScale, minGexLevel, false);
            }

            Print("OnRender completed");
        }

        private SharpDX.Color GetColorForNetAskVolume(double netAskVolume, double callAskVolume, double putAskVolume)
        {
            double maxVolume = 3000000; // Adjust this value based on your typical volume range
            byte alpha = 64; // 50% opacity
            double strikeMoneyThresholdMillions = StrikeMoneyThreshold * 1000000;
            double strikeMoneyAlertMillions = StrikeMoneyAlert * 1000000;

            // Check if the dominant value is over the user-defined threshold
            double dominantValue = Math.Max(Math.Abs(callAskVolume), Math.Abs(putAskVolume));
            
            // Use logarithmic scale for better differentiation at higher values
            double logNormalizedVolume = Math.Log10(1 + (9 * Math.Min(1, dominantValue / maxVolume)));

            if (dominantValue > strikeMoneyThresholdMillions)
            {
                // Apply gradient based on the normalized net ask volume
                byte intensity = (byte)(64 + 191 * logNormalizedVolume); // Range from 64 to 255
                if (netAskVolume < 0) // Negative, use red gradient
                {
                    return new SharpDX.Color(intensity, (byte)0, (byte)0, alpha);
                }
                else // Positive, use green gradient
                {
                    return new SharpDX.Color((byte)0, intensity, (byte)0, alpha);
                }
            }
            else if (dominantValue > strikeMoneyAlertMillions && dominantValue < strikeMoneyThresholdMillions)
            {
                // Use yellow for values between strikeMoneyAlert and strikeMoneyThreshold
                byte intensity = (byte)(128 + 127 * logNormalizedVolume); // Range from 128 to 255
                return new SharpDX.Color(intensity, intensity, (byte)0, alpha); // Yellow gradient
            }
            else
            {
                // Use blue for all other cases
                byte intensity = (byte)(128 + 127 * logNormalizedVolume); // Range from 65 to 255
                return new SharpDX.Color((byte)65, (byte)105, intensity, alpha); // Royal Blue gradient
            }
        }

        private void PlotStrikeLevels(ChartControl chartControl, ChartScale chartScale)
        {
            Print("PlotStrikeLevels started");

            if (strikeLevels.Count == 0)
            {
                Print("No strike levels available. Returning.");
                return;
            }

            int minCount = Math.Min(strikeLevels.Count, indexStrikes.Count);
            minCount = Math.Min(minCount, netAskVolumes.Count);
            minCount = Math.Min(minCount, Math.Min(callAskVolumes.Count, putAskVolumes.Count));
            minCount = Math.Min(minCount, Math.Min(callProbabilityOfTouch.Count, putProbabilityOfTouch.Count));

            float xStart = ChartPanel.X;
            float xEnd = ChartPanel.X + ChartPanel.W;

            for (int i = 0; i < minCount; i++)
            {
                try
                {
                    double strikeLevel = strikeLevels[i];
                    double indexStrike = indexStrikes[i];
                    double netAskVolume = netAskVolumes[i];
                    float y = chartScale.GetYByValue(strikeLevel);
                    double callAskVolume = callAskVolumes[i];
                    double putAskVolume = putAskVolumes[i];
                    double callPoT = callProbabilityOfTouch[i];
                    double putPoT = putProbabilityOfTouch[i];

                    SharpDX.Color rectangleColor = GetColorForNetAskVolume(netAskVolume, callAskVolume, putAskVolume);

                    // Draw the line
                    using (var lineBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color((byte)65, (byte)105, (byte)225, (byte)64))) // Royal Blue with 50% opacity
                    {
                        RenderTarget.DrawLine(new SharpDX.Vector2(xStart, y), new SharpDX.Vector2(xEnd, y), lineBrush, 2);
                    }

                    // Calculate rectangle coordinates
                    float yTop = chartScale.GetYByValue(strikeLevel + 1);
                    float yBottom = chartScale.GetYByValue(strikeLevel - 1);

                    // Draw the rectangle
                    using (var rectangleBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, rectangleColor))
                    {
                        RenderTarget.FillRectangle(
                            new SharpDX.RectangleF(xStart, yTop, xEnd - xStart, yBottom - yTop),
                            rectangleBrush
                        );
                    }

                    // Prepare text for display
                    string strikeText = "Strike: " + Math.Round(indexStrike).ToString() + " (" + Math.Round(strikeLevel).ToString() + ")";
                    string callVolumeText = "Call $$ Vol: " + FormatVolume(callAskVolume);
                    string putVolumeText = "Put $$ Vol: " + FormatVolume(putAskVolume);
                    string netAskVolumeText = "Net $$ Vol: " + FormatVolume(netAskVolume);
                    string potText = ShowProbabilityOfTouch ? "PoT: " + Math.Round(callPoT, 1) + "%" : "";

                    using (var textBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.White))
                    using (var strikeTextLayout = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, strikeText, textFormat, float.MaxValue, float.MaxValue))
                    using (var callVolumeTextLayout = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, callVolumeText, textFormat, float.MaxValue, float.MaxValue))
                    using (var putVolumeTextLayout = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, putVolumeText, textFormat, float.MaxValue, float.MaxValue))
                    using (var netAskVolumeTextLayout = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, netAskVolumeText, textFormat, float.MaxValue, float.MaxValue))
                    using (var potTextLayout = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, potText, textFormat, float.MaxValue, float.MaxValue))
                    {
                        float strikeTextWidth = strikeTextLayout.Metrics.Width;
                        float strikeTextHeight = strikeTextLayout.Metrics.Height;
                        float callVolumeTextHeight = callVolumeTextLayout.Metrics.Height;
                        float putVolumeTextHeight = putVolumeTextLayout.Metrics.Height;
                        float netAskVolumeTextHeight = netAskVolumeTextLayout.Metrics.Height;
                        float potTextHeight = potTextLayout.Metrics.Height;

                        float x = (float)ChartPanel.X + (float)ChartPanel.W - Math.Max(
                            Math.Max(strikeTextWidth, 
                            Math.Max(callVolumeTextLayout.Metrics.Width, 
                            Math.Max(putVolumeTextLayout.Metrics.Width,
                            Math.Max(netAskVolumeTextLayout.Metrics.Width,
                            potTextLayout.Metrics.Width)))), 5);
                        
                        // Position text elements
                        float yStrikeText = y - 30;
                        float yNetAskVolumeText = yStrikeText + strikeTextHeight;
                        float yCallVolumeText = yNetAskVolumeText + netAskVolumeTextHeight;
                        float yPutVolumeText = yCallVolumeText + callVolumeTextHeight;
                        float yPotText = yPutVolumeText + putVolumeTextHeight;

                        RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, yStrikeText), strikeTextLayout, textBrush);
                        RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, yNetAskVolumeText), netAskVolumeTextLayout, textBrush);
                        RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, yCallVolumeText), callVolumeTextLayout, textBrush);
                        RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, yPutVolumeText), putVolumeTextLayout, textBrush);
                        if (ShowProbabilityOfTouch)
                        {
                            RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, yPotText), potTextLayout, textBrush);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Print("Error plotting strike level " + i + ": " + ex.Message);
                }
            }

            Print("PlotStrikeLevels completed");
        }

        private void DrawExpectedMoveLevel(ChartControl chartControl, ChartScale chartScale, double price, string label)
        {
            Print("Method DrawExpectedMoveLevel has been called");
            float y = chartScale.GetYByValue(price);
            float xStart = ChartPanel.X;
            float xEnd = ChartPanel.X + ChartPanel.W;

            // Draw the line
            using (var lineBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color((byte)255, (byte)255, (byte)0, (byte)64))) // Pale yellow with 50% opacity
            {
                RenderTarget.DrawLine(new SharpDX.Vector2(xStart, y), new SharpDX.Vector2(xEnd, y), lineBrush, 2);
            }

            // Draw the rectangle
            using (var rectangleBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color((byte)255, (byte)255, (byte)0, (byte)32))) // Pale yellow with 25% opacity
            {
                float yTop = chartScale.GetYByValue(price + 0.25);
                float yBottom = chartScale.GetYByValue(price - 0.25);
                RenderTarget.FillRectangle(new SharpDX.RectangleF(xStart, yTop, xEnd - xStart, yBottom - yTop), rectangleBrush);
            }

            // Draw label - matching GEX level text positioning
            using (var textBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.White))
            using (var textFormat = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Arial", 12))
            {
                RenderTarget.DrawText(label, textFormat, 
                    new SharpDX.RectangleF(xStart + 5, y - 15, 100, 20), textBrush);
            }

            Print("DrawExpectedMoveLevel completed for " + label);
        }

        private string FormatVolume(double volume)
        {
            string sign = volume < 0 ? "-" : "";
            return sign + "$" + String.Format("{0:N0}", Math.Abs(volume)).Replace(",", ".");
        }

        private void DrawVolumeTable(ChartControl chartControl)
        {
            Print("DrawVolumeTable started");

            float tableWidth = 300;
            float tableHeight = 150; // Increased height to accommodate the ratio row
            float padding = 10;
            float labelWidth = 120;
            float titleHeight = 30;
            float rowHeight = 20;

            // Use ChartPanel properties for positioning
            float x = ChartPanel.W - tableWidth - padding;
            float y = ChartPanel.H - tableHeight - padding;

            // Draw table background
            RenderTarget.FillRectangle(new SharpDX.RectangleF(x, y, tableWidth, tableHeight), tableBackgroundBrush);

            // Draw table border
            RenderTarget.DrawRectangle(new SharpDX.RectangleF(x, y, tableWidth, tableHeight), tableBorderBrush);

            // Prepare the text to display
            string statusText = null;
            if (!isLoggedIn)
            {
                statusText = "Login failed. Please check your credentials.";
            }
            else if (!isDataFetched)
            {
                statusText = "Failed to fetch options data. Please check your connection.";
            }
            else if (strikeLevels.Count == 0)
            {
                statusText = "No options data available.";
            }

            if (statusText != null)
            {
                // Draw status message instead of normal content
                // Center the title
                tableTitleFormat.TextAlignment = SharpDX.DirectWrite.TextAlignment.Center;
                RenderTarget.DrawText("Status", tableTitleFormat, new SharpDX.RectangleF(x, y, tableWidth, titleHeight), textBrush);
                RenderTarget.DrawText(statusText, tableContentFormat, new SharpDX.RectangleF(x + 5, y + titleHeight, tableWidth - 10, tableHeight - titleHeight), textBrush);
            }
            else
            {
                // Draw normal volume table content
                tableTitleFormat.TextAlignment = SharpDX.DirectWrite.TextAlignment.Center;
                RenderTarget.DrawText("Summary", tableTitleFormat, new SharpDX.RectangleF(x, y, tableWidth, titleHeight), textBrush);

                float contentY = y + titleHeight;
                float valueX = x + labelWidth;

                // Underlying Symbol
                RenderTarget.DrawText("Underlying:", tableContentFormat, new SharpDX.RectangleF(x + 5, contentY, labelWidth, rowHeight), textBrush);
                RenderTarget.DrawText(underlyingSymbol, tableContentFormat, new SharpDX.RectangleF(valueX, contentY, tableWidth - labelWidth - 5, rowHeight), textBrush);

                // Future/Index Ratio
                RenderTarget.DrawText("Ratio:", tableContentFormat, new SharpDX.RectangleF(x + 5, contentY + rowHeight, labelWidth, rowHeight), textBrush);
                RenderTarget.DrawText(currentRatio.ToString("F4"), tableContentFormat, new SharpDX.RectangleF(valueX, contentY + rowHeight, tableWidth - labelWidth - 5, rowHeight), textBrush);

                // Last Update Time
                RenderTarget.DrawText("Last Update:", tableContentFormat, new SharpDX.RectangleF(x + 5, contentY + 2 * rowHeight, labelWidth, rowHeight), textBrush);
                RenderTarget.DrawText(FormatTimestamp(lastUpdateTimestamp), tableContentFormat, new SharpDX.RectangleF(valueX, contentY + 2 * rowHeight, tableWidth - labelWidth - 5, rowHeight), textBrush);

                // Total Ask Volume
                RenderTarget.DrawText("Total Net $$ Vol:", tableContentFormat, new SharpDX.RectangleF(x + 5, contentY + 3 * rowHeight, labelWidth, rowHeight), textBrush);
                RenderTarget.DrawText(FormatVolumeForDisplay(totalAskVolume), tableContentFormat, new SharpDX.RectangleF(valueX, contentY + 3 * rowHeight, tableWidth - labelWidth - 5, rowHeight), textBrush);

                // Time Since Last Update
                RenderTarget.DrawText("Time Since Update:", tableContentFormat, new SharpDX.RectangleF(x + 5, contentY + 4 * rowHeight, labelWidth, rowHeight), textBrush);
                
                long currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long secondsSinceUpdate = currentTimestamp - lastUpdateTimestamp;
                string timeSinceUpdateStr = secondsSinceUpdate + " seconds";
                
                RenderTarget.DrawText(timeSinceUpdateStr, tableContentFormat, new SharpDX.RectangleF(valueX, contentY + 4 * rowHeight, tableWidth - labelWidth - 5, rowHeight), textBrush);
            }

            Print("DrawVolumeTable completed");
        }

        private string FormatVolumeForDisplay(double volume)
        {
            string sign = volume < 0 ? "-" : "";
            return sign + "$" + String.Format("{0:N0}", Math.Abs(volume)).Replace(",", ".");
        }

        private string FormatGexVolumeForDisplay(double gexVolume)
        {
            // GEX is in billions, so divide by 1 billion to get the number of billions
            double gexInBillions = gexVolume / 1000000000;
            string sign = gexInBillions < 0 ? "-" : "";
            return sign + "$" + String.Format("{0:F3}", Math.Abs(gexInBillions)) + " Bn";
        }

        private async Task ConnectWebSocket()
        {
            int maxRetries = 3;
            int retryDelay = 5000; // 5 seconds

            for (int i = 0; i < maxRetries; i++)
            {
                if (string.IsNullOrEmpty(bearerToken))
                {
                    Print("Bearer token is not set. Cannot connect to WebSocket.");
                    return;
                }

                Print("Attempting WebSocket connection (attempt " + (i + 1) + " of " + maxRetries + ")");
                try
                {
                    var wsUri = new Uri(WebSocketUrl + "?token=" + bearerToken);
                    await webSocket.ConnectAsync(wsUri, cts.Token);
                    Print("WebSocket connection established");
                    Task.Run(() => ReceiveWebSocketMessages());
                    return;
                }
                catch (Exception ex)
                {
                    Print("WebSocket connection attempt " + (i + 1) + " failed: " + ex.Message);
                }

                if (i < maxRetries - 1)
                {
                    Print("Retrying in " + (retryDelay / 1000) + " seconds...");
                    await Task.Delay(retryDelay);
                }
                else
                {
                    Print("Max retries reached. " + "WebSocket connection failed.");
                }
            }
        }

        private async Task ReceiveWebSocketMessages()
        {
            var buffer = new byte[1024 * 4];
            while (webSocket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                using (var ms = new MemoryStream())
                {
                    do
                    {
                        result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                        ms.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    ms.Seek(0, SeekOrigin.Begin);
                    using (var reader = new StreamReader(ms, Encoding.UTF8))
                    {
                        string message = await reader.ReadToEndAsync();
                        ProcessWebSocketMessage(message);
                    }
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cts.Token);
                    Print("WebSocket connection closed");
                    return;
                }
            }
        }

        private void ProcessWebSocketMessage(string message)
        {
            try
            {
                var data = jsonSerializer.Deserialize<Dictionary<string, object>>(message);
                if (data.ContainsKey("type") && data["type"].ToString() == "chunk")
                {
                    int chunkIndex = Convert.ToInt32(data["chunkIndex"]);
                    int totalChunks = Convert.ToInt32(data["totalChunks"]);
                    string chunkData = data["data"].ToString();

                    // Ensure the messageChunks list has enough capacity
                    while (messageChunks.Count <= chunkIndex)
                    {
                        messageChunks.Add(null);
                    }

                    messageChunks[chunkIndex] = chunkData;

                    if (messageChunks.Count(chunk => chunk != null) == totalChunks)
                    {
                        string fullMessage = string.Join("", messageChunks);
                        messageChunks.Clear(); // Reset for next message

                        ProcessReassembledMessage(fullMessage);
                    }
                }
                else
                {
                    // Handle other message types if any
                    Print("Received non-chunk message: " + message);
                    ProcessReassembledMessage(message);
                }
            }
            catch (Exception ex)
            {
                Print("Error processing WebSocket message: " + ex.Message);
            }
        }

        private void ProcessReassembledMessage(string fullMessage)
        {
            try
            {
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                Print("[" + timestamp + "] Received new message");

                var data = jsonSerializer.Deserialize<Dictionary<string, object>>(fullMessage);
                if (data.ContainsKey("type") && data["type"].ToString() == "update")
                {
                    Print("[" + timestamp + "] Received new options chain data");
                    if (data.ContainsKey("data"))
                    {
                        var optionsData = jsonSerializer.Serialize(data["data"]);
                        ProcessOptionsData(optionsData);
                    }
                }
            }
            catch (Exception ex)
            {
                Print("Error processing reassembled message: " + ex.Message);
            }
        }

        private void DisconnectWebSocket()
        {
            cts.Cancel();
            if (webSocket.State == WebSocketState.Open)
            {
                webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None).Wait();
            }
            webSocket.Dispose();
            cts.Dispose();
        }

        private void DrawGexLevel(ChartControl chartControl, ChartScale chartScale, double price, bool isMax)
        {
            Print("DrawGexLevel started for " + (isMax ? "Max" : "Min") + " GEX Level at " + price);

            float y = chartScale.GetYByValue(price);
            float xStart = ChartPanel.X;
            float xEnd = ChartPanel.X + ChartPanel.W;

            // Use green for max GEX, red for min GEX
            SharpDX.Color lineColor = isMax ? 
                new SharpDX.Color((byte)0, (byte)255, (byte)0, (byte)64) :  // Green with 25% opacity
                new SharpDX.Color((byte)255, (byte)0, (byte)0, (byte)64);   // Red with 25% opacity

            // Draw the line
            using (var lineBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, lineColor))
            {
                RenderTarget.DrawLine(new SharpDX.Vector2(xStart, y), new SharpDX.Vector2(xEnd, y), lineBrush, 2);
            }

            // Draw the rectangle
            using (var rectangleBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, 
                new SharpDX.Color(lineColor.R, lineColor.G, lineColor.B, (byte)32))) // 12.5% opacity
            {
                float yTop = chartScale.GetYByValue(price + 0.25);
                float yBottom = chartScale.GetYByValue(price - 0.25);
                RenderTarget.FillRectangle(new SharpDX.RectangleF(xStart, yTop, xEnd - xStart, yBottom - yTop), rectangleBrush);
            }

            // Draw label
            string label = isMax ? "Max GEX" : "Min GEX";
            using (var textBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.White))
            using (var textFormat = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Arial", 12))
            {
                RenderTarget.DrawText(label, textFormat, 
                    new SharpDX.RectangleF(xStart + 5, y - 15, 100, 20), textBrush);
            }
        }

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "Username", Order = 1, GroupName = "Parameters")]
        public string Username { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Password", Order = 2, GroupName = "Parameters")]
        public string Password { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "WebSocket URL", Order = 3, GroupName = "Parameters")]
        public string WebSocketUrl { get; set; }

        // Add properties for thresholds
        [NinjaScriptProperty]
        [Display(Name = "Dollar Threshold ($M)", Order = 4, GroupName = "Parameters")]
        public double StrikeMoneyThreshold { get; set; } 

        [NinjaScriptProperty]
        [Display(Name = "Net Difference Threshold ($M)", Order = 5, GroupName = "Parameters")]
        public double StrikeMoneyAlert { get; set; } 

        [NinjaScriptProperty]
        [Display(Name = "Options Symbol", Description = "Select SPX or SPY options data", Order = 1, GroupName = "Parameters")]
        public string SelectedSymbol { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Probability of Touch", Description = "Display PoT values next to volumes", Order = 6, GroupName = "Parameters")]
        public bool ShowProbabilityOfTouch { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show GEX Levels", Description = "Display maximum Gamma Exposure levels", Order = 7, GroupName = "Parameters")]
        public bool ShowMaxGexLevels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Expected Move Levels", Description = "Display Expected Move price levels", Order = 8, GroupName = "Parameters")]
        public bool ShowExpectedMoveLevels { get; set; }
        #endregion

        // Add this property override near the top of your class
        public override string DisplayName
        {
            get { return "KriyaFXOptionsMap (" + SelectedSymbol + ")"; }
        }

    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private KriyaFXOptionsMap[] cacheKriyaFXOptionsMap;
		public KriyaFXOptionsMap KriyaFXOptionsMap(string username, string password, string webSocketUrl, double strikeMoneyThreshold, double strikeMoneyAlert, string selectedSymbol, bool showProbabilityOfTouch, bool showMaxGexLevels, bool showExpectedMoveLevels)
		{
			return KriyaFXOptionsMap(Input, username, password, webSocketUrl, strikeMoneyThreshold, strikeMoneyAlert, selectedSymbol, showProbabilityOfTouch, showMaxGexLevels, showExpectedMoveLevels);
		}

		public KriyaFXOptionsMap KriyaFXOptionsMap(ISeries<double> input, string username, string password, string webSocketUrl, double strikeMoneyThreshold, double strikeMoneyAlert, string selectedSymbol, bool showProbabilityOfTouch, bool showMaxGexLevels, bool showExpectedMoveLevels)
		{
			if (cacheKriyaFXOptionsMap != null)
				for (int idx = 0; idx < cacheKriyaFXOptionsMap.Length; idx++)
					if (cacheKriyaFXOptionsMap[idx] != null && cacheKriyaFXOptionsMap[idx].Username == username && cacheKriyaFXOptionsMap[idx].Password == password && cacheKriyaFXOptionsMap[idx].WebSocketUrl == webSocketUrl && cacheKriyaFXOptionsMap[idx].StrikeMoneyThreshold == strikeMoneyThreshold && cacheKriyaFXOptionsMap[idx].StrikeMoneyAlert == strikeMoneyAlert && cacheKriyaFXOptionsMap[idx].SelectedSymbol == selectedSymbol && cacheKriyaFXOptionsMap[idx].ShowProbabilityOfTouch == showProbabilityOfTouch && cacheKriyaFXOptionsMap[idx].ShowMaxGexLevels == showMaxGexLevels && cacheKriyaFXOptionsMap[idx].ShowExpectedMoveLevels == showExpectedMoveLevels && cacheKriyaFXOptionsMap[idx].EqualsInput(input))
						return cacheKriyaFXOptionsMap[idx];
			return CacheIndicator<KriyaFXOptionsMap>(new KriyaFXOptionsMap(){ Username = username, Password = password, WebSocketUrl = webSocketUrl, StrikeMoneyThreshold = strikeMoneyThreshold, StrikeMoneyAlert = strikeMoneyAlert, SelectedSymbol = selectedSymbol, ShowProbabilityOfTouch = showProbabilityOfTouch, ShowMaxGexLevels = showMaxGexLevels, ShowExpectedMoveLevels = showExpectedMoveLevels }, input, ref cacheKriyaFXOptionsMap);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.KriyaFXOptionsMap KriyaFXOptionsMap(string username, string password, string webSocketUrl, double strikeMoneyThreshold, double strikeMoneyAlert, string selectedSymbol, bool showProbabilityOfTouch, bool showMaxGexLevels, bool showExpectedMoveLevels)
		{
			return indicator.KriyaFXOptionsMap(Input, username, password, webSocketUrl, strikeMoneyThreshold, strikeMoneyAlert, selectedSymbol, showProbabilityOfTouch, showMaxGexLevels, showExpectedMoveLevels);
		}

		public Indicators.KriyaFXOptionsMap KriyaFXOptionsMap(ISeries<double> input , string username, string password, string webSocketUrl, double strikeMoneyThreshold, double strikeMoneyAlert, string selectedSymbol, bool showProbabilityOfTouch, bool showMaxGexLevels, bool showExpectedMoveLevels)
		{
			return indicator.KriyaFXOptionsMap(input, username, password, webSocketUrl, strikeMoneyThreshold, strikeMoneyAlert, selectedSymbol, showProbabilityOfTouch, showMaxGexLevels, showExpectedMoveLevels);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.KriyaFXOptionsMap KriyaFXOptionsMap(string username, string password, string webSocketUrl, double strikeMoneyThreshold, double strikeMoneyAlert, string selectedSymbol, bool showProbabilityOfTouch, bool showMaxGexLevels, bool showExpectedMoveLevels)
		{
			return indicator.KriyaFXOptionsMap(Input, username, password, webSocketUrl, strikeMoneyThreshold, strikeMoneyAlert, selectedSymbol, showProbabilityOfTouch, showMaxGexLevels, showExpectedMoveLevels);
		}

		public Indicators.KriyaFXOptionsMap KriyaFXOptionsMap(ISeries<double> input , string username, string password, string webSocketUrl, double strikeMoneyThreshold, double strikeMoneyAlert, string selectedSymbol, bool showProbabilityOfTouch, bool showMaxGexLevels, bool showExpectedMoveLevels)
		{
			return indicator.KriyaFXOptionsMap(input, username, password, webSocketUrl, strikeMoneyThreshold, strikeMoneyAlert, selectedSymbol, showProbabilityOfTouch, showMaxGexLevels, showExpectedMoveLevels);
		}
	}
}

#endregion
