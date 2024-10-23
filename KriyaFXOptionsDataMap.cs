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
        private double currentPrice;

        // SharpDX Resources
        private SharpDX.DirectWrite.TextFormat textFormat;
        private SharpDX.Direct2D1.Brush textBrush;
        private SharpDX.Direct2D1.Brush strikeLineBrush;

        // Mutex for thread safety
        private object renderLock = new object();

        private double futurePrice;
        private double indexPrice;

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
        private double expectedMaxPrice;
        private double expectedMinPrice;
        private bool isLoggedIn = false;
        private bool isDataFetched = false;
        private long lastUpdateTimestamp;
        private string underlyingSymbol;

        private ClientWebSocket webSocket;
        private CancellationTokenSource cts;
        private string authToken;

        private List<string> messageChunks = new List<string>();

        private bool isRatioCalculated = false;
        private double fixedRatio;
        private bool isExpectedMoveLevelsCalculated = false;
        private double fixedExpectedMaxPrice;
        private double fixedExpectedMinPrice;

        // Class to store net ask volume data per strike
        private class NetAskVolumeData
        {
            public double NetAskVolume { get; set; }
            public long NetAskVolumeTimestamp { get; set; }
            public double Velocity { get; set; }
            public long VelocityTimestamp { get; set; }
        }

        // Dictionary to store previous net ask volume data per strike
        private Dictionary<double, NetAskVolumeData> previousNetAskVolumeData = new Dictionary<double, NetAskVolumeData>();

        // Lists to store velocities and accelerations for each strike level
        private List<double> velocitiesList = new List<double>();
        private List<double> accelerationsList = new List<double>();

        // Variables to store total velocity and acceleration
        private double totalVelocity = 0;
        private double totalAcceleration = 0;

        // Variables to store previous total ask volume and timestamp
        private double previousTotalAskVolume = 0;
        private long previousTotalAskVolumeTimestamp = 0;

        // Variables to store previous total velocity and timestamp
        private double previousTotalVelocity = 0;
        private long previousTotalVelocityTimestamp = 0;

        // Dictionaries to store EMA values per strike
        private Dictionary<double, double> emaVelocity = new Dictionary<double, double>();
        private Dictionary<double, double> emaAcceleration = new Dictionary<double, double>();

        // Variables for EMA of total velocity and acceleration
        private double emaTotalVelocity = 0;
        private double emaTotalAcceleration = 0;
        private bool isTotalVelocityInitialized = false;
        private bool isTotalAccelerationInitialized = false;

        // Smoothing factor for EMA
        private double smoothingFactor = 0.4; // Adjust between 0 and 1 as needed

        private double totalLiquidity;
        private List<double> netLiquidityList = new List<double>();

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
                    Username = string.Empty;
                    Password = string.Empty;
                    WebSocketUrl = "ws://localhost:3000";
                    SmoothingFactor = 0.5; // Default value
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

        private double GetFuturePriceAtTimestamp(long timestamp)
        {
            // Convert Unix timestamp to DateTime
            var barTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(timestamp).ToLocalTime();

            // Get the bar index for the specified time
            int barIndex = BarsArray[0].GetBar(barTime);

            if (barIndex >= 0)
            {
                // Return the ask price of the bar
                return Bars.GetAsk(barIndex);
            }
            else
            {
                // If no exact match is found, manually search for the closest bar before the specified time
                for (int i = BarsArray[0].Count - 1; i >= 0; i--)
                {
                    if (BarsArray[0].GetTime(i) <= barTime)
                    {
                        return Bars.GetAsk(i);
                    }
                }

                // If still no match, return the current ask price as a fallback
                Print("No historical data found for the specified timestamp. Using current ask price.");
                return GetCurrentAsk();
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
                if (options == null || !options.ContainsKey("Data"))
                {
                    Print("Options or Data not found in the response.");
                    isDataFetched = false;
                    return;
                }

                long currentUpdateTimestamp = 0;
                if (options.ContainsKey("Updated"))
                {
                    currentUpdateTimestamp = Convert.ToInt64(options["Updated"]);
                }

                // Only process the data if the timestamp has changed
                if (currentUpdateTimestamp != lastUpdateTimestamp)
                {
                    lastUpdateTimestamp = currentUpdateTimestamp;
                    Print("New update timestamp: " + lastUpdateTimestamp);

                    if (options.ContainsKey("Symbol"))
                    {
                        underlyingSymbol = options["Symbol"].ToString();
                    }

                    // Calculate future price and ratio only if not already calculated
                    if (!isRatioCalculated)
                    {
                        futurePrice = GetFuturePriceAtTimestamp(lastUpdateTimestamp);
                        var dateTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(lastUpdateTimestamp).ToLocalTime();

                        indexPrice = Convert.ToDouble(data["Price"]);
                        fixedRatio = futurePrice / indexPrice;
                        isRatioCalculated = true;

                        Print("Initial Future Price (at timestamp): " + futurePrice);
                        Print("Initial Index Price: " + indexPrice);
                        Print("Fixed Index to Futures Ratio: " + fixedRatio);
                    }
                    else
                    {
                        // Use the fixed ratio for calculations, but update the index price
                        indexPrice = Convert.ToDouble(data["Price"]);
                        futurePrice = indexPrice * fixedRatio;
                    }

                    double roundedIndexStrike = Math.Floor(indexPrice);

                    if (data.ContainsKey("ExpectedMove"))
                    {
                        expectedMove = Convert.ToDouble(data["ExpectedMove"]);

                        indexPrice = Convert.ToDouble(data["Price"]);

                        if (!isExpectedMoveLevelsCalculated)
                        {
                            fixedExpectedMaxPrice = (indexPrice + expectedMove) * fixedRatio;
                            fixedExpectedMinPrice = (indexPrice - expectedMove) * fixedRatio;

                            isExpectedMoveLevelsCalculated = true;

                        }
                    }

                    var dataJson = jsonSerializer.Serialize(options["Data"]);
                    var optionsData = jsonSerializer.Deserialize<List<Dictionary<string, object>>>(dataJson);

                    velocitiesList.Clear();
                    accelerationsList.Clear();
                    netLiquidityList.Clear();
                    strikeLevels.Clear();
                    indexStrikes.Clear();
                    netAskVolumes.Clear();
                    callAskVolumes.Clear();
                    putAskVolumes.Clear();

                    foreach (var item in optionsData)
                    {
                        if (item.ContainsKey("strike") && item.ContainsKey("Net_ASK_Volume") &&
                            item.ContainsKey("call") && item.ContainsKey("put"))
                        {
                            double indexStrike = Convert.ToDouble(item["strike"]);
                            double futureStrike = indexStrike * fixedRatio;
                            double netAskVolume = Convert.ToDouble(item["Net_ASK_Volume"]);
                            var call = item["call"] as Dictionary<string, object>;
                            var put = item["put"] as Dictionary<string, object>;

                            double callAskVolume = call.ContainsKey("ASK_Volume") ? Convert.ToDouble(call["ASK_Volume"]) : 0;
                            double putAskVolume = put.ContainsKey("ASK_Volume") ? Convert.ToDouble(put["ASK_Volume"]) : 0;
                            
                            // Default values if not present
                            double netLiquidity = item.ContainsKey("Net_Liquidity") ? Convert.ToDouble(item["Net_Liquidity"]) : 0;
                            
                            // Compute Velocity and Acceleration
                            double smoothedVelocity = 0;
                            double smoothedAcceleration = 0;
                            
                            if (previousNetAskVolumeData.ContainsKey(futureStrike))
                            {
                                NetAskVolumeData previousData = previousNetAskVolumeData[futureStrike];
                                double previousNetAskVolume = previousData.NetAskVolume;
                                long previousNetAskVolumeTimestamp = previousData.NetAskVolumeTimestamp;

                                if (lastUpdateTimestamp > previousNetAskVolumeTimestamp)
                                {
                                    double deltaV = netAskVolume - previousNetAskVolume;
                                    double deltaT = lastUpdateTimestamp - previousNetAskVolumeTimestamp;

                                    if (deltaT > 0)
                                    {
                                        double velocity = deltaV / deltaT;

                                        double previousVelocity = previousData.Velocity;
                                        long previousVelocityTimestamp = previousData.VelocityTimestamp;
                                        double deltaVelocityTime = lastUpdateTimestamp - previousVelocityTimestamp;

                                        if (deltaVelocityTime > 0)
                                        {
                                            double acceleration = (velocity - previousVelocity) / deltaVelocityTime;

                                            // Apply EMA to Velocity
                                            if (emaVelocity.ContainsKey(futureStrike))
                                            {
                                                double previousEmaVelocity = emaVelocity[futureStrike];
                                                smoothedVelocity = (velocity * smoothingFactor) + (previousEmaVelocity * (1 - smoothingFactor));
                                            }
                                            else
                                            {
                                                // First time, initialize EMA with the current velocity
                                                smoothedVelocity = velocity;
                                            }
                                            emaVelocity[futureStrike] = smoothedVelocity;

                                            // Apply EMA to Acceleration
                                            if (emaAcceleration.ContainsKey(futureStrike))
                                            {
                                                double previousEmaAcceleration = emaAcceleration[futureStrike];
                                                smoothedAcceleration = (acceleration * smoothingFactor) + (previousEmaAcceleration * (1 - smoothingFactor));
                                            }
                                            else
                                            {
                                                // First time, initialize EMA with the current acceleration
                                                smoothedAcceleration = acceleration;
                                            }
                                            emaAcceleration[futureStrike] = smoothedAcceleration;
                                        }
                                    }
                                }
                            }

                            // Print updated velocity and acceleration
                            Print("Strike: " + futureStrike +
                                  " | Timestamp: " + lastUpdateTimestamp +
                                  " | Smoothed Velocity: " + smoothedVelocity +
                                  " | Smoothed Acceleration: " + smoothedAcceleration);

                            // Update the dictionaries with current values
                            previousNetAskVolumeData[futureStrike] = new NetAskVolumeData
                            {
                                NetAskVolume = netAskVolume,
                                NetAskVolumeTimestamp = lastUpdateTimestamp,
                                Velocity = smoothedVelocity,
                                VelocityTimestamp = lastUpdateTimestamp
                            };

                            // Add all values to lists, using default values if necessary
                            indexStrikes.Add(indexStrike);
                            strikeLevels.Add(futureStrike);
                            netAskVolumes.Add(netAskVolume);
                            callAskVolumes.Add(callAskVolume);
                            putAskVolumes.Add(putAskVolume);
                            velocitiesList.Add(smoothedVelocity);
                            accelerationsList.Add(smoothedAcceleration);
                            netLiquidityList.Add(netLiquidity);
                        }
                    }

                    // --- Adjusted Code for Total Velocity and Acceleration ---
                    if (options.ContainsKey("Total_ASK_Volume"))
                    {
                        totalAskVolume = Convert.ToDouble(options["Total_ASK_Volume"]);

                        if (previousTotalAskVolumeTimestamp == 0)
                        {
                            // First time, initialize previous values
                            previousTotalAskVolume = totalAskVolume;
                            previousTotalAskVolumeTimestamp = lastUpdateTimestamp;

                            totalVelocity = 0;
                            totalAcceleration = 0;

                            previousTotalVelocity = 0;
                            previousTotalVelocityTimestamp = lastUpdateTimestamp;

                            // Initialize EMA
                            emaTotalVelocity = 0;
                            emaTotalAcceleration = 0;
                            isTotalVelocityInitialized = true;
                            isTotalAccelerationInitialized = true;
                        }
                        else if (lastUpdateTimestamp > previousTotalAskVolumeTimestamp)
                        {
                            double deltaV = totalAskVolume - previousTotalAskVolume;
                            double deltaT = lastUpdateTimestamp - previousTotalAskVolumeTimestamp;

                            if (deltaT > 0)
                            {
                                double rawTotalVelocity = deltaV / deltaT;
                                double rawTotalAcceleration = 0;

                                if (lastUpdateTimestamp > previousTotalVelocityTimestamp)
                                {
                                    double deltaVelocity = rawTotalVelocity - previousTotalVelocity;
                                    double deltaTVelocity = lastUpdateTimestamp - previousTotalVelocityTimestamp;

                                    if (deltaTVelocity > 0)
                                    {
                                        rawTotalAcceleration = deltaVelocity / deltaTVelocity;
                                    }
                                }

                                // Apply EMA to totalVelocity
                                if (isTotalVelocityInitialized)
                                {
                                    emaTotalVelocity = (rawTotalVelocity * smoothingFactor) + (emaTotalVelocity * (1 - smoothingFactor));
                                }
                                else
                                {
                                    emaTotalVelocity = rawTotalVelocity;
                                    isTotalVelocityInitialized = true;
                                }
                                totalVelocity = emaTotalVelocity;

                                // Apply EMA to totalAcceleration
                                if (isTotalAccelerationInitialized)
                                {
                                    emaTotalAcceleration = (rawTotalAcceleration * smoothingFactor) + (emaTotalAcceleration * (1 - smoothingFactor));
                                }
                                else
                                {
                                    emaTotalAcceleration = rawTotalAcceleration;
                                    isTotalAccelerationInitialized = true;
                                }
                                totalAcceleration = emaTotalAcceleration;

                                // Update previous total velocity and timestamp
                                previousTotalVelocity = rawTotalVelocity;
                                previousTotalVelocityTimestamp = lastUpdateTimestamp;
                            }

                            // Update previous total ask volume and timestamp
                            previousTotalAskVolume = totalAskVolume;
                            previousTotalAskVolumeTimestamp = lastUpdateTimestamp;
                        }
                        else
                        {
                            // Timestamp has not advanced; do nothing
                        }

                        // Print updated total velocity and acceleration
                        Print("Total | Timestamp: " + lastUpdateTimestamp +
                              " | Smoothed Total Velocity: " + totalVelocity +
                              " | Smoothed Total Acceleration: " + totalAcceleration);
                    }
                    // --- End of Adjusted Code ---

                    if (options.ContainsKey("Total_GEX_Volume"))
                    {
                        totalGexVolume = Convert.ToDouble(options["Total_GEX_Volume"]);
                    }

                    if (options.ContainsKey("Total_Liquidity"))
                    {
                        totalLiquidity = Convert.ToDouble(options["Total_Liquidity"]);
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

            if (isExpectedMoveLevelsCalculated)
            {
                DrawExpectedMoveLevel(chartControl, chartScale, fixedExpectedMaxPrice, "Expected Max Price");
                DrawExpectedMoveLevel(chartControl, chartScale, fixedExpectedMinPrice, "Expected Min Price");
            }
            else
            {
                Print("Expected move levels not yet calculated");
            }

            Print("OnRender completed");
        }

        private SharpDX.Color GetColorForNetAskVolume(double netAskVolume)
        {
            // Define the range for color interpolation
            double maxVolume = 30000; // Adjust this value based on your typical volume range

            // Normalize the netAskVolume to a value between -1 and 1
            double normalizedVolume = Math.Max(-1, Math.Min(1, netAskVolume / maxVolume));

            byte alpha = 64; // 50% opacity

            if (Math.Abs(normalizedVolume) < 0.1) // Close to zero, use a more vibrant blue
            {
                return new SharpDX.Color((byte)65, (byte)105, (byte)225, alpha); // Royal Blue
            }
            else if (normalizedVolume < 0) // Negative, use red gradient
            {
                byte intensity = (byte)(255 * -normalizedVolume);
                return new SharpDX.Color(intensity, (byte)0, (byte)0, alpha);
            }
            else // Positive, use green gradient
            {
                byte intensity = (byte)(255 * normalizedVolume);
                return new SharpDX.Color((byte)0, intensity, (byte)0, alpha);
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

            int minCount = Math.Min(
                Math.Min(strikeLevels.Count, indexStrikes.Count),
                Math.Min(netAskVolumes.Count, velocitiesList.Count)
            );
            minCount = Math.Min(minCount, Math.Min(accelerationsList.Count, netLiquidityList.Count));
            minCount = Math.Min(minCount, Math.Min(callAskVolumes.Count, putAskVolumes.Count));

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
                    // double velocity = velocitiesList[i];  // Commented out
                    // double acceleration = accelerationsList[i];  // Commented out
                    double netLiquidity = netLiquidityList[i];
                    double callAskVolume = callAskVolumes[i];
                    double putAskVolume = putAskVolumes[i];

                    SharpDX.Color rectangleColor = GetColorForNetAskVolume(netAskVolume);

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
                    string netLiquidityText = "Net Liquidity: " + FormatLiquidity(netLiquidity);

                    // Comment out velocity and acceleration
                    // string velocityText = "Velocity: " + FormatRate(velocity);
                    // string accelerationText = "Acceleration: " + FormatAcceleration(acceleration);

                    using (var textBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.White))
                    using (var strikeTextLayout = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, strikeText, textFormat, float.MaxValue, float.MaxValue))
                    using (var callVolumeTextLayout = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, callVolumeText, textFormat, float.MaxValue, float.MaxValue))
                    using (var putVolumeTextLayout = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, putVolumeText, textFormat, float.MaxValue, float.MaxValue))
                    using (var netAskVolumeTextLayout = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, netAskVolumeText, textFormat, float.MaxValue, float.MaxValue))
                    using (var netLiquidityTextLayout = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, netLiquidityText, textFormat, float.MaxValue, float.MaxValue))
                    {
                        float strikeTextWidth = strikeTextLayout.Metrics.Width;
                        float strikeTextHeight = strikeTextLayout.Metrics.Height;
                        float callVolumeTextHeight = callVolumeTextLayout.Metrics.Height;
                        float putVolumeTextHeight = putVolumeTextLayout.Metrics.Height;
                        float netAskVolumeTextHeight = netAskVolumeTextLayout.Metrics.Height;
                        float netLiquidityTextHeight = netLiquidityTextLayout.Metrics.Height;

                        float x = (float)ChartPanel.X + (float)ChartPanel.W - Math.Max(strikeTextWidth, 
                            Math.Max(callVolumeTextLayout.Metrics.Width, 
                            Math.Max(putVolumeTextLayout.Metrics.Width, 
                            Math.Max(netAskVolumeTextLayout.Metrics.Width, 
                            netLiquidityTextLayout.Metrics.Width)))) - 5;
                        
                        // Position text elements
                        float yStrikeText = y - 30 ;
                        float yCallVolumeText = yStrikeText + strikeTextHeight;
                        float yPutVolumeText = yCallVolumeText + callVolumeTextHeight;
                        float yNetAskVolumeText = yPutVolumeText + putVolumeTextHeight;
                        float yNetLiquidityText = yNetAskVolumeText + netAskVolumeTextHeight;

                        RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, yStrikeText), strikeTextLayout, textBrush);
                        RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, yCallVolumeText), callVolumeTextLayout, textBrush);
                        RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, yPutVolumeText), putVolumeTextLayout, textBrush);
                        RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, yNetAskVolumeText), netAskVolumeTextLayout, textBrush);
                        RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, yNetLiquidityText), netLiquidityTextLayout, textBrush);
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
            Print("DrawExpectedMoveLevel started for " + label + " Price: " + price);

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

            Print("DrawExpectedMoveLevel completed for " + label);
        }

        private string FormatVolume(double volume)
        {
            // Convert the volume to hundreds
            double volumeInHundreds = volume * 100;
            string sign = volumeInHundreds < 0 ? "-" : "";
            return sign + "$" + String.Format("{0:N0}", Math.Abs(volumeInHundreds)).Replace(",", ".");
        }

        private string FormatRate(double rate)
        {
            string sign = rate < 0 ? "-" : "";
            return sign + "$" + String.Format("{0:N2}", Math.Abs(rate)).Replace(",", ".") + "/s";
        }

        private string FormatAcceleration(double acceleration)
        {
            string sign = acceleration < 0 ? "-" : "";
            return sign + "$" + String.Format("{0:N2}", Math.Abs(acceleration)).Replace(",", ".") + "/s²";
        }

        private void DrawVolumeTable(ChartControl chartControl)
        {
            Print("DrawVolumeTable started");

            float tableWidth = 300;
            float tableHeight = 170; // Increased height to accommodate the new row
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
                // Center the title
                tableTitleFormat.TextAlignment = SharpDX.DirectWrite.TextAlignment.Center;
                RenderTarget.DrawText("Summary", tableTitleFormat, new SharpDX.RectangleF(x, y, tableWidth, titleHeight), textBrush);

                // Draw table content
                float contentY = y + titleHeight;
                float valueX = x + labelWidth;

                // Underlying Symbol
                RenderTarget.DrawText("Underlying:", tableContentFormat, new SharpDX.RectangleF(x + 5, contentY, labelWidth, rowHeight), textBrush);
                RenderTarget.DrawText(underlyingSymbol, tableContentFormat, new SharpDX.RectangleF(valueX, contentY, tableWidth - labelWidth - 5, rowHeight), textBrush);

                // Last Update Time
                RenderTarget.DrawText("Last Update:", tableContentFormat, new SharpDX.RectangleF(x + 5, contentY + 1 * rowHeight, labelWidth, rowHeight), textBrush);
                RenderTarget.DrawText(FormatTimestamp(lastUpdateTimestamp), tableContentFormat, new SharpDX.RectangleF(valueX, contentY + 1 * rowHeight, tableWidth - labelWidth - 5, rowHeight), textBrush);

                // Total Ask Volume
                RenderTarget.DrawText("Total Net $$ Vol:", tableContentFormat, new SharpDX.RectangleF(x + 5, contentY + 2 * rowHeight, labelWidth, rowHeight), textBrush);
                RenderTarget.DrawText(FormatVolumeForDisplay(totalAskVolume), tableContentFormat, new SharpDX.RectangleF(valueX, contentY + 2 * rowHeight, tableWidth - labelWidth - 5, rowHeight), textBrush);

                // Total Velocity
                RenderTarget.DrawText("Total Velocity:", tableContentFormat, new SharpDX.RectangleF(x + 5, contentY + 3 * rowHeight, labelWidth, rowHeight), textBrush);
                RenderTarget.DrawText(FormatRate(totalVelocity), tableContentFormat, new SharpDX.RectangleF(valueX, contentY + 3 * rowHeight, tableWidth - labelWidth - 5, rowHeight), textBrush);

                // Total Acceleration
                RenderTarget.DrawText("Total Acceleration:", tableContentFormat, new SharpDX.RectangleF(x + 5, contentY + 4 * rowHeight, labelWidth, rowHeight), textBrush);
                RenderTarget.DrawText(FormatAcceleration(totalAcceleration), tableContentFormat, new SharpDX.RectangleF(valueX, contentY + 4 * rowHeight, tableWidth - labelWidth - 5, rowHeight), textBrush);

                // Total Liquidity
                RenderTarget.DrawText("Total Liquidity:", tableContentFormat, new SharpDX.RectangleF(x + 5, contentY + 5 * rowHeight, labelWidth, rowHeight), textBrush);
                RenderTarget.DrawText(FormatLiquidity(totalLiquidity), tableContentFormat, new SharpDX.RectangleF(valueX, contentY + 5 * rowHeight, tableWidth - labelWidth - 5, rowHeight), textBrush);
            }

            Print("DrawVolumeTable completed");
        }

        // Add this method to format the liquidity value
        private string FormatLiquidity(double liquidity)
        {
            return "$"+ liquidity.ToString("F2");
        }

        private string FormatVolumeForDisplay(double volume)
        {
            // Convert the volume to hundreds
            double volumeInHundreds = volume * 100;
            string sign = volumeInHundreds < 0 ? "-" : "";
            return sign + "$" + String.Format("{0:N0}", Math.Abs(volumeInHundreds)).Replace(",", ".");
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

        [NinjaScriptProperty]
        [Range(0, 1)]
        [Display(Name = "Smoothing Factor", Description = "EMA smoothing factor (0-1)", Order = 4, GroupName = "Parameters")]
        public double SmoothingFactor { get; set; }
        #endregion

    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private KriyaFXOptionsMap[] cacheKriyaFXOptionsMap;
		public KriyaFXOptionsMap KriyaFXOptionsMap(string username, string password, string webSocketUrl, double smoothingFactor)
		{
			return KriyaFXOptionsMap(Input, username, password, webSocketUrl, smoothingFactor);
		}

		public KriyaFXOptionsMap KriyaFXOptionsMap(ISeries<double> input, string username, string password, string webSocketUrl, double smoothingFactor)
		{
			if (cacheKriyaFXOptionsMap != null)
				for (int idx = 0; idx < cacheKriyaFXOptionsMap.Length; idx++)
					if (cacheKriyaFXOptionsMap[idx] != null && cacheKriyaFXOptionsMap[idx].Username == username && cacheKriyaFXOptionsMap[idx].Password == password && cacheKriyaFXOptionsMap[idx].WebSocketUrl == webSocketUrl && cacheKriyaFXOptionsMap[idx].SmoothingFactor == smoothingFactor && cacheKriyaFXOptionsMap[idx].EqualsInput(input))
						return cacheKriyaFXOptionsMap[idx];
			return CacheIndicator<KriyaFXOptionsMap>(new KriyaFXOptionsMap(){ Username = username, Password = password, WebSocketUrl = webSocketUrl, SmoothingFactor = smoothingFactor }, input, ref cacheKriyaFXOptionsMap);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.KriyaFXOptionsMap KriyaFXOptionsMap(string username, string password, string webSocketUrl, double smoothingFactor)
		{
			return indicator.KriyaFXOptionsMap(Input, username, password, webSocketUrl, smoothingFactor);
		}

		public Indicators.KriyaFXOptionsMap KriyaFXOptionsMap(ISeries<double> input , string username, string password, string webSocketUrl, double smoothingFactor)
		{
			return indicator.KriyaFXOptionsMap(input, username, password, webSocketUrl, smoothingFactor);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.KriyaFXOptionsMap KriyaFXOptionsMap(string username, string password, string webSocketUrl, double smoothingFactor)
		{
			return indicator.KriyaFXOptionsMap(Input, username, password, webSocketUrl, smoothingFactor);
		}

		public Indicators.KriyaFXOptionsMap KriyaFXOptionsMap(ISeries<double> input , string username, string password, string webSocketUrl, double smoothingFactor)
		{
			return indicator.KriyaFXOptionsMap(input, username, password, webSocketUrl, smoothingFactor);
		}
	}
}

#endregion
