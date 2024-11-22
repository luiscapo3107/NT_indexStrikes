#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.Data;
using System.Net.Http;
using System.Web.Script.Serialization;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
using System.Net.WebSockets;
using System.Threading;
using System.IO;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class KriyaFXIndexStrikes : Indicator
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

        private double futurePrice;
        private double indexPrice;
        private double currentRatio;

        private long lastUpdateTimestamp;
        private string underlyingSymbol;

        private ClientWebSocket webSocket;
        private CancellationTokenSource cts;
        private List<string> messageChunks = new List<string>();

        private Queue<double> ratioHistory = new Queue<double>();

        protected override void OnStateChange()
        {
            try
            {
                if (State == State.SetDefaults)
                {
                    Description = @"Retrieves and plots Options Data from the KriyaFX service";
                    Name = "KriyaFXIndexStrikes";
                    Calculate = Calculate.OnEachTick;
                    IsOverlay = true;
                    DisplayInDataBox = true;
                    DrawOnPricePanel = true;
                    PaintPriceMarkers = true;
                    ScaleJustification = NinjaTrader.Gui.Chart.ScaleJustification.Right;
                    IsSuspendedWhileInactive = true;
                    SelectedSymbol = "SPY";
                    Username = string.Empty;
                    Password = string.Empty;
                    WebSocketUrl = "ws://kriyafx.de";
                }
                else if (State == State.Configure)
                {
                    httpClient = new HttpClient();
                    jsonSerializer = new JavaScriptSerializer();
                    webSocket = new ClientWebSocket();
                    cts = new CancellationTokenSource();
                }
                else if (State == State.DataLoaded)
                {
                    // Initiate login
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
                }
                else if (State == State.Realtime)
                {
                    // Initiate WebSocket connection
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
                        httpClient.Dispose();
                    DisposeSharpDXResources();
                    DisconnectWebSocket();
                }
            }
            catch (Exception ex)
            {
                Print("Error in OnStateChange: " + ex.Message);
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
                    return true;
                }
                else
                {
                    Print("Login failed. Status code: " + response.StatusCode);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Print("Error during login: " + ex.Message);
                return false;
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

                currentRatio = ratioHistory.Average();
            }
        }

        private void ProcessOptionsData(string json)
        {
            try
            {
                var data = jsonSerializer.Deserialize<Dictionary<string, object>>(json);

                if (!data.ContainsKey("Options"))
                {
                    Print("Options data not found in the response.");
                    return;
                }
                var options = data["Options"] as Dictionary<string, object>;
                if (options == null)
                {
                    Print("Options or Data not found in the response.");
                    return;
                }

                string symbolKey = SelectedSymbol;
                if (!options.ContainsKey(symbolKey))
                {
                    Print("Data for " + symbolKey + " not found in the response.");
                    return;
                }

                var symbolData = options[symbolKey] as Dictionary<string, object>;
                if (symbolData == null || !symbolData.ContainsKey("Data"))
                {
                    Print(symbolKey + " data structure is invalid.");
                    return;
                }

                long currentUpdateTimestamp = 0;
                if (symbolData.ContainsKey("Updated"))
                    currentUpdateTimestamp = Convert.ToInt64(symbolData["Updated"]);

                // Only process the data if the timestamp has changed
                if (currentUpdateTimestamp != lastUpdateTimestamp)
                {
                    lastUpdateTimestamp = currentUpdateTimestamp;
                    if (symbolData.ContainsKey("Symbol"))
                        underlyingSymbol = symbolData["Symbol"].ToString();

                    indexPrice = Convert.ToDouble(symbolData["Price"]);
                    UpdateRatio();

                    var dataJson = jsonSerializer.Serialize(symbolData["Data"]);
                    var optionsData = jsonSerializer.Deserialize<List<Dictionary<string, object>>>(dataJson);

                    strikeLevels.Clear();
                    indexStrikes.Clear();

                    foreach (var item in optionsData)
                    {
                        if (item.ContainsKey("strike"))
                        {
                            double indexStrike = Convert.ToDouble(item["strike"]);
                            double futureStrike = indexStrike * currentRatio;

                            indexStrikes.Add(indexStrike);
                            strikeLevels.Add(futureStrike);
                        }
                    }

                    // Invalidate the chart to trigger a redraw
                    if (ChartControl != null)
                    {
                        ChartControl.Dispatcher.InvokeAsync(() =>
                        {
                            ChartControl.InvalidateVisual();
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Print("Error processing options data: " + ex.Message);
            }
        }

        public override void OnRenderTargetChanged()
        {
            DisposeSharpDXResources();

            if (RenderTarget != null)
            {
                textFormat = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Arial", 12);
                textBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.White);
                strikeLineBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.RoyalBlue);
            }
        }

        private void DisposeSharpDXResources()
        {
            if (textBrush != null) { textBrush.Dispose(); textBrush = null; }
            if (strikeLineBrush != null) { strikeLineBrush.Dispose(); strikeLineBrush = null; }
            if (textFormat != null) { textFormat.Dispose(); textFormat = null; }
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (RenderTarget == null || textFormat == null || textBrush == null || strikeLineBrush == null)
            {
                return;
            }

            PlotStrikeLevels(chartControl, chartScale);
        }

        private void PlotStrikeLevels(ChartControl chartControl, ChartScale chartScale)
        {
            if (strikeLevels.Count == 0)
                return;

            int minCount = Math.Min(strikeLevels.Count, indexStrikes.Count);

            float xStart = ChartPanel.X;
            float xEnd = ChartPanel.X + ChartPanel.W;

            for (int i = 0; i < minCount; i++)
            {
                try
                {
                    double strikeLevel = strikeLevels[i];
                    double indexStrike = indexStrikes[i];

                    float y = chartScale.GetYByValue(strikeLevel);

                    // Draw the line in royal blue color
                    RenderTarget.DrawLine(new SharpDX.Vector2(xStart, y), new SharpDX.Vector2(xEnd, y), strikeLineBrush, 2);

                    // Prepare text for display
                    string strikeText = "Strike: " + Math.Round(indexStrike).ToString() + " (" + Math.Round(strikeLevel).ToString() + ")";

                    using (var strikeTextLayout = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, strikeText, textFormat, float.MaxValue, float.MaxValue))
                    {
                        float x = (float)ChartPanel.X + (float)ChartPanel.W - strikeTextLayout.Metrics.Width - 5;
                        float yStrikeText = y - strikeTextLayout.Metrics.Height / 2;

                        RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, yStrikeText), strikeTextLayout, textBrush);
                    }
                }
                catch (Exception ex)
                {
                    Print("Error plotting strike level " + i + ": " + ex.Message);
                }
            }
        }

        private async Task ConnectWebSocket()
        {
            if (string.IsNullOrEmpty(bearerToken))
            {
                Print("Bearer token is not set. Cannot connect to WebSocket.");
                return;
            }

            try
            {
                var wsUri = new Uri(WebSocketUrl + "?token=" + bearerToken);
                await webSocket.ConnectAsync(wsUri, cts.Token);
                Print("WebSocket connection established");
                Task.Run(() => ReceiveWebSocketMessages());
            }
            catch (Exception ex)
            {
                Print("WebSocket connection failed: " + ex.Message);
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
                var data = jsonSerializer.Deserialize<Dictionary<string, object>>(fullMessage);
                if (data.ContainsKey("type") && data["type"].ToString() == "update")
                {
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
        [Display(Name = "Options Symbol", Description = "Select SPX or SPY options data", Order = 4, GroupName = "Parameters")]
        public string SelectedSymbol { get; set; }
        #endregion

        public override string DisplayName
        {
            get { return "KriyaFXIndexStrikes (" + SelectedSymbol + ")"; }
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
    public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
    {
        private KriyaFXIndexStrikes[] cacheKriyaFXIndexStrikes;
        public KriyaFXIndexStrikes KriyaFXIndexStrikes(string username, string password, string webSocketUrl, string selectedSymbol)
        {
            return KriyaFXIndexStrikes(Input, username, password, webSocketUrl, selectedSymbol);
        }

        public KriyaFXIndexStrikes KriyaFXIndexStrikes(ISeries<double> input, string username, string password, string webSocketUrl, string selectedSymbol)
        {
            if (cacheKriyaFXIndexStrikes != null)
                for (int idx = 0; idx < cacheKriyaFXIndexStrikes.Length; idx++)
                    if (cacheKriyaFXIndexStrikes[idx] != null && cacheKriyaFXIndexStrikes[idx].Username == username && cacheKriyaFXIndexStrikes[idx].Password == password && cacheKriyaFXIndexStrikes[idx].WebSocketUrl == webSocketUrl && cacheKriyaFXIndexStrikes[idx].SelectedSymbol == selectedSymbol && cacheKriyaFXIndexStrikes[idx].EqualsInput(input))
                        return cacheKriyaFXIndexStrikes[idx];
            return CacheIndicator<KriyaFXIndexStrikes>(new KriyaFXIndexStrikes() { Username = username, Password = password, WebSocketUrl = webSocketUrl, SelectedSymbol = selectedSymbol }, input, ref cacheKriyaFXIndexStrikes);
        }
    }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        public Indicators.KriyaFXIndexStrikes KriyaFXIndexStrikes(string username, string password, string webSocketUrl, string selectedSymbol)
        {
            return indicator.KriyaFXIndexStrikes(Input, username, password, webSocketUrl, selectedSymbol);
        }

        public Indicators.KriyaFXKriyaFXIndexStrikes KriyaFXIndexStrikes(ISeries<double> input, string username, string password, string webSocketUrl, string selectedSymbol)
        {
            return indicator.KriyaFXIndexStrikes(input, username, password, webSocketUrl, selectedSymbol);
        }
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.KriyaFXIndexStrikes KriyaFXIndexStrikes(string username, string password, string webSocketUrl, string selectedSymbol)
        {
            return indicator.KriyaFXIndexStrikes(Input, username, password, webSocketUrl, selectedSymbol);
        }

        public Indicators.KriyaFXIndexStrikes KriyaFXIndexStrikes(ISeries<double> input, string username, string password, string webSocketUrl, string selectedSymbol)
        {
            return indicator.KriyaFXIndexStrikes(input, username, password, webSocketUrl, selectedSymbol);
        }
    }
}

#endregion
