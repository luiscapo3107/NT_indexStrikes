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
using System.Windows.Threading;
using System.Web.Script.Serialization; // For JSON Parsing
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
using Brushes = System.Windows.Media.Brushes;
using System.Timers; 
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class IndexStrikesv2 : Indicator
    {
        #region Private Variables

        private HttpClient httpClient;
        private JavaScriptSerializer jsonSerializer;
        private const int STRIKES_ABOVE = 10;
        private const int STRIKES_BELOW = 10;
        private List<double> strikeLevels = new List<double>();
        private List<double> indexStrikes = new List<double>();
        private Timer updateTimer; 

        // Variables for expected move calculation
        private double expectedMoveValue = 0.0;
        private double indexPrice = 0.0;
        private double futurePrice = 0.0;
		private double ratio = 0.0; 

        // SharpDX Resources
        private SharpDX.DirectWrite.TextFormat textFormat;
        private SharpDX.Direct2D1.Brush textBrush;
        private SharpDX.Direct2D1.Brush lineBrush;
        private SharpDX.Direct2D1.Brush strikeLineBrush;

        // Mutex for thread safety
        private object renderLock = new object();

        #endregion

        #region OnStateChange

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                                 = @"Plots the strike and day range levels of the given index in the chart.";
                Name                                        = "IndexStrikesv2";
                Calculate                                   = Calculate.OnBarClose;
                IsOverlay                                   = true;
                DisplayInDataBox                            = false;
                DrawOnPricePanel                            = true;
                PaintPriceMarkers                           = true;
                ScaleJustification                          = NinjaTrader.Gui.Chart.ScaleJustification.Right;
                IsSuspendedWhileInactive                    = true;
                APIToken                                    = string.Empty;
                IndexTicker                                 = string.Empty;
                Username					                = string.Empty;
				Password					                = string.Empty;
            }
            else if (State == State.Configure)
            {
                // Initialize HTTP client and JSON serializer
                httpClient = new HttpClient();
                jsonSerializer = new JavaScriptSerializer();

                //Initialize the timer
                updateTimer = new Timer(600000); // 600000 milliseconds = 10 minutes
                updateTimer.Elapsed += OnTimerElapsed; 
                updateTimer.AutoReset = true; 
            }
            else if (State == State.DataLoaded)
            {
                // Start the timer
                updateTimer.Start(); 
                // Start the async operation without blocking
                CheckMarketStatusAsync();
            }
            else if (State == State.Terminated)
            {
                // Stop and dispose of the timer
                if (updateTimer != null)
                {
                    updateTimer.Stop();
                    updateTimer.Dispose();
                }

			    // Dispose of HTTP client
			    if (httpClient != null)
			        httpClient.Dispose();

			    // Dispose of SharpDX resources
			    DisposeSharpDXResources();
            }
        }

        #endregion
		private void OnTimerElapsed(object sender, ElapsedEventArgs e)
        {
            // This method will be called every x minutes
            CheckMarketStatusAsync();
        }
		public override void OnRenderTargetChanged()
		{
		    // Dispose existing resources
		    DisposeSharpDXResources();

		    if (RenderTarget != null)
		    {
		        // Initialize SharpDX resources
		        textFormat = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Arial", 12);
		        textBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.White);
		        lineBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.LightYellow);
		        strikeLineBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.DodgerBlue);
		    }
		}

		private void DisposeSharpDXResources()
		{
		    if (textBrush != null)
		    {
		        textBrush.Dispose();
		        textBrush = null;
		    }
		    if (lineBrush != null)
		    {
		        lineBrush.Dispose();
		        lineBrush = null;
		    }
		    if (strikeLineBrush != null)
		    {
		        strikeLineBrush.Dispose();
		        strikeLineBrush = null;
		    }
		    if (textFormat != null)
		    {
		        textFormat.Dispose();
		        textFormat = null;
		    }
		}


        #region Async Methods

        private async Task CheckMarketStatusAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(APIToken))
                {
                    Print("API Token is not set.");
                    return;
                }

                string marketStatusUrl = "https://api.marketdata.app/v1/markets/status?token=" + APIToken;
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, marketStatusUrl);
                HttpResponseMessage response = await httpClient.SendAsync(request);

                string content = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    // Assume market is open for testing
                    bool isMarketOpen = true;

                    if (isMarketOpen)
                    {
                        // Start both tasks
                        var indexPriceTask = GetIndexPriceAsync();
                        var expectedMoveTask = GetExpectedMoveAsync();

                        // Wait for both tasks to complete
                        await Task.WhenAll(indexPriceTask, expectedMoveTask);

                        // Once both tasks are done, update the UI
                        Dispatcher.InvokeAsync(() =>
                        {
                            PlotAllLines();
                        });
                    }
                    else
                    {
                        UpdateMarketStatusOnUI(false);
                    }
                }
                else
                {
                    Print("Error checking market status: " + response.ReasonPhrase);
                }
            }
            catch (Exception ex)
            {
                Print("Exception in CheckMarketStatusAsync: " + ex.Message);
            }
        }

        private async Task GetIndexPriceAsync()
        {
            try
            {
                string indexPriceUrl = "https://api.marketdata.app/v1/stocks/quotes/" + IndexTicker + "?token=" + APIToken;
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, indexPriceUrl);

                HttpResponseMessage response = await httpClient.SendAsync(request);
                string content = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    indexPrice = ParseIndexPrice(content);

                    futurePrice = GetCurrentAsk() - TickSize;

                    Print("IndexPrice: " + indexPrice);
                    Print("FuturePrice: " + futurePrice);
					Print("Index to Futures Ratio: " + futurePrice/indexPrice); 
                }
                else
                {
                    Print("Error retrieving index price: " + response.ReasonPhrase);
                }
            }
            catch (Exception ex)
            {
                Print("Exception in GetIndexPriceAsync: " + ex.Message);
            }
        }

        private async Task GetExpectedMoveAsync()
        {
            try
            {
                string optionsChainUrl = "https://api.marketdata.app/v1/options/chain/" + IndexTicker + "?dte=1&delta=0.5&token=" + APIToken;
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, optionsChainUrl);

                HttpResponseMessage response = await httpClient.SendAsync(request);
                string content = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    // Declare variables to hold the last prices
                    double lastPriceCall;
                    double lastPricePut;

                    // Parse the response to get last price of the call and the put
                    ParseOptionsChain(content, out lastPriceCall, out lastPricePut);

                    // Calculate the expected move
                    expectedMoveValue = ((lastPriceCall + lastPricePut) * 0.85)/2;

                    Print("Expected Move Value: " + expectedMoveValue);
                }
                else
                {
                    Print("Error retrieving options chain: " + response.ReasonPhrase);
                }
            }
            catch (Exception ex)
            {
                Print("Exception in GetExpectedMoveAsync: " + ex.Message);
            }
        }

        #endregion

        #region Parsing Methods

        private double ParseIndexPrice(string json)
        {
            // Deserialize the JSON into a dynamic object
            var obj = jsonSerializer.Deserialize<dynamic>(json);

            // Check if the response status is "ok"
            if ((string)obj["s"] != "ok")
            {
                throw new Exception("API response indicates failure.");
            }

            // Access the "last" array and get the first element
            var lastArray = obj["last"] as object[];
            if (lastArray == null || lastArray.Length == 0)
            {
                throw new Exception("No price data available.");
            }

            double indexPrice = Convert.ToDouble(lastArray[0]);
            return indexPrice;
        }

        private void ParseOptionsChain(string json, out double lastPriceCall, out double lastPricePut)
        {
            // Deserialize the JSON into a dynamic object
            var obj = jsonSerializer.Deserialize<dynamic>(json);

            // Check if the response status is "ok"
            if ((string)obj["s"] != "ok")
            {
                throw new Exception("API response indicates failure.");
            }

            // Access the "last" array
            var lastArray = obj["last"] as object[];
            // Access the "side" array
            var sideArray = obj["side"] as object[];

            // Initialize the output variables
            lastPriceCall = 0.0;
            lastPricePut = 0.0;

            for (int i = 0; i < sideArray.Length; i++)
            {
                string side = (string)sideArray[i];
                double lastPrice = Convert.ToDouble(lastArray[i]);

                if (side == "call")
                {
                    lastPriceCall = lastPrice;
                }
                else if (side == "put")
                {
                    lastPricePut = lastPrice;
                }
            }
        }

        #endregion

        #region Update Methods

        private void PlotAllLines()
        {
            // First, update the strike levels
            UpdateStrikeLevels(indexPrice, futurePrice);

            // Invalidate the chart to trigger OnRender
            if (ChartControl != null)
                ChartControl.InvalidateVisual();
        }

        private void UpdateStrikeLevels(double indexPrice, double futurePrice)
        {
            try
            {
                // Check if there are any bars loaded
                if (Count < 1)
                {
                    Print("No bars loaded. Exiting UpdateStrikeLevels.");
                    return;
                }

                // Clear previous strike levels
                strikeLevels.Clear();
                indexStrikes.Clear();

                ratio = futurePrice / indexPrice;
                double roundedIndexStrike = Math.Floor(indexPrice);

                // Generate strike levels
                for (int i = -STRIKES_BELOW; i <= STRIKES_ABOVE; i++)
                {
                    double indexStrike = roundedIndexStrike + i;
                    double strikeLevel = indexStrike * ratio;
                    strikeLevels.Add(strikeLevel);
                    indexStrikes.Add(indexStrike);
                }

                Print("Strike levels updated.");
            }
            catch (Exception ex)
            {
                Print("Exception in UpdateStrikeLevels: " + ex.Message);
            }
        }

        private void UpdateMarketStatusOnUI(bool isMarketOpen)
        {
            // Update the UI to reflect the market status
            if (!isMarketOpen)
            {
                // Clear any existing strike levels
                strikeLevels.Clear();
                indexStrikes.Clear();

                // Remove any drawn objects from the chart
                RemoveDrawObjects();

                // Optionally, display a message on the chart
                Draw.TextFixed(this, "MarketClosed", "Market is closed", TextPosition.Center, Brushes.Red, new Gui.Tools.SimpleFont("Arial", 24), Brushes.Transparent, Brushes.Transparent, 0);
            }
        }

        #endregion

        #region OnRender Method

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            lock (renderLock)
            {
                // Ensure we have a RenderTarget
                if (RenderTarget == null)
                    return;

                // Ensure SharpDX resources are available
                if (textFormat == null || textBrush == null || lineBrush == null || strikeLineBrush == null)
                    return;

                // Plot the expected move lines and text
                PlotExpectedMoveLines(chartControl, chartScale);

                // Plot the strike levels and their text
                PlotStrikeLevels(chartControl, chartScale);
            }
        }

		private void PlotExpectedMoveLines(ChartControl chartControl, ChartScale chartScale)
		{
		    Print("PlotExpectedMoveLines called. expectedMoveValue: " + expectedMoveValue + ", indexPrice: " + indexPrice);

		    if (expectedMoveValue <= 0 || indexPrice <= 0)
		    {
		        Print("Invalid expectedMoveValue or indexPrice. Returning early.");
		        return;
		    }

		    double maxMovePrice = (indexPrice + expectedMoveValue) * ratio;
		    double minMovePrice = (indexPrice - expectedMoveValue) * ratio;

		    float yMax = chartScale.GetYByValue(maxMovePrice);
		    float yMin = chartScale.GetYByValue(minMovePrice);

		    float xStart = ChartPanel.X; 
		    float xEnd = ChartPanel.X + ChartPanel.W; 

		    Print("Line coordinates - xStart: " + xStart + ", xEnd: " + xEnd + ", yMax: " + yMax + ", yMin: " + yMin);

		    if (RenderTarget == null)
		    {
		        Print("RenderTarget is null. Cannot draw lines.");
		        return;
		    }
			
			// Create a pale yellow color with transparency
			SharpDX.Color paleYellowColor = new SharpDX.Color(255,255,224, 128); //RGBA pale yellow with 50% opacity
			
			// Create a transparent yellow color for rectangles
			SharpDX.Color transparentYellowColor = new SharpDX.Color(255,255,0,10); //RGBA yellow wiht 4% opacity
			
		    // Change the color to a more visible one
		    using (var paleYellowBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, paleYellowColor))
			using (var transparentYellowBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, transparentYellowColor))
		    {
		        try
		        {
		            // Ensure y-coordinates are within the chart panel
		            yMax = Math.Max(ChartPanel.Y, Math.Min(yMax, ChartPanel.Y + ChartPanel.H));
		            yMin = Math.Max(ChartPanel.Y, Math.Min(yMin, ChartPanel.Y + ChartPanel.H));
					
					// Calculate rectangle coordinates
					float yMaxTop = chartScale.GetYByValue(maxMovePrice + 2); 
					float yMaxBottom = chartScale.GetYByValue(maxMovePrice - 2); 
					float yMinTop = chartScale.GetYByValue(minMovePrice + 2); 
					float yMinBottom = chartScale.GetYByValue(minMovePrice - 2); 
					
					// Draw rectangles
					RenderTarget.FillRectangle(
						new SharpDX.RectangleF(xStart, yMaxTop, xEnd - xStart, yMaxBottom - yMaxTop), 
						transparentYellowBrush
					); 
					RenderTarget.FillRectangle(
						new SharpDX.RectangleF(xStart, yMinTop, xEnd - xStart, yMinBottom - yMinTop), 
						transparentYellowBrush
					); 

		            // Draw the max move line with increased thickness
		            RenderTarget.DrawLine(new SharpDX.Vector2(xStart, yMax), new SharpDX.Vector2(xEnd, yMax), paleYellowBrush, 2);
		            Print("Max move line drawn at y = " + yMax);

		            // Draw the min move line with increased thickness
		            RenderTarget.DrawLine(new SharpDX.Vector2(xStart, yMin), new SharpDX.Vector2(xEnd, yMin), paleYellowBrush, 2);
		            Print("Min move line drawn at y = " + yMin);

		            // Draw the text labels
		            //DrawTextLabel("Max 1 Day Move", yMax - 15);
		            //DrawTextLabel("Min 1 Day Move", yMin + 5);
		            //Print("Text labels drawn");
		        }
		        catch (Exception ex)
		        {
		            Print("Exception in PlotExpectedMoveLines: " + ex.Message);
		        }
		    }

		    Print("PlotExpectedMoveLines completed.");
		}

        private void PlotStrikeLevels(ChartControl chartControl, ChartScale chartScale)

        {
            // Ensure we have strike levels to draw
            if (strikeLevels == null || strikeLevels.Count == 0)
                return;

            // Ensure indexStrikes list has the same count as strikeLevels
            if (indexStrikes == null || indexStrikes.Count != strikeLevels.Count)
                return;

            // Get the X coordinates for the start and end of the lines
            //float xStart = chartControl.GetXByTime(ChartBars.GetTimeByBarIdx(chartControl, ChartBars.FromIndex));
            //float xEnd = chartControl.GetXByTime(ChartBars.GetTimeByBarIdx(chartControl, ChartBars.ToIndex));
			float xStart = ChartPanel.X; 
			float xEnd = ChartPanel.X + ChartPanel.W; 

			
            for (int i = 0; i < strikeLevels.Count; i++)
            {
                double strikeLevel = strikeLevels[i];
                double indexStrike = indexStrikes[i];

                // Convert the price (Y-axis value) to a pixel coordinate
                float y = chartScale.GetYByValue(strikeLevel);

                // Draw the strike line
                RenderTarget.DrawLine(new SharpDX.Vector2(xStart, y), new SharpDX.Vector2(xEnd, y), strikeLineBrush, 1);
				
				// Draw rectangle around the strike level
       			string rectangleTag = IndexTicker + ": RegionHighlight " + indexStrike.ToString();

		        // Set area opacity to 4% (areaOpacity ranges from 0 to 255 in SharpDX)
		        byte areaOpacity = 10; // 4% of 255

		        // Create a semi-transparent brush for the rectangle
		        using (var areaBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color((byte)0, (byte)191, (byte)255, areaOpacity))) // DodgerBlue with opacity
		        {
		            // Calculate rectangle coordinates
		            float yTop = chartScale.GetYByValue(strikeLevel + 2);
		            float yBottom = chartScale.GetYByValue(strikeLevel - 2);

		            // Draw the rectangle
		            RenderTarget.FillRectangle(
		                new SharpDX.RectangleF(xStart, yTop, xEnd - xStart, yBottom - yTop),
		                areaBrush
		            );
		        }

                // Define the text to display
                string text = "Strike: " + indexStrike;

                // Create a TextLayout to measure the text size
                using (var textLayout = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, text, textFormat, float.MaxValue, float.MaxValue))
                {
                    // Get the width and height of the text
                    float textWidth = textLayout.Metrics.Width;
                    float textHeight = textLayout.Metrics.Height;

                    // Calculate X-coordinate: right edge minus text width minus padding
                    float x = (float)ChartPanel.X + (float)ChartPanel.W - textWidth - 10; // 10 pixels padding from the right edge

                    // Adjust Y-coordinate to center the text vertically at the adjusted y
                    float yText = y - textHeight / 2 - 4; // Slightly above the line

                    // Draw the text
                    RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, yText), textLayout, textBrush);
                }
            }
        }

        private void DrawTextLabel(string text, float yPosition)
        {
            // Create a TextLayout to measure the text size
            using (var textLayout = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, text, textFormat, float.MaxValue, float.MaxValue))
            {
                // Get the width and height of the text
                float textWidth = textLayout.Metrics.Width;
                float textHeight = textLayout.Metrics.Height;

                // Calculate X-coordinate: right edge minus text width minus padding
                float x = (float)ChartPanel.X + (float)ChartPanel.W - textWidth - 10; // 10 pixels padding from the right edge

                // Draw the text
                RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, yPosition), textLayout, textBrush);
            }
        }

        #endregion

        #region Properties

        [NinjaScriptProperty]
        [Display(Name = "APIToken", Description = "MarketData.app API token", Order = 1, GroupName = "Parameters")]
        public string APIToken
        { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "IndexTicker", Description = "Index Ticker Symbol", Order = 2, GroupName = "Parameters")]
        public string IndexTicker
        { get; set; }

        #endregion

    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private IndexStrikesv2[] cacheIndexStrikesv2;
		public IndexStrikesv2 IndexStrikesv2(string aPIToken, string indexTicker)
		{
			return IndexStrikesv2(Input, aPIToken, indexTicker);
		}

		public IndexStrikesv2 IndexStrikesv2(ISeries<double> input, string aPIToken, string indexTicker)
		{
			if (cacheIndexStrikesv2 != null)
				for (int idx = 0; idx < cacheIndexStrikesv2.Length; idx++)
					if (cacheIndexStrikesv2[idx] != null && cacheIndexStrikesv2[idx].APIToken == aPIToken && cacheIndexStrikesv2[idx].IndexTicker == indexTicker && cacheIndexStrikesv2[idx].EqualsInput(input))
						return cacheIndexStrikesv2[idx];
			return CacheIndicator<IndexStrikesv2>(new IndexStrikesv2(){ APIToken = aPIToken, IndexTicker = indexTicker }, input, ref cacheIndexStrikesv2);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.IndexStrikesv2 IndexStrikesv2(string aPIToken, string indexTicker)
		{
			return indicator.IndexStrikesv2(Input, aPIToken, indexTicker);
		}

		public Indicators.IndexStrikesv2 IndexStrikesv2(ISeries<double> input , string aPIToken, string indexTicker)
		{
			return indicator.IndexStrikesv2(input, aPIToken, indexTicker);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.IndexStrikesv2 IndexStrikesv2(string aPIToken, string indexTicker)
		{
			return indicator.IndexStrikesv2(Input, aPIToken, indexTicker);
		}

		public Indicators.IndexStrikesv2 IndexStrikesv2(ISeries<double> input , string aPIToken, string indexTicker)
		{
			return indicator.IndexStrikesv2(input, aPIToken, indexTicker);
		}
	}
}

#endregion
